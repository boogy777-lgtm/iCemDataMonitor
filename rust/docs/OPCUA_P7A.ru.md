# P7a: read-only OPC UA consumer

Отдельный `apps/host/src/opcua_consumer/` использует настоящий OPC UA Binary/TCP
server FreeOpcUa async-opcua 0.19.0. В Host добавлены только optional typed config
и registration `opcua` в existing lifecycle registry. Reader/core, MQTT, Modbus,
Web, AddIn и frozen INI не менялись. Acquisition остаётся единственным, 200 мс;
клиентские Read/sampling/subscriptions не имеют доступа к MMF/source.

## Запуск и профиль

Из `rust/`, exact Rust 1.99.1:

```text
cargo build -p icem-host-app --locked --offline
./target/debug/icem-host-dev --modbus-fixture tests/fixtures/bus-v5-minimal.bin --bind 127.0.0.1:1502 --duration-ms 10000 --opcua-config docs/examples/opcua.json
```

Endpoint `opc.tcp://127.0.0.1:4840/`. Namespace URI `urn:icem:bus:v1`;
клиент разрешает namespace index по URI. `Objects/Bus` содержит канонические
`var{first_register_address}` и metadata nodes. Отсутствие `--opcua-config` отключает
server. Конфигурация startup-only JSON, максимум 16 KiB, все три поля обязательны;
unknown/duplicate/null/wrong-type fields отклоняются с config exit2 до acquisition.
`bind` — numeric loopback IPv4/IPv6 socket, port1..65535;
`profile` — только точная строка `loopback-anonymous-none`;
`update_interval_ms` — 50..60000. Повторный CLI option отклоняется.

Это явный локальный test/development профиль: anonymous, SecurityPolicy None,
MessageSecurityMode None. Secure certificate/authentication configuration и remote
binding не реализованы; удалённый адрес всегда отклоняется. Никаких sample keys
не генерируется. SDK может создавать собственные default PKI directories; этот
профиль не использует сертификаты для защиты соединения. Новые license obligations
MPL-2.0 относятся к SDK: сохранять notices/source obligations при распространении;
upstream SDK sources здесь не модифицированы.

## Модель, качество и время

| Node | UA Variant/DataType |
|---|---|
| var0..var3 | UInt16 |
| var4 (registers4–5) | Int32, исходный COM Unix seconds |
| var6,var8,…,var598 | Float (binary32), исходные IEEE bits, включая NaN payload |
| var600..varN−1 | UInt16, каждый reserve word |

Имена/NodeIds создаются один раз при первом successful snapshot; inactive slots
сохраняют identity и value. До первого capture доступны metadata, var nodes ещё
не существуют. Geometry фиксируется первым кадром; её изменение fault'ит worker
и требует restart, без перемешивания прежнего и нового layout. Nodes read-only;
Write отклоняется SDK. Optional dictionary не реализован.

Один address-space write guard покрывает создание первых var nodes, полный update
metadata/values и notification sampling. SDK SimpleNodeManager берёт один read guard
на batch Read этих Bus nodes; клиент не увидит промежуточный half-updated model.
Это одна synchronized memory copy, **не** единая синхронная transaction измерения
COM/LAN/RAW. Отдельные Read-запросы, разные subscriptions и notification queues могут
относиться к разным кадрам. `capture_generation` — local successful physical capture
sequence, не producer frame sequence; при outage он не увеличивается.

Base quality: до capture `BadWaitingForInitialData`; retained/failed attempt с
last good body — `UncertainLastUsableValue`; successful capture с неизвестным
monotonic capture/completion age или возрастом >=5s —
`UncertainNoCommunicationLastUsableValue`; иначе `Good`. Для COM4..199,
LAN200..399, RAW400..599 при base Good и raw owning-zone `producer_active=false`
используется `BadOutOfService`. Service0..3 и reserve используют base quality.
Base non-Good имеет приоритет над active flag. Last good values сохраняются при
outage/inactive; quality относится к пригодности их текущего получения.

Good означает свежую физическую копию и active producer flag для source variables,
а не доказательство нового/свежего upstream measurement. Writer tick age не
превращается в quality threshold: COM/LAN writer clocks меняются только при
изменении pair bits, поэтому неизменный здоровый сигнал может иметь старый writer
time. RAW ticks связаны с nonempty line. `quality_scope` явно фиксирует этот предел;
legacy D09 `Fresh` не используется. Metadata flags/ticks/counters доступны независимо
для клиентской domain-specific health policy.

Все Value DataValues имеют **отсутствующий SourceTimestamp**. COM var4 остаётся
сырым i32; он никогда не назначается LAN/RAW time. Точное upstream measurement time
неизвестно. ServerTimestamp — UTC локального получения latest publication адаптером
при model update, в том числе при повторе того же кадра, не source measurement.
`time_origin` описывает provenance. Никакой timestamp не обновляет исходные поля.

Metadata nodes: `capture_generation` UInt64; `capture_age_ms`/`completion_age_ms`
UInt64 — monotonic ages на момент model update (нулевое placeholder значение имеет
BadWaitingForInitialData, если observation отсутствует); `capture_status` String
initial/captured/retained/failed; `source_error` String none либо имя SourceError;
`quality_scope`/`time_origin` String. Для каждого COM/LAN/RAW:
`{ZONE}_writer_ticks` Int64 — .NET UTC ticks100ns since0001,
`{ZONE}_producer_active` Boolean и `{ZONE}_write_count` UInt32 (wrapping).
Ticks — AddIn zone writer last-write, не measurement time. Counters не packet count.

