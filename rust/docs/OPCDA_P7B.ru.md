# P7b — read-only OPC DA sidecar

Class: новый Windows COM local-server synchronous read profile; Host `opc_da_consumer`,
shared `icem-opc-da-codec` и отдельный process `icem-opc-da-sidecar`. Legacy OPC toggle,
MMF source и остальные adapters вне класса.
Inv: read-only SnapshotReader, bounded versioned IPC≤256KiB, один in-flight frame,
watchdog kill собственного hung child; raw types/bits, адресные ItemID и writer FILETIME
проверяются codec/service/lifecycle tests. Никаких MMF handles или handwritten unsafe.
N+1: следующий adapter добавляет свой module/config/registration; core/reader не меняются.
Два IPC endpoint требуют одного deterministic codec, malformed/limit tests охраняют контракт.

## Dependency review (до кода)

`https://github.com/Ronbb/rust_opc`, exact revision
`da44a99265f9706618bbe506c98f714f2aa46f0b`: unpublished workspace0.4.0.
Выбраны Windows-only `opc_da_bindings`, `opc_classic_utils`, `opc_classic_types`,
без optional features. Роли: safe DA service/COM adapters, MTA/class factory/runtime
registration, owned scalar/timestamp/error types. MIT; upstream NOTICE отдельно
указывает OPC Foundation metadata IDL terms: не утверждаем, что metadata под MIT.
Upstream MSRV1.97/edition2024≤workspace1.99.1. Published0.3.1 не имеет нужной safe facade.
Transitive windows0.62.2/windows-core и build-only windows-bindgen0.66.0 генерируют
upstream winmd; ABI/COM unsafe находится исключительно в неизменённом dependency.
Safe API reviewed: DaServer/DaService/DaGroupService, ComApartment::mta,
ClassFactory::new, LocalClassRegistration::register; safe owned Value/Timestamp.
Workspace forbid(unsafe_code) не меняется; native Win32 исключение не расширяется.
Full lockfile closure и exact-target compile будут проверены после resolution.

## Scope

Это ограниченный synchronous read profile: IOPCServer, groups/item management,
IOPCSyncIO. Нет browse, asynchronous callbacks/subscriptions или DA3.
Полная OPC DA compatibility не заявляется. Runtime CoRegisterClassObject использует
собственный CLSID; постоянная registry/DCOM/proxy-stub installation не выполняется
автоматически. Cross-process Windows COM client acceptance и production packaging
остаются OPEN; Linux tests не являются Windows COM evidence.

## Запуск

Windows x64, exact Rust1.99.1:

```powershell
cargo build -p icem-host-app -p icem-opc-da-sidecar --locked
.\target\debug\icem-opc-da-sidecar.exe --register
.\target\debug\icem-host-dev.exe --modbus-fixture tests/fixtures/bus-v5-minimal.bin --bind 127.0.0.1:1502 --duration-ms 10000 --opc-da-config docs/examples/opc-da.json
# После остановки Host, при необходимости удалить только собственные mappings:
.\target\debug\icem-opc-da-sidecar.exe --unregister
```

Config≤16KiB, deny unknown/duplicate/missing fields. `sidecar` — executable path
(относительный разрешается от config directory), `update_interval_ms`50..60000,
`watchdog_ms`200..5000. Нет shell command parsing или MMF names/handles.
На Linux `--opc-da-config` и отдельный sidecar завершаются ошибкой; fake COM нет.
Host без optional config не запускает child. `LayerKind::OpcDa` сохраняет legacy no-op.

`--register`/`--unregister` — явные CLI, safe `reg.exe`, только HKCU Software/Classes
и собственные CLSID `{C69B6B01-878C-4927-8CC0-C6BA26C4B71A}` /
ProgID `iCem.Rust.ReadOnlyDA.1`, x64 view. Ошибка любого шага — nonzero;
частично выполненные mappings остаются видимыми. Runtime registration/revoke
принадлежат apartment RAII. Нет LocalServer32 autoactivation: сначала запускается
Host, затем COM client. Категории OPCEnum, proxy/stubs, DCOM permissions и bitness
не регистрируются автоматически. Нужны проверенные совместимые OPC Foundation
proxy/stubs для cross-process custom interfaces; in-process test этого не доказывает.
Registry CLI и actual cross-process activation ещё требуют Windows acceptance.

## Данные и ресурсы

Один существующий acquisition200ms; Host получает только SnapshotReader и клонирует
Arc. IPC — private v1, little-endian length u32, magic/version, sequence/generation,
nullable monotonic capture/completion ages в ns, capture status и полный Bus v5
raw words/zone metadata. Frame≤256KiB; max65535 registers укладываются.
Decode отклоняет bad magic/version/status, отсутствующую геометрию, truncation,
trailing bytes и inconsistent presence/generation. Sidecar также запрещает sequence,
capture generation и geometry regressions. Clean EOF отличается от truncation.
Два endpoints используют один codec, нет parallel schema definitions.

