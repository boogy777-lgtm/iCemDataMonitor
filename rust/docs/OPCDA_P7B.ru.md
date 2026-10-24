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
—0. Ticks выше DateTime.MaxValue также invalid/unknown0. Это AddIn writer time,
не measurement time. Никогда COM value или now.
Service words0..3 и reserve≥600 имеют unknown FILETIME0 и uncertain quality.
Sample quality: acquisition captured + capture/completion age<5s + produceractive
+ validwriterclock →GOOD0xc0; inactive zone→BAD_OUT_OF_SERVICE0x1c;
retained/failed/expired/unknownwriter→UNCERTAIN_LAST_USABLE0x44;
service/reserve→UNCERTAIN0x40. CACHE item inactive→out-of-service; DEVICE ignores item-active, preserving
source producer-active/age quality. Group-active limitations are listed below.
Age = Host age + elapsed от sidecar Instant перед READY/предыдущим ACK.
Этот credit выдан до Host sample; возраст консервативно включает idle, pipe
write, decode и model-lock wait. После приёма возраст продолжает расти; Read
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

## Final review: monotonic IPC age and item validation

Class: two-endpoint, one-credit-at-a-time IPC freshness (`Received`, sidecar run loop);
measurement clocks and general COM transport are near nonmembers.
Inv: receiver anchor precedes Host age sampling; Host age plus receiver elapsed is
an upper bound including blocked write/decode/model publication. Synthetic delayed
receipt/expiry tests guard against stale GOOD without comparing process clocks.
N+1: another IPC endpoint must preserve READY/ACK credit ordering and the same
constructor contract; codec tests cover delayed delivery and backward/overflow ages.

Class: DA validation of canonical item identities/types; committing item handles is
its near nonmember. Inv: ValidateItems is independent of held-item/handle capacity,
allocates no item handles, and keeps batch bound4096; Windows service regression test.
N+1: add item conversions at the same validation point with both committing and
noncommitting tests, without modifying acquisition or canonical identities.

Class: synchronous CACHE activity gating versus DEVICE snapshot reads; source
producer-active is independent. Inv: inactive item forces CACHE out-of-service; DEVICE ignores this subscription
flag, not source quality. Group activation remains a pinned SDK blocker.
Windows service tests compare both reads. N+1: dynamic group state/callback support
requires the missing safe SDK state notification hook (see blockers below).

Class: optional-runtime DA sidecar in Windows payload inventory; COM registration,
proxy/stubs and DCOM are near nonmembers. Inv: fourth binary is built, hash/PE checked,
archived and harvested into MSI using existing inventory; packaging contract requires
its presence. N+1: another binary edits inventory and role allowlist, existing
roundtrip/fixture enumeration guards build/staging/MSI completeness.

Class: source writer DateTime ticks to DA FILETIME; raw COM Unix value is a near
nonmember. Inv: only ticks in1601..DateTime.MaxValue become nonzero FILETIME;
pre1601/overflow dates remain unknown/UNCERTAIN, with codec boundary tests.
N+1: another timestamp field requires its own provenance/range conversion tests;
canonical raw writer metadata is never repaired or normalized.

### Findings and remaining safe SDK blockers

P1 fixed: a blocked Host pipe write followed by receipt-after-decode could hide
1..5s of transit and rejuvenate stale data. READY/the previous ACK now grants a
single frame credit; its receiver Instant predates Host age sampling. For capture
time C, Host sampling H, sidecar credit R and read N, R≤H gives
(H−C)+(N−R)≥N−C. This is a conservative bound, not a measured one-way latency.
Idle before Host sampling is double counted; interval-spaced frames after the
first at update interval≥5s therefore yield UNCERTAIN even with a healthy source. The watchdog is cooperative kill/reap,
checks approximately every10ms, and has no scheduling SLA: its200..5000ms bound
is not used as proof of freshness. No UTC/measurement time or shared process
clock representation is invented. Credit ordering is part of private v1 protocol.