## Границы ресурсов и lifecycle

Application хранит один latest PublishedSnapshot/Arc во время update, stable NodeIds
и один текущий typed address-space image; нет backlog копий Snapshot или per-tick
var-name arrays. SDK/address-space/subscriptions выделяют свою ограниченную память,
zero-allocation/RSS/performance guarantee не заявляется. Maximum Bus count65535
ограничивает число var nodes (count−298). Source body в core не копируется consumer'ом.

SDK limits: sessions4; message1MiB; chunks16; send/receive buffers64KiB;
array65535; string4096; bytestring64KiB; browse continuation points4;
history/query continuation1; Read nodes1024/call; Browse64 nodes/call и1024refs/node;
monitored items1024/call,1024/subscription; subscriptions4/session; itemqueue2;
pendingPublish8/session и2/subscription; queuedNotifications2048/subscription;
notifications1024/publish; sampling/publishing минимум100ms; keepalive<=100,
lifetime<=300; hello timeout2s. Client queue requests ревизуются SDK до bounds.
Limits не обещают fixed OS socket/runtime memory: SDK ещё не имеет hard cap на
pre-session TCP connections; loopback-only profile не является remote hardened server.

Bind выполняется реально до регистрации; busy port — startup error. SDK server
создаётся внутри отдельного current-thread runtime owner. Running означает bound,
built, завершённую SDK node-manager initialization и живой worker; protocol/client failures не обновляют source data.
Worker termination без cancellation — Faulted. Cancel flag проверяется heartbeat25ms;
SDK cancellation получает500ms, затем runtime уничтожается с owned tasks. Registry
stop использует общий caller deadline, timeout сохраняет owner для repeated stop,
не задерживая cancellation других consumers. Cooperative scheduling не hard realtime;
slow/unresponsive client не блокирует acquisition. Shutdown не обещает drain delivery.

## Проверки и открытые границы

Linux exact Rust1.99.1, locked/offline: actual SDK client/server TCP session проверяет
namespace lookup, browse stable IDs, read-only Write rejection, UInt16/Int32/Float,
NaN payload7fc01234, SourceTimestamp absence/ServerTimestamp presence, subscriptions,
revised queue bound2, COM inactive при изменяющемся LAN/RAW и fixed COM time,
retained values/UncertainLastUsableValue при source failure, bind failure, idle TCP
cancellation и repeated deadline stop. Strict config и injected monotonic quality
expiry покрыты отдельным тестом. Host tests/fmt/build/clippy результаты — execution
journal. Самостоятельный Windows client, Windows MMF outage, exact-head Windows
compile/runtime, production authentication, MSI и hardware acceptance остаются OPEN.
OPC DA sidecar — отдельный этап7b, в этом increment отсутствует.

## Dependency audit

Pinned async-opcua=0.19.0: MPL-2.0, selected `server` включает generated core namespace;
`client` — dev-only actual interop tests. Default features empty; XML/JSON/discovery
registration отключены. SDK не заявляет MSRV; acceptance — compile на exact1.99.1.
Прямые опубликованные SDK crates server/types/crypto/core/nodes/macros/core-namespace
не содержат unsafe blocks; application `forbid(unsafe_code)` не ослаблен. RustCrypto
вместо OpenSSL. Новая resolved closure в Cargo.lock включает MPL-2.0 SDK и
MIT/Apache-2.0 transitive crates (byteorder: Unlicense OR MIT; flagset/gethostname:
Apache-2.0; tracing-futures/unsafe-libyaml: MIT). Highest declared MSRV among new
packages1.85. SDK config dependency serde_yaml deprecated тянет unsafe-libyaml;
приложение не принимает YAML и не вызывает SDK config-file parser. Arc-swap,
runtime/allocator/crypto/platform transitive crates имеют свои unsafe internals,
это внешний dependency boundary, не обещание whole-process safe Rust. Версии и
checksums зафиксированы lockfile; future secure/remote adoption требует отдельного audit.

Upstream primary sources: https://github.com/FreeOpcUa/async-opcua/releases/tag/0.19.0,
https://docs.rs/crate/async-opcua/0.19.0 и published0.19.0 sources в Cargo registry.

Отдельный independent-client gate использует Python `asyncua==2.1.0` (optional dev
installation), не SDK сервера:

```text
python rust/tools/acceptance/nplus1_opcua_client.py --host rust/target/debug/icem-host-dev --fixture rust/tests/fixtures/bus-v5-minimal.bin --output result.json
```

Из repository root; запускаются только свои temporary fixture/config/Host processes
на loopback ephemeral ports. Linux результат:
[asyncua2.1.0 → Rust server](perf/nplus1-p7a-asyncua-linux.json), browse368, типы,
NaN payload/negative zero, raw COMtime, writer metadata, honest inactive LAN quality,
активный RAW Good, timestamps, write rejection, initial subscription notification,
Host exit0. Это functional interoperability smoke, не performance benchmark.


R3 Windows independent gate подготовлен: [pins, licenses, isolated build и scope](../tools/acceptance/COUNTERPARTS.md).
CI после полного Scaffold запускает real Mosquitto 2.0.22 broker/CLI и asyncua2.1.0
с hash-locked Windows wheels; JSON и bounded logs сохраняются always.
Actual Windows результат этого gate ещё не получен; R3 остаётся OPEN.
