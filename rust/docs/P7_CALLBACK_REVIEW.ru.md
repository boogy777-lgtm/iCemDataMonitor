# P7c2: независимое ревью SDK native boundary

2026-10-24. База `bd7e91c`; исправления `29b46ae` и `5c225f3`.
Ревью выполнено независимо от автора реализации ADR0019. Вывод по исходному
native boundary: пять доказуемых дефектов исправлены; новых неустранённых
ownership/apartment/panic/queue blockers в проверенном профиле не найдено.
Это разрешает продолжить Windows acceptance, но не означает adoption/full P7 PASS.
Exact-head Windows runtime новых regressions и cross-apartment/process RPC OPEN.

## Контракт до исправления

Class: lifecycle/error delivery SDK subscription и bounded owned callback decode;
входит failed Unadvise/GIT startup/retrieval и входной BSTR, не входит acquisition.
Inv: cookie сохраняется до successful disconnect; registration имеет одного владельца;
failed callback delivery возвращает HRESULT error; callback payload budget проверяется
до owned string allocation.
N+1: `client/callbacks.rs` и `server/callbacks.rs`, targeted native regressions;
complete reversible `icem.patch` и неизменные original42 SHA256 служат guard.

Class: baseline periodic delivery при Refresh2/Cancel2, не явный AsyncRead.
Inv: baseline меняется только после успешной доставки; clear_latest + cancelled
refresh не подавляют неизменившееся, но ещё не доставленное значение.
N+1: local `PeriodicBaseline` в server/callbacks.rs; deterministic queue test
проходит clear_latest/cancel и проверяет subsequent delivery без sleeps.

## Findings и исправления

| Severity | Доказуемый дефект | Исправление и regression |
|---|---|---|
| HIGH | `close` делал `cookie.take()` до Unadvise: временный HRESULT failure терял retry при ещё удерживаемом sink | Cookie очищается после success; `failed_unadvise_keeps_cookie_for_retry` проверяет fail → повтор с тем же cookie → idempotent close |
| HIGH | Reentrant closure игнорировала GIT retrieval failure и ACK-ала недоставленное событие | Внутренний callback handler возвращает Result через catch_ffi; `reentrant_git_failure_returns_error_instead_of_success_ack` использует настоящий invalid GIT cookie, проверяет HRESULT failure и отсутствие handler call |
| HIGH | Count<=4096 не ограничивал UTF-16 BSTR → owned String allocation | Полный preflight считает aggregate UTF-8 payload + event/item storage до conversion; `callback_bounds_aggregate_strings_before_handler` проверяет две строки, UTF-8 expansion и успешное обычное событие |
| HIGH | Ошибка MTA/GIT startup до worker RAII оставляла registration без revoke | Caller RAII создаётся сразу после Register. Worker сначала получает apartment-local GIT, сигнализирует ready, затем принимает только scalar cookie через rendezvous channel. Caller disarm только после успешного send. `apartment_startup_failure_leaves_caller_rollback_owner` вызывает реальный STA → MTA initialization failure и проверяет revoked cookie |
| HIGH | Periodic baseline сдвигался при sampling; Refresh clear_latest + Cancel могли удалить все data frames и навсегда подавить unchanged value | Отдельный bounded candidate коммитится после successful invoke; Refresh коммитит доставленный full snapshot. `cancelled_refresh_cannot_commit_an_undelivered_periodic_baseline` детерминированно проверяет потерю latest, cancel и последующую доставку, затем changes subset/full baseline coherence |

Public handlers сохраняют прежнюю сигнатуру. Result добавлен только во внутренний
transport callback. `Drop` subscription остаётся best effort: при постоянном
Unadvise failure деструктор не может обещать successful disconnect; явный `close`
сохраняет возможность retry, пока lease жив. Ошибка GIT retrieval теперь наблюдаема
сервером, который закрывает/retire failed sink вместо silent success.

## Native ownership и lock review

Проверены server/{mod,callbacks,callback_queue,callback_health}.rs,
client/{mod,group,callbacks}.rs, safe sidecar windows.rs,
windows/callback_tests.rs, tests/windows_process.rs, ADR0019 и provenance delta.

- CP/CPC/AsyncIO2 реализованы одним group identity. GetContainer возвращает его же;
  SDK не хранит собственный connection point interface внутри объекта.
- Advise QI/Register выполняются в вызывающем apartment. Передача worker не содержит
  IUnknown/proxy/ComApartment: только cookie, owned snapshots, Arc safe state.
  Rendezvous waits выполняются после освобождения members lock. До передачи cookie
  caller владеет rollback; после передачи worker уже имеет initialized apartment/GIT
  и немедленно создаёт registration RAII без промежуточной fallible операции.
- GIT retrieval выдаёт ровно одну typed reference; null success отвергается.
  Proxy Release предшествует registration revoke, оба предшествуют ComApartment Drop.
  Пользовательский invoke, proxy Release и revoke выполняются вне members/state/queue
  locks. Startup rollback не делает COM calls под ними.
- CallbackGroup не экспортирует native group/object и возвращает owned results;
  lifetime и apartment marker не позволяют safe handler передать proxy в другой thread.
  Reentrant registrations держит originating subscription, callback хранит только cookies.
