# ADR 0012 — пассивный Host decode/catalog

Статус: E16; software Windows verdict в PR, hardware gates открыты.

Class: Host LantableRegistry.DecodeSignals и read-only capture cache
(`icem-protocols/src/ace/host.rs`, `icem-platform-windows/src/sniffer_catalog.rs`).
Inv: Host offset/type/factor/sentinel, атомарный vector cache, bounded cardinality;
original C# oracle + pinned PCAP + capture fault/resource tests.
N+1: новый wire type — enum/exhaustive decode arm/oracle case; Capture lifecycle,
SetLayer toggle, MMF и core не приобретают protocol conditionals.

## Фактический legacy

`Plugins.Icem/LantableRegistry` — отдельный decoder от `Emule.Core/SignalWireCodec`.
Сохраняются frame[24+ByteOffset], фиксированные widths, scaling также для FF,
scaled sentinel abs(value+9999)<0.001, NaN/Infinity, ASCII NUL/замена high bytes на ?,
HX как text 0xXXXXXXXX. Регистр ValueType значим. Out-of-frame declared span даёт
null/unavailable; fixed scalar, не помещающийся в frame, прерывает decode vector.
Текст/unavailable в legacy numeric preview = 0, sentinel preview = -9999.

Host загружает только lantable_registry.json, без firmware merge. DTO/first-wins
разбор переиспользует существующий Registry через from_json; существующий file
loader использует тот же load_value. D22 не исправляется: UTF-8/BOM, ограничения
malformed DTO и Unicode comparison явно остаются. UnitClassId не используется
decoder и пока не входит в Rust SignalDef; metadata parity этого поля не заявлена.

`AceSnifferService` хранит signals по SV+name, независимо от equipment; последний
кадр любого equipment обновляет общий SV cache. Повторяющиеся names — last wins.
Equipment хранит первый MAC и signed i32 equipment projection. VectorId выдаётся
при первом наблюдении, включая unknown; vectors сортируются по ID. Decode failure
не публикует частичный cache. Host event AceFrameDecoded имеет callers только в
tests; production subscriber/writer в текущем wiring отсутствует. Новый bus writer
или универсальный callback/event bus не добавлен.

## Ownership и активный путь

Catalog принадлежит существующему Capture на Host service thread. Poll имеет прежние
64 packets; pause drains/discards без decode, stop/fault/reconnect не создают другого
owner. Owned last-good cache и counters сохраняются, как capture state. Collector
не открывает сеть, COM, IPC или файл записи. Existing file/screen logger получает
witness через take_witness: первый после 1 s, затем максимум раз в 60 s. ON/OFF,
rotation, clear/path ownership E12 остаются у logger. Frozen REST/proto/web прежние;
dormant catalog/preview/file endpoints не активируются. Rust read-only snapshot API
доступен тестам/будущему consumer, не объявляется новым external API.

Registry читается из EXE-adjacent templates. Ошибка catalog явно записывается в
существующую диагностику; capture/counters продолжаются, decode не симулируется.
Нет автоматического retry catalog load или изменения INI/Data.dat. MSI inventory
получает один исходный Host lantable JSON; прежний harvester/ownership/Repair
механизм сам включает его в payload, hash и восстановление. SDK gate не обходится.

## Bounds и deliberate deltas

- File read: take(2 MiB + 1), затем UTF-8/JSON; metadata race не позволяет прочитать
  бесконечно растущий файл. Programmatic constructor также ограничивает text budget.
- Максимум 256 catalog vectors, 128 signals на vector, 16384 signals суммарно;
  pinned Host = 194 unique SV (213 entries до first-wins), max 43 signals на vector.
- Максимум 256 observed vectors и 128 equipment, до 256 seen SV на equipment;
  overflow сохраняет existing entries, продолжает counters/decode, поднимает
  tracking_limit_reached. Это lower-bound inventory, не полный список после overflow.
- Scalar decode checked: отрицательные/overflow spans не вызывают panic. Malformed
  spans вне valid traffic могут отличаться от C# unchecked arithmetic — explicit
  safety delta. Declared width меньше fixed width сохраняет фактическое C# чтение,
  если fixed scalar помещается в frame; его нельзя заменить emulator width policy.
- Rate rotation использует injected monotonic clock, с legacy first-window reset;
  rate/age не являются доказательством packet timestamps/аппаратного pacing.

Новых dependencies/unsafe нет; выбранный serde_json остаётся в существующем
emulation crate и Host test closure. Frozen models/proto/AddIn/.NET production не
меняются. Common parser extraction не является новым loader compatibility claim.

## Evidence

Windows oracle компилирует неизменённый Host LantableRegistry, вызывает его Load и
DecodeSignals. Сравниваются 194 definitions, четыре patterns на каждую, type/factor/
sentinel/bounds и первые реальные frame шести SV из pinned PCAP. Values сравниваются
по IEEE bits (NaN classification), text/null/sentinel exact; затем кадры проходят
Rust catalog/cache. Отчёт host-sniffer-differential-report.json входит в прежний
Actions evidence artifact. Oracle имеет 120 s process deadline и 16 MiB output cap.

Actual Npcap/NIC, adapter loss/driver hang, latency, real AddIn/MMF, UI/DPI/session,
SDK-built MSI, reboot/rollback/offline и payload measurement остаются отдельными
NOT_RUN gates. Tests на injected capture не заменяют живую сеть.

## Valid JSON decimal precision

Windows oracle обнаружил разницу 1 ULP на valid catalog factor. serde_json 1.0.154
по умолчанию использует best-effort float parsing. Включён штатный float_roundtrip
в existing icem-emulation dependency. Versions/checksums/lockfile прежние; feature
не добавляет dependencies (MIT OR Apache-2.0), не вводит unsafe в application code.
Cargo feature unification действует на общий serde_json parser workspace: это явное
изменение precision корректных JSON numbers, не только Host. Float parsing дороже
примерно вдвое по upstream; catalog read ограничен 2 MiB и не выполняется UI thread
или per-packet decoder. Existing differential/web/config tests остаются обязательными.

Regression сравнивает длинные catalog decimals с standard f64 parse. Oracle теперь
передаёт также factor IEEE bits, чтобы его JSON transport не повторял округление
проверяемого loader. Коэффициенты/значения сравниваются exact, NaN — classification.
D22 malformed DTO/encoding/Unicode limitations не закрыты и не объявлены parity.
Источник: https://github.com/serde-rs/json/blob/v1.0.154/Cargo.toml#L64-L71
