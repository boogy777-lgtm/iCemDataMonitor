# ADR 0014 — типизированный application API emulator

Class: 21 операторская команда emulator (`icem-emulation::control`); Host RPC вне класса.
Inv: один exhaustive execution match и один Engine owner; полнота COMMANDS/typed/v1
проверяется тестом. N+1: вариант Command, v1 adapter и acceptance case; transport,
Host и core не получают ветвей по предметным командам.

Class: исход и сохранение мутации (`runtime::execute_application`, app Service).
Inv: отказ до эффекта отличим от применения/partial effect/save failure; injected-save
и native process tests. N+1: существующий settings_revision задаёт необходимость save;
нового persistence manager/rollback/повторного исполнения нет.

Class: клиентская emulator pipe session (`icem-transport::emulator_control`), не Host.
Inv: connect/write/ACK имеют один deadline; status frames bounded, idle wait и cancel
конечны; потерянный ответ мутации даёт Unknown без replay. N+1: CLI/библиотека
используют один client_session; server watch_tick и engine не дублируются.

## Решение и граница совместимости

Application API — `Command → Execution` в существующем control module. Legacy
`EmuControl` остаётся узким backend-портом Engine, а `parse_line` только адаптирует
text/JSON v1. Service вызывает один `Engine::execute_application`, под тем же mutex
задаёт clock, выполняет операцию и нужное сохранение. Второго engine/command bus,
plugin framework, общего Host/emulator manager, HTTP listener или процесса нет.

GPUI создаёт Command и отправляет его в прежний bounded Bridge. UiPort имеет отдельный
метод emulator(Command), а презентационные команды Host/logs сохраняют свой путь.
Execution преобразуется в owned presentation Value без stringify/parse; metadata
application доступна только внутри процесса. UI не исполняет blocking I/O. Очереди
остаются 16 requests + 16 replies + одна in-flight работа. E14 Drop отбрасывает очередь;
WorkerCompletion после UI loop завершает уже начатую операцию. X/Open/window/tray и
mapping 176 + 6 не меняются; visual parity этим не доказывается.

**Внешний pipe v1 не меняется.** Сохраняются envelope, proto=1, command names,
operation data, coercions, watch ACK/status и Quit drain. В частности, v1 ok=true
при save failure сохраняет историческое подтверждение применения в памяти. Такой
ответ не является доказательством сохранения на диск. Новая metadata не добавляется
в pipe; внешний v2 не вводится. Достоверные новые исходы доступны внутреннему API/UI;
старый внешний клиент не может получить persistence guarantee из v1.

CLI использует тот же общий client_session, а не собственный watch loop. JSON и text
watch обе формы читают ACK и последовательные status. Ctrl+C watch закрывает текущую
сессию и возвращает 0; это не команда Quit engine. Callback/cancel token доступны
библиотечному клиенту. Compatibility request() возвращает только первый ответ,
включая watch ACK, но использует тот же consumer. Никаких автоматических повторов
accepted commands, process launch, reconnect или status generator у клиента нет.

CLI stdout состоит из JSON lines. Server envelopes выводятся без изменения. Локальные
client errors сериализуются прежним failure envelope, operator error text включает
машинную категорию и исход; typed ClientError предоставляет их отдельными полями.
Exit: 0 — successful command / явная отмена watch; 1 — server/client/protocol/timeout
error; 2 — usage. Раньше Rust относил transport errors к 2; теперь они отделены от
usage и соответствуют фактическому legacy CLI. Это явное исправление клиента, не
изменение server grammar. Broken stdout завершает consumer без panic/replay.

## Предметные типы и числовая политика

ChannelName нельзя создать из произвольной строки публичным API: typed caller выбирает
Channel::Com/Lan. Adapter сохраняет v1 spelling для backend traces. Scope — оба канала
или typed channel; неизвестные legacy variable scopes сохраняются только адаптером
и по-прежнему не совпадают с каналами. Equipment — u32, preset — existing Preset,
firmware/emulation — bool, SV — Option<u32>. Group/unit остаются domain strings с
существующим backend validation; необычные единицы не нормализуются новым API.

NativeValue::finite принимает только finite f64, включая signed zero. Только закрытый
adapter constructor принимает legacy NaN/Infinity и точные IEEE bits. В частности,
setfirmware 2/NaN → true, setequipment/setpreset 2.9 → 2, NaN → 0 остаются v1
characterization cases. Нет молчаливого ужесточения внешнего parser.

Typed text payload ограничен 65536 bytes до помещения в UI queue и перед execution.
V1 wire request cap остаётся 65536 bytes с framing; reply — 8 MiB с framing. Память
consumer — один bounded frame и parser-owned Value, без накопления status history.
Server resources (32 sessions, write 3 s, Quit drain 1.5 s) прежние. CLI whole
connect/write/ACK deadline 3 s, watch idle data deadline 3 s. При постоянно активном
watch общий срок жизни не ограничен; каждое ожидание ограничено и отменяемо.

## Исходы

| Outcome | Гарантия |
|---|---|
| Completed | Read/ACK/help завершён; нет новой гарантии persistence |
| Rejected | Запрос отклонён до применения |
| Applied | Мутация применена; если engine потребовал save, save вернулся успешно |
| AppliedNotPersisted | Состояние применено; требуемый save вернул ошибку; rollback отсутствует |
| AppliedWithError | Ошибка после эффекта, например старый endpoint уже закрыт или COM override изменён до LAN ambiguity |
| Unknown | Client потерял ответ/получил timeout после попытки отправить мутацию; эффект нельзя установить |

Execution содержит отдельные category/operator message и frozen v1 result. Native
filesystem/driver call нельзя надёжно прервать Rust deadline; UI остаётся отзывчивым,
client wait конечен, started effects завершаются у прежнего владельца. Никакой
выдуманной atomicity/durability или гарантии wall-clock join при hung OS нет.
Периодические status/vars не повторяют failed save. Следующая явная settings mutation
может сохранить текущее совокупное состояние обычным existing механизмом.

## Dependencies и проверки

Новых packages/versions нет. Добавлены workspace edge desktop→emulation и прямой
доступ transport к уже locked serde_json 1.0.154 (MIT OR Apache-2.0), default std.
Tokio signal feature только на Windows нужен CLI Ctrl+C (существующие windows-sys и
signal-hook-registry уже в graph); handwritten FFI/unsafe не добавлены. Global graph
и pin 1.99.1/Edition2024/resolver3, GPUI0.2.2/gpui-tray0.1.4 и lint guards сохранены.

Software evidence: 21 typed/text execution cases, original C# parser differential,
finite/IEEE/coercions, save failure/read-no-retry, typed UI/bridge completion, bounded
framing/ACK/status/cancel/timeout/disconnect и Windows engine/CLI/singleton/pipe tests.
Результаты exact head фиксируются в PR. Физический Ctrl+C/focus/DPI/session, emulator
X/Open ×3 с drafts/scroll/selection, COM/Npcap/AddIn и SDK release не заменяются CI.
Selftest/landiag — следующий инкремент; selfTest:null сохраняется как у legacy.