- catch_ffi/serve покрывают callback decode, application handler/service и mutating
  COM entrypoints. Worker catch_unwind выставляет sticky failure; RAII сохраняет
  teardown при unwind. Прямые constant IID/E_NOTIMPL methods не вызывают handler.
- Output initialization предшествует queue mutation. Async all-failed возвращает
  cancel=0 без completion; readonly Write возвращает early BADRIGHTS.
  Positive OPC_S_UNSUPPORTEDRATE не теряется generic Error conversion.
- Revision читается до state snapshot; SetState invalidates до unlock. Gate перед
  invoke задаёт границу in-flight: перекрывающаяся mutation может завершиться, когда
  уже начавшийся callback ещё исполняется. Item mutation invalidates до возврата.
  Refresh epoch подавляет stale periodic frames, но больше не коммитит unsent baseline.
- Reservation/cancel/dequeue сериализованы members → queues; rollback возвращает
  ранее взятые credits. Если хотя бы один live sink начал completion, Cancel fails.
  Latest не отнимает completion credits. Retired/hung workers сохраняют monitor lease
  и process worker charge до фактического завершения, включая Release/revoke.

## Resource ledger и предел доказательства

Сохраняются 32 groups, 4096 items/batch, 4 sinks/group, 128 workers включая retired,
32 retained async requests/group, latest1 + completion32/sink и frame payload<=512KiB.
Arc fanout не deep-copy-ит completion frame на каждый sink. Retention до dequeue
всех оставшихся advised queues даёт прежние <=512MiB retained request payload.
Stack reservations <=32MiB при 128 workers по256KiB.

Delivered baseline <=512KiB/worker; новый complete pending candidate также
<=512KiB/worker: дополнительные <=64MiB process payload, baseline+candidate <=128MiB.
Latest, in-flight, transient service results и native VARIANT/parallel arrays —
дополнительные bounded copies, не часть 512MiB request subtotal. UTF-16 native output
и String allocator capacity не равны UTF-8 logical payload: 512KiB не является
обещанием общего RSS или точного размера всех allocations.

**OPEN finding R6, MEDIUM — pre-enqueue concurrent SDK allocations.**
В `server/callbacks.rs`, `IOPCAsyncIO2_Impl::Read` создаёт handles Vec, вызывает
service.read и готовит CoTaskMem errors до `callbacks.enqueue`; `Refresh2` получает
owned notification до того же admission. Одновременно вошедшие calls могут пройти
эти allocations до первого заполнения retained request queue. Targeted early
admission перед SDK-owned preparation — отдельный follow-up, не unsafe/lifetime
blocker и не реализован этим reviewer. COM/RPC unmarshalling allocation происходит
ещё до SDK entrypoint и требует отдельного анализа; early SDK admission его не
ограничивает.

32-request cap относится к retained accepted requests. AsyncRead/Refresh service
snapshot и output preparation происходят до enqueue admission. Число одновременно
выполняющихся внешних RPC calls этим cap не ограничено; whole-process concurrency/RSS
cap не доказан. Каждый profile batch ограничен; generic service обязан ограничить
свои allocations до возврата owned snapshot. Никакой blanket process-memory guarantee
из этой проверки не следует.

SDK не может заставить произвольный COM call вернуться. Safe application watchdog
независим от IPC, проверяет каждые25ms и использует exit70 при >=2s busy/failure;
SDK только сообщает health. Unadvise/RemoveGroup не join-ят чужой worker. Parent
own-child kill/reap остаётся последней границей. Это containment, не RPC deadline.

## Выполненные проверки

- Exact Rust1.99.1 Windows x64 SDK+sidecar `cargo clippy --all-targets --locked
  --offline -- -D warnings`: PASS после обоих production исправлений, включая
  compilation пяти новых regressions. Первый запуск выявил native test macro под
  новым forbid; fixture вынесен отдельно, safe client test module сохраняет forbid.
- Linux exact-source queue5/health3: PASS. Constant arithmetic test `finite_profile`
  не считается самостоятельным доказательством boundedness; существенные checks —
  completion credits, cancel/dequeue race, close и retained worker health/cap.
- Complete reversible patch: original42 SHA256 PASS; exact upstream pin,
  MIT/NOTICE, dependency/lockfile и app/shared forbid не изменены.
- Vendor rustfmt и git diff whitespace check: PASS. Изменены только external SDK
  callback implementation/tests и этот review; core/acquisition/.NET/AddIn/proto/assets
  и остальные adapters не затронуты этим reviewer.

## Открытые acceptance gates

1. Exact-head Windows runtime пяти новых regressions и прежних native/sidecar tests.
   Известный runtime PASS предыдущего head не распространяется на новые изменения.
2. Matching pinned IDL/proxy/stub: реальные STA/MTA и cross-process callbacks,
   reentrancy, 4096-item geometry, process cleanup и deployment/license audit.
   In-process MTA tests не доказывают RPC marshalling и не закрывают этот пункт.
3. Full P7/operator/SDK deployment acceptance, включая packaging distribution terms.

Source review завершено с исправлениями. Adoption остаётся условным до выполнения
этих gates; P7 остаётся PARTIAL.
