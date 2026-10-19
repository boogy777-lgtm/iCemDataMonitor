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
