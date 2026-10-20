# E21 — типизированная граница dashboard

База main 999920686bce699829df03490284de7264985151, tree
a833a86521bfc3d1b5bba32e0db15cbafa7003e5. E20 exact PR17 head
2a7ddb3aa1db03c5f57e22769fd1a39a1aeb9b39 прошёл Rust/MSVC 39271331957,
Legacy 39271331691 и installer contracts 39271331985. Post-merge main Rust
39273027177 и Legacy 39273027151 также SUCCESS. Это evidence базы, не E21 gate.

[ADR0018](adr/0018-typed-dashboard.ru.md) фиксирует Class/Inv/N+1 и checked API.
Read-only HTTP adapter возвращает owned Registers и независимый Result<History>.
Shell/GPUI передают typed результат без JSON roundtrip и permissive Value field
reads. Malformed response не публикует новый snapshot и не заменяет last-good data.
До первого good snapshot loaded=false; после ошибки прежний good snapshot остаётся
loaded=true с error.
Good registers остаются видимыми при malformed/failed history. Selection race
фильтруется по current selection после проверки ответа против request selection.

Contract tests сохраняют существующие assertions и добавляют полную геометрию,
unsigned scalar, finite values, signed Unix extremes, original timestamp formats,
required nullable fields, liveness 4999/5000 coherence, malformed HTTP-200, HTTP 500,
empty selection, bounded history 700→600 и actual typed host dispatch retention.
Frozen REST payload/SPA/proto и UI appearance/mapping 176+6 прежние.

Toolchain 1.99.1, Edition 2024/resolver 3, GPUI0.2.2/gpui-tray0.1.4, native unsafe
exception и guards неизменны. Прямой serde edge использует уже locked package и
features; review лицензии/unsafe boundary описан в ADR0018. Production .NET,
immutable source manifest и 14 legacy approvals не меняются.

Локально Rust 1.99.1 Linux: fmt --all --check, check --workspace --all-targets,
build --workspace, test --workspace (193 passed, включая 2 compile-fail doctests),
Clippy --workspace --all-targets / -D warnings — PASS; cargo gates locked/offline.
После Box<Read> representation fix все эти gates выполнены заново; никаких lint
allowances не добавлено. Dashboard 9/9 и web_contract 16/16 входят в общий результат.
Фактические Windows results требуют отдельного exact-head gate.
Windows/MSVC/PowerShell/native/legacy/browser/MSI требуют exact-head SUCCESS.
Physical UI/drafts/scroll/selection/keyboard/focus/DPI/session, real iCem/AddIn/COM/
Npcap, true SDK release/install/reboot/rollback/offline и payload measurements —
NOT_RUN/OPEN. D22, dormant capture-to-file и external OPC DA no-op прежние.
Следующие отдельные increments: production --selftest, explicit-adapter --landiag,
затем stdin/REPL CLI и оставшийся active-production audit.


D51 / PR18: installer-contract run 39311030990 / job 118260335941 on
b4df3f3926aa55d828367373945a128c04fc1636 failed on a stale UIA IsEnabled read in
the disposable InstallerUiProbe.Find fixture. Narrow ElementNotAvailableException
handling now skips retired windows/controls during discovery within the existing
wait budget. Invoke/Toggle, assertions, deadlines and production MSI are unchanged.
[Audit D51](SOURCE_AUDIT.ru.md) preserves the original FAIL and validation limits.
Linux source/diff checks PASS; real UIA/MSI is NOT_RUN locally and amended exact-head
Windows checks remain mandatory. The dashboard Rust code and prior 193-test proof
are unchanged.


D52 / PR18: Rust run 39311807867 / job 118263127720 on 9fe6fb30 failed the
console helper's 10s deadline after a 7.419s dynamic interop compile. The exact
final serialization/exit scheduling remains unknown; Probe has no state wait.
The disposable helper is now compiled once in separately bounded pre-Host setup;
actions keep 10s, state waits 2s and teardown 5s, with original PID/HWND checks,
actions and assertions. [Audit D52](SOURCE_AUDIT.ru.md) preserves failure evidence.
Windows execution must pass again at the amended exact head; portable checks do
not establish console/UI parity. Dashboard Rust behavior is unchanged.


D52 follow-up: 956498ab Rust run 39313953787 failed before Host startup with
Framework csc CS1504/source-not-found. The compiler now runs in the checked source
directory and receives literal `OwnedConsoleProbe.cs`; helper bytes, timeouts,
actions and assertions remain unchanged. Local source identity, relative-input
compile and missing-source/invalid-invocation checks PASS; actual Framework/Windows
execution remains pending. [Evidence and limits](SOURCE_AUDIT.ru.md).


D52 build-budget correction: 0743051d Rust run 39315553503 failed the NEW 10s
compiler setup cap before Host launched. That unsupported preparation assumption
is explicitly changed 10→60s as a separate bounded BUILD policy, with compiler
path/PID/start/elapsed/exit/output diagnostics and mandatory nonempty executable.
The original 10s action, 2s state, 5s Host stop and assertions remain unchanged;
there are no retries. Local negative setup checks fail before Host startup.
[Failure and validation limits](SOURCE_AUDIT.ru.md) retain the original failure;
actual Windows compiler duration and behavior remain required on the amended head.

D53 / resumed PR18: on head7c406efe Rust run39317752694 passed console_stop and
native integration but failed web_contract (15/16) with socket error10035 in the
malformed HTTP fixture. Each accepted fixture stream now explicitly enters blocking
mode before its existing 3s read timeout. A real-TCP fragmented-header regression
checks the exact request and response; all previous malformed/partial assertions
remain. Production I/O is unchanged. [Audit D53](SOURCE_AUDIT.ru.md) retains the
failure and distinction between observed error and inferred inheritance mechanism.
Legacy39317752797 and MSI39317752827 passed on the old head; all mandatory checks
must pass on the amended head before merge.

Resumed local Rust1.99.1 validation: workspace fmt --check, web_contract17/17,
icem-web all-targets Clippy locked/offline/-D warnings and git diff --check PASS.
Windows verdict remains separate; the prior E21 193-test count is historical.
