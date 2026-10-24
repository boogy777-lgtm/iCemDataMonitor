# P7b — read-only OPC DA sidecar

Текущий P7c1 снимает только coherent dynamic group-state blocker через локальное
reviewed safe SDK extension (последний раздел). Исторические feasibility findings
ниже описывают неизменённый upstream pin; callbacks остаются NEXT P7c2, P7 PARTIAL.

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
source producer-active/age quality. P7c1 also gates CACHE by coherent current group-active; see below.
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

### Findings and remaining safe SDK blockers (historical P7b, superseded by P7c1 below)

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

## Повторная upstream feasibility-проверка (2026-10-24, до P7c1)

Class: готовая safe DA server capability для coherent group state и owned COM
callbacks; client callback receivers и ABI-only interface declarations вне класса.
Inv: capability принимается только при доступном service API, ownership/marshalling
контракте и Windows client evidence; ниже exact refs и acceptance cases.
N+1: обновление SDK повторяет эту проверку, dependency audit/lock и те же Windows
cases; отсутствие одного из обязательных пунктов сохраняет P7 PARTIAL.

Проверены live `git ls-remote --symref origin`, fetch всех heads/tags и исходники
[Ronbb/rust_opc](https://github.com/Ronbb/rust_opc). На дату проверки remote HEAD
указывает на master, `da44a99265f9706618bbe506c98f714f2aa46f0b`
(commit 2026-08-06); это уже используемый pin, более нового default ref нет.
`refs/tags/*` отсутствуют; GitHub REST
[`releases?per_page=100`](https://api.github.com/repos/Ronbb/rust_opc/releases?per_page=100)
и [`pulls?state=open&per_page=100`](https://api.github.com/repos/Ronbb/rust_opc/pulls?state=open&per_page=100)
вернули пустые массивы. Published `opc_da`/`opc_da_bindings` latest в docs.rs —
0.3.1; это отдельная старая API-линия, а не готовая 0.4 server extension.

| Проверенный remote head | Exact commit | Результат осмотра |
|---|---|---|
| master | `da44a99265f9706618bbe506c98f714f2aa46f0b` | Текущая safe facade, оба blockers сохранены |
| refactor/safe-opc-bindings | `753ffa0e90b4371031cc6db2bd6af814f65775ce` | Те же service signatures и три group interfaces |
| ci/opc-classic-integration | `99a0b58bfc4085e0473b42cf251026416a435749` | Те же service signatures и три group interfaces |
| feat/client | `e0bdc0c3a573f50b6e2f470f4789a172a45ddff4` | Старые ABI-shaped server traits/connection point scaffold |
| feat/remote-client | `0dd435b0f913e2d59f1d90b7956ed86f2871bc7e` | Старая API-линия, не 0.4 safe extension |
| feat/bindings | `fb45e85f66939dae6fec8f9704d69f03d99d2a0a` | Старая API-линия, не 0.4 safe extension |
| chore/update-dpes | `27ac0a31edceb77bcd231f15169eef23f43f12f6` | Старая API-линия, не 0.4 safe extension |
| chore/update-idl | `7a92495bd5651c864bb30274965b6067d3ed8f84` | Старая API-линия, не 0.4 safe extension |
| feat/opc_da | `cd3d8a76c2503e43f77186d57d09a441a9414920` | Раздельные ABI adapters, нет готового owned server callback lifecycle |

На master перепроверены не только trait declarations, но и возможный safe обход:

- [`server/mod.rs`](https://github.com/Ronbb/rust_opc/blob/da44a99265f9706618bbe506c98f714f2aa46f0b/opc_da_bindings/src/server/mod.rs):
  `DaService::add_group` получает initial `GroupOptions`, возвращает service Arc;
  private `DaGroupAdapter` создаётся SDK после этого. `SetState` меняет его private
  mutex state, `Read` вызывает только `service.read(source, handles)`. Ни state
  capability, ни notification hook в service не передаётся.
- [`client/group.rs`](https://github.com/Ronbb/rust_opc/blob/da44a99265f9706618bbe506c98f714f2aa46f0b/opc_da_bindings/src/client/group.rs)
  действительно имеет safe `DaGroup::state()`, но `DaGroup::new` — `pub(crate)`.
  [`client/mod.rs`](https://github.com/Ronbb/rust_opc/blob/da44a99265f9706618bbe506c98f714f2aa46f0b/opc_da_bindings/src/client/mod.rs)
  предоставляет `DaClient::from_object` и `add_group`, не safe lookup существующей
  client-created group. Создать другую group не значит получить её состояние.
  `DaServer::service()` возвращает исходный service, `object()` — server identity,
  а `ComObject::query_interface` не создаёт typed state reader. Даже отдельный
  poll GetState не задаёт atomic ordering с concurrent SetState/Read и не решает
  callbacks. Начальный active snapshot не является исправлением.
- Group COM identity реализует только `IOPCItemMgt`, `IOPCSyncIO`,
  `IOPCGroupStateMgt`. Safe client/common/AE subscription APIs и сгенерированный
  `IOPCDataCallback` не добавляют DA server connection point/publisher.

Старые branches не пропущены из-за их названий: там действительно есть
`opc_da/src/server/com/group.rs` с async/connection-point interfaces и
`connection_point.rs` с Advise/Unadvise. Однако public `GroupTrait` принимает
`tagOPCITEMDEF`, `PWSTR`, `VARIANT` и COM interfaces, а
[`memory.rs` на feat/bindings](https://github.com/Ronbb/rust_opc/blob/fb45e85f66939dae6fec8f9704d69f03d99d2a0a/opc_da/src/server/com/memory.rs)
имеет safe public `IntoRef`/`IntoComArrayRef` для raw pointers с произвольным
возвращаемым lifetime: проверка null не доказывает валидность памяти.
`ConnectionPoint` хранит `IUnknown` в map; это не доказательство marshalled owned
sink, bounded dispatch/cancel/shutdown. Такие wrappers не проходят требование
проверенной safe границы. Feature name `unstable_server` сам по себе не означает
требование nightly; причиной отказа является API/ownership, а не имя feature.

Дополнительно проверены первичные README/API альтернатив:
[`opcda-bridge`](https://github.com/bytehound-labs/opcda-bridge) и его
`bytehound-opc-da-client`, [`opc-da-client`](https://docs.rs/opc-da-client/latest/opc_da_client/)
предоставляют доступ к существующим DA servers, не публикацию нашего COM server.
[`marcschier/opc-classic`](https://github.com/marcschier/opc-classic) описывает
DA publisher, но это experimental alpha .NET 10 SDK, не safe Rust extension к
текущему adapter; готового Rust ownership/marshalling контракта для него нет.
Это bounded поиск доступного поддерживаемого пути, не утверждение об отсутствии
любого SDK в мире. Квалифицирующего готового safe Rust server пути не найдено.
Новых dependencies/lockfile changes нет. Действующий MIT + отдельные OPC IDL terms,
MSRV1.97≤1.99.1 и существующий safe synchronous profile остаются прежними.

### Минимальное safe upstream extension и приёмка

Требуются обе capabilities на одной group identity. Первая — owned coherent
group-state snapshot в read/dispatch или transactional state hook с явно заданным
commit/error/rollback ordering. Вторая — safe owned DA sink/advise lease и
publisher: SDK владеет COM refs и apartment marshalling; приложение передаёт
только owned values/quality/timestamps/handles. Ограничения групп/items/sinks,
очередей и in-flight операций должны быть явными; latest-value coalescing не
должен терять completion/cancellation events. Нужны reentrant-safe dispatch вне
model locks, корректный unadvise/remove/disconnect и bounded shutdown с
изоляцией hung sink существующим process watchdog. Raw sink pointer, handwritten
application COM ABI или добавление unsafe в native crate не являются extension.

| Acceptance case | Необходимое наблюдение настоящим Windows client |
|---|---|
| AddGroup(false), SetState true→false→true, concurrent CACHE Read | Group/item activity учитываются по согласованному состоянию; DEVICE сохраняет source quality и игнорирует subscription activity |
| Изменение update rate/client handle/item active при публикации | Revised rate и callback handles соответствуют committed group/item state; producer acquisition остаётся 200ms |
| Advise двух sinks, Unadvise, повторный/неизвестный cookie, disconnect | Независимые leases; определённая граница in-flight доставки и освобождение COM refs |
| OnDataChange и поддерживаемые async read/refresh/cancel, read-only write denial | Coherent native type/bits/quality/writer FILETIME, правильные transaction/client handles и per-item HRESULT; нет фиктивного completion |
| Slow/hung/reentrant sink, burst публикаций, RemoveGroup(force), stop | Bounded storage/dispatch, нет deadlock core/model locks; остальные consumers работают; own-child kill/reap при deadline |
| Cross-apartment и cross-process client с matching proxy/stubs | Реальные callback marshalling/lifetime, повторные connect/disconnect и отсутствие leaked group/sink после stop |

До этих evidence P7 остаётся PARTIAL. Policy менять для снятия blocker не нужно:
достаточно реального audited upstream safe extension с этими гарантиями; сейчас
оно отсутствует. Нельзя объявить blocker снятым обновлением pin, polling private
state, имитацией callback или уменьшением требований плана. Эта проверка doc-only;
нового Windows runtime PASS или compile evidence она не добавляет.

## P7c1 — coherent owned group state, 2026-10-24

Class: синхронный group-state snapshot в SDK `DaGroupAdapter::read_current_state`
и sidecar `Group::read`; callbacks и producer acquisition вне класса.
Inv: один owned `GroupState` из существующего mutex на весь Read; CACHE требует
active group+item, DEVICE сохраняет producer quality. SDK/client tests охраняют
state tuples, failed SetState, CACHE/DEVICE и сохранение существующих limits.
N+1: обязательный trait argument заставляет обновить каждый service implementation;
P7c2 использует committed state, не протягивая OPC в core/reader/IPC.

Dependency review до реализации: тот же exact upstream commit
`da44a99265f9706618bbe506c98f714f2aa46f0b`, теперь deterministic external workspace
`third-party/rust_opc`, явно excluded из application members. Выбраны только пять
crates нужного DA closure: 42 upstream files, 415895 bytes, включая LICENSE,
NOTICE и оба build-input winmd. MIT/отдельные OPC metadata terms, edition2024,
features и registry dependency versions не меняются. Upstream MSRV1.97 сохранён
в provenance; selected patch declares/tested exact1.99.1, без обещания older MSRV.
Cargo.lock
меняет только пять git source identities на reviewed local dependencies.
`README.icem.md`, exact SHA256 manifest, reversible `icem.patch` и `verify.py`
делают всю delta проверяемой без cache surgery, замены CARGO_HOME или upstream
checkout. Build scripts пишут bindings/module wrapper только в OUT_DIR;
tracked upstream bindings не изменяются build. Три deprecated atomic calls
заменены на equivalent stable `try_update`, без подавления warnings.

`DaGroupService::read` теперь обязательно получает owned `GroupState`. Новый
helper `forbid(unsafe_code)` клонирует существующий mutex state и освобождает
lock до application service. Это единственная linearization point group state
для Read: concurrent SetState может завершиться до возврата Read, но не смешать
old active с new rate/client handle. Existing SetState ABI/unsafe operations
не изменены: revised output проверяется/обнуляется до lock, весь state меняется
под одним lock, revised rate записывается до unlock; invalid output не commit.
Read error не откатывает уже committed group state. В AddGroup используется
тот же initial state. Item handles/client handles и resource leases не меняются.

Sidecar CACHE теперь выдаёт BAD_OUT_OF_SERVICE при inactive group **или** item.
DEVICE игнорирует оба subscription flags и сохраняет producer activity,
retained/age/writer-clock quality. Update rate/client handle сохраняются SDK
и видимы service в одном snapshot; rate не меняет acquisition200ms и не создаёт
ещё не реализованные notifications. Codec, IPC clocks и MMF ownership неизменны.
Новых unsafe operations, ABI definitions или native exception нет;
application/shared `forbid` и ADR0001 не ослабляются.

Windows tests: настоящий safe COM client AddGroup(false)/Read/GetState проверяет
inactive CACHE, GOOD DEVICE и retained producer UNCERTAIN. Существующий item
activity/capacity test остаётся. Новые SDK tests с `forbid(unsafe_code)` вызывают
**existing generated SetState implementation entrypoint** с owned аргументами,
затем настоящий safe COM client Read/GetState: false→true→false, revised rate,
client handle, invalid revised-output без commit и 2000 concurrent SetState/Read
с цельными допустимыми tuples. Это честно отличается от внешнего COM SetState:
safe upstream client wrapper для него отсутствует. Новая unsafe COM операция
ради теста не добавлена; actual external SetState runtime gate остаётся OPEN.

Runtime Windows gate из application workspace:
`cargo test -p opc_da_bindings -p icem-opc-da-sidecar --locked --offline`.
Linux/cross-target compile не являются Windows runtime PASS. Callbacks,
advise/unadvise, async completion/cancel, cross-apartment/process marshalled
sinks и hung/reentrant callback handling остаются NEXT P7c2. Полный P7 не закрыт.

Локальная проверка exact Rust1.99.1: `cargo fetch --locked`, затем locked/offline
Linux Host26/26, codec5/5 и sidecar build PASS; Linux all-target clippy этих трёх
packages с `-D warnings`, fmt-all/check и diff-check PASS. Windows x64 all-target
check/clippy sidecar+codec+SDK с `-D warnings` PASS, включая новые SDK/client tests
и прежний ignored process harness compilation. Runtime Windows здесь не запущен.
Все 42 upstream SHA256 сверены непосредственно с exact git objects pin;
reverse-patch verifier и metadata nonmember guard PASS. Принудительная повторная
генерация обоих bindings через touched build scripts и actual Windows check
сохранила SHA256 **каждого** vendor file, включая bindings и provenance.

## P7c2 current draft — real callbacks implemented, review/runtime gates OPEN

Class: read-only DA2 callback/async SDK boundary on existing group identity; source
MMF/core/acquisition and full DA3/browse outside scope. Inv: owned bounded snapshots,
GIT ownership, callbacks outside locks, pure queue/health and actual Windows COM
client tests. N+1: mandatory service notification, exhaustive event kind, explicit
four-file SDK provenance allowlist. ADR0019 records accepted implementation design;
native independent review is still required before adoption. P7c1 limitations above
are historical; P7 remains PARTIAL.

Group now implements CPC/CP/AsyncIO2 on the same COM identity. Real native
OnDataChange/OnReadComplete/OnCancelComplete use current owned sidecar model, no MMF
read on callback. F32/F64 comparison uses bits; timestamp-only change is not an
exception, delivered writer FILETIME and monotonic source age are preserved. Activity,
client handles and revised rate are committed snapshots; revision read before state
snapshot plus dispatch generation gate prevents stale periodic delivery. Acquisition
stays200ms. Rate minimum10ms returns OPC_S_UNSUPPORTEDRATE for smaller requests,
bypassing generic Error's deliberate rejection of arbitrary positive status codes.

Advise registers in caller GIT, worker initializes MTA and retrieves its own proxy;
only cookies/owned state cross threads. CP/container identity is tested. Unadvise/
RemoveGroup close delivery without waiting for in-flight; refs and global worker
charge survive until actual exit. Foreign invoke/Release/revoke outside all locks,
health includes retired hung workers. Safe client subscription has explicit close;
reentrant borrowed commands cannot export native group/object references.

Bounds:32groups/process,4096items/batch,4sinks/group,128workers including retired,
32async/group,1latest slot+32reserved completions/sink,1in-flight. Arc frames<=512KiB
fan out without per-sink deep copy. Requests retain frames until ALL live sinks dequeue
completion; pending worst case512MiB across32groups, with bounded additional baseline/
in-flight/native buffers and256KiB stacks. Atomic fanout reservation rolls back before
failure. Close ends connection delivery; in-flight may finish. Failed COM sink retires.

Read DEVICE ignores activity; early failed items excluded, all-failed no completion.
Refresh2 inactive group/all inactive=>E_FAIL, refresh invalidates stale periodic frames
without moving timer. Set/GetEnable without sink=>CONNECT_E_NOCONNECTION; enable only
affects transaction0. Late/repeated cancel=>E_FAIL, successful queued cancel produces
real native OnCancelComplete. Async Write early BADRIGHTS, no fake completion.
Locale0/deadband0 only; unsupported settings reject before commit. Enumerators
E_NOTIMPL, arrays/byte arrays, browse/full DA3 outside profile. Primary spec sections
and native operations are listed in ADR0019.

Independent safe application watchdog checks every25ms; busy callback/foreign teardown
>=2s or failed SDK health terminates own sidecar with exit70 even at Host interval60000
or idle IPC. Generic SDK never exits process or joins foreign callback. Existing parent
own-child kill/reap boundary isolates the rest of Host.

Prepared runtime checks: two sinks native U16/I32/F32 NaN/exact FILETIME/retained10s
quality, inactive/reactivation, item/client handles, rate/profile rollback/reconnect,
AsyncRead/Write/Refresh/cancel,32-request two-blocked-sink capacity, fifth-sink cap and
4096-item real callback, reentrant COM Unadvise+RemoveGroup, real hung callback child
then Unadvise+60s idle requiring exit70<=6s. Pure queue race/health execute on Linux.
Opt-in process harness now includes actual callbacks/refresh, remains ignored until
matching proxy/stub gate. Neither compiled tests nor in-process tests prove external
cross-apartment/process acceptance. Root separately owns proxy/stub CI investigation.