P2 fixed: ValidateItems previously returned OUT_OF_MEMORY at the committed item
quota or exhausted handle counter despite adding nothing. Validation now ignores
these committing quotas and retains the4096 batch bound. DEVICE now ignores
item-active as required for synchronous reads; CACHE still gates inactive items.
Writer ticks outside the .NET DateTime range now remain unknown/UNCERTAIN.

P2 OPEN: group-active semantics are incomplete. At pinned rust_opc revision
`da44a99265f9706618bbe506c98f714f2aa46f0b`,
`opc_da_bindings/src/server/mod.rs` stores `GroupOptions.active` in private
`DaGroupAdapter.state`. Its `IOPCGroupStateMgt::SetState` changes only this
private state; `IOPCSyncIO::Read` forwards only `(DataSource, handles)` to
`DaGroupService::read`. `DaGroupService` has no group-state notification/query
hook. The application receives initial options but cannot observe subsequent
changes through its service capability; storing initial active alone would
produce incorrect CACHE results after SetState. The current development profile
does not implement group-active CACHE quality; client group activation changes
are unsupported. Full DA/P7 acceptance is blocked. Required safe SDK change:
pass current coherent group state to read, or add a reliable state-update hook
with rollback/error semantics and client tests for AddGroup(false)/SetState.

P2 OPEN: plan§7 explicitly requires COM groups/callbacks. The same pinned
`DaGroupAdapter` implements only `IOPCItemMgt`, `IOPCSyncIO`,
`IOPCGroupStateMgt`; no `IConnectionPointContainer`/connection point or
`IOPCAsyncIO2` server is implemented, and `DaGroupService` exposes no owned
callback sink/advise/unadvise/change notification API. ABI interface definitions
are not a safe usable server facade. Implementing callbacks requires an audited
SDK extension covering COM sink lifetimes/thread marshalling, bounded/coalesced
notifications, group/item activity and rates, cancellation/unadvise, backpressure
and shutdown; then real Windows cross-process subscription/client tests. This
review does not add a speculative native fork or relax unsafe policy. P7 remains
PARTIAL; limiting this development profile does not change the plan requirement.

RPC resource boundary: upstream `AddItems`/`ValidateItems` call
`decode_item_specs(borrow_input(...))` before the application service, allocating
owned strings/blob/item vectors; `Read` decodes handles before service limits.
Result arrays/VARIANTs are allocated and marshalled by upstream SDK using its
CoTaskMem builders/cleanup. Application batch/object limits bound retained
application state, not pre-service RPC decoding or arbitrary DCOM traffic.
No transport security/memory-quota claim is made.

Packaging inventory now includes the optional-runtime sidecar binary, built and
staged through the existing generic builder and MSI harvester. It performs no
COM/proxy registration. Actual Windows package/COM/proxy/client/MMF/hardware
acceptance remains OPEN. No legacy source, AddIn, frozen proto or core changes.

Protocol evidence: OPC Foundation, *OPC Data Access Custom Interface Specification
3.0*, §4.4.5.1 (p77), [published specification mirror](https://advosol.com/opcspecs/opcda3.00.pdf):
CACHE requires active group and item; DEVICE is independent of those activity
flags. The snapshot adapter still does not initiate MMF I/O from client reads;
its explicit source is the latest shared publication, with acquisition quality.

Final local validation (exact Rust1.99.1): Linux codec5/5 and Host26/26 PASS;
fmt all/check, Linux codec all-target clippy and Windows x64 sidecar+codec
all-target check/clippy `-D warnings`, locked/offline PASS. Windows-only service
regression and process client harness are compile checked, not executed here.
Routing/cache42/42 and `git diff --check` PASS. PowerShell is unavailable locally;
changed packaging contract must run in Windows CI. These checks do not close the
remaining group-state/callback/COM client/MMF/hardware/release gates.