Только один encoded in-flight request+ACK. После ACK новый frame берётся из latest
publication; нет очереди промежуточных состояний. Sidecar хранит один received frame,
canonical `Box<[u16]>` внутри `Arc<Snapshot>`; Float32/Int32/Word — проекции.
Host publication и held frames immutable, client Read не инициирует acquisition.
Sidecar reread/decode — цена process IPC, это не второй MMF acquisition.

Ready handshake только после реального COM CoRegisterClassObject. Host watchdog
проверяет собственный Child каждые10ms, kill+wait при зависшем read/write/ACK или
cancel. Он не убивает процессы по имени/PID. Deadline stop не сбрасывает owner;
повторное ожидание допустимо. Faulted child не перезапускается бесконечно, статус
actual Running/Faulted/Stopping/Stopped; startup failures возвращаются Host.
Время ожидания не является real-time SLA. При cancel child уничтожается: COM
clients теряют connection, гарантируется изоляция shutdown от hung COM SDK.

Собственные service limits:32 server identities,32 одновременно удерживаемых group
objects,4096 items наgroup/batch, non-wrapping item handles. Lease освобождается
только при Drop group, даже если клиент удерживает его после RemoveGroup.
Upstream COM adapter сначала маршалит RPC input, потом вызывает service; эти
application limits не являются доказательством RPC/security memory quota SDK.
SDK transport/DCOM resource/security acceptance остаётся отдельным gate.

## Identity / values / quality / time

ItemID — строго `var{register_address}`, без leading zeros, odd addresses внутри
paired4..599 запрещены. Ни variable index, ни dictionary label не становятся ID.
Native Automation VT_UI2/VT_I4/VT_R4 сохраняют raw16, signedCOM32 и IEEE754bits,
включаяNaNpayload/−0/infinity. COM4..5 — толькоraw int32 Unix seconds.
Requested conversion в другой type не реализуется: EMPTY/canonical only;
ValidateItems не добавляет items, writes всегда OPC_E_BADRIGHTS.

DA sample FILETIME обязателен. Для COM4..199/LAN200..399/RAW400..599 он равен
`zone.last_write_utc_ticks − 504911232000000000` (100ns с1601), при invalid/pre1601
—0. Это AddIn writer time, не measurement time. Никогда COM value или now.
Service words0..3 и reserve≥600 имеют unknown FILETIME0 и uncertain quality.
Sample quality: acquisition captured + capture/completion age<5s + produceractive
+ validwriterclock →GOOD0xc0; inactive zone→BAD_OUT_OF_SERVICE0x1c;
retained/failed/expired/unknownwriter→UNCERTAIN_LAST_USABLE0x44;
service/reserve→UNCERTAIN0x40. Item inactive→out-of-service.
Age увеличивается от Host age на sidecar monotonic elapsed после receipt; Read
не освежает capture age, writer clock или COM rawtime. Повторные failed attempts
могут обновить completion age, но не capture time/generation. ServerStatus local
start/currentTime — исключительно server diagnostics; lastUpdateTime unknown0.

## Windows gates

Default Windows unit test использует safeSDK COM object/client в одном процессе;
он проверяет native types, bits и write denial. Отдельный настоящий process gate:

```powershell
cargo test -p icem-opc-da-sidecar --test windows_process --locked -- --ignored --nocapture
```

Он запускает свой binary, передаёт fixtureIPC, вызывает CoCreateInstance LOCAL_SERVER,
создаёт group/items, читает types/NaN/COMi32/writerFILETIME/quality и запрещённыйwrite.
Own-child watchdog ограничивает зависание. Этот gate intentionally ignored на
обычных машинах без installedmatching proxy/stubs. Его compile не считается PASS
runtime. Industrial third-party Windowsclient, realMMF/AddIn, DCOM/x86/packaging,
registration rollback/operator acceptance остаются OPEN.

CACHE и DEVICE читают один latest publication, без physical I/O изclientRead.
Этот read-only snapshot profile не обещает vendor-device refresh semantics.

## Проверка этого инкремента

Exact Rust1.99.1 Linux: scoped all-target check/build/tests/clippy `-D warnings`,
`--locked --offline`; Host26/26 (4 новых process/config tests), codec3/3 PASS.
`cargo fmt --all --check`, `git diff --check` PASS.
Новые tests покрывают malformed version/status/geometry/truncation/oversize/EOF,
max65535 layout, retention/quality expiry/backwards clock, stable address/native
bits, writer timestamps, own hung child ACK/read и blocked131KiB write, cancellation,
repeat stop, actual pipe ACK, независимые2000 SnapshotReader observations.
Windows x64 target: codec+sidecar `cargo check/clippy --all-targets` PASS, включая
safeSDK COM unit tests и ignored realprocess client harness compile. Linux runtime
sidecar возвращает exit1 сrequiresWindows. Windows runtime tests здесь НЕ запускались.
Полный Windows CI предыдущего head имел независимые failures в acquisition/MQTT
Windows harness и legacy Modbus; этот scope их не изменяет и не объявляет исправленными.
