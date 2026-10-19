# E20 — типизированные данные ответов emulator

База E19: c1c4c0daf2addc59d517e853d4a8faa3a0ba5922 / tree
8b70979f512a5e0daeac3025a6d4a97d3d4199c9. Во время работы отдельно слит PR16,
main 8f835aaf148ce0a120503d2a9a5884b8d778e606: только MetricsSamplerTests harness
D48; E20 backend checkpoint объединён с этой базой без потери кода. Production
.NET, frozen baseline/proto/web, toolchain/dependencies/lints/unsafe policy прежние.

[ADR0017](adr/0017-typed-emulator-payloads.ru.md) фиксирует Class/Inv/N+1 и границы.
Все 21 backend responses теперь имеют собственные предметные payload families.
Status/vars сохраняют numeric/nullability/metadata, profiles — device identity,
endpoints — actual open facts, operation replies — прежние эффекты. Generic result
имеет private fields и не допускает смешанного success/error/data состояния.
Единственный execute и прежний Engine owner сохранены.

GPUI получает Execution, а не Value. Derived View хранит typed Status и прежние
formatted strings. Неверный command/family reply не публикует status/options;
Signal identity сохраняет SV без повторного разбора hex. Snapshot Value, ui_value и
неиспользуемый string/JSON Model.apply dispatcher удалены. Тесты Host boot/navigation/
metrics переведены на активный typed apply_host с сохранением прежних assertions.
D49 исправляет только прежние status display reads в dashboard, не REST adapter.

Wire v1 не изменён. Save failure оставляет ok=true (applied in memory), while internal
Outcome=AppliedNotPersisted. Partial endpoint/COM effects и Unknown/no-retry сохранены.
NativeValue finite input не ужесточает legacy parser. Nonfinite output остаётся f64
до v1 null/UI «—»; selfTest:null остаётся. Полный E19 golden проверяет 43 frames и отдельный direct-backend unknown-vars data:{},
включая все 21 команды; C# parser oracle использует domain-shaped synthetic backend
и по-прежнему сравнивает весь envelope/data/method/exact IEEE args/ACK.

Software tests: 21 typed/v1/presentation completeness, compile-fail family/private
result boundary, mismatch rejection, escaping/nonfinite display, typed selection,
full E19 wire golden, save failure/read-no-retry/partial effects, прежние transport
watch ACK/status/cancel/deadline/no-replay и E14 discard/in-flight completion.
Control differential report включён в существующий CI artifact.

Локально pinned Rust1.99.1 Linux fmt/check/build/test/Clippy locked/offline PASS:
workspace check --all-targets, workspace build, workspace test (185 passed, включая
2 compile-fail doctests), workspace Clippy --all-targets -D warnings. Усиленные
nonfinite/unknown-vars runtime assertions отдельно перепроверены после общего gate. Windows/MSVC, original C# oracle и physical
operator/hardware gates этим запуском не считаются пройденными. 176+6 mapping,
colors/geometry неизменны; PowerShell mapping guard выполняется Windows CI.
Physical drafts/scroll/keyboard/focus/DPI/session, real iCem/AddIn/COM/Npcap и SDK
release/install NOT_RUN. D22, capture-to-file, external OPC DA no-op остаются прежними.
Следующие отдельные инкременты: selftest/landiag и dashboard REST payload.


D50 / PR17 Windows follow-up: first exact-head MSVC run 39265963972 failed only the
unit-summary order regression before the remaining gate completed; its FAIL remains
recorded. Legacy 39265964072 (941 tests + MSI smoke) and installer contracts
39265964048 passed on c4bbc1fa. Typed display now uses an ordered private Serialize
subject matching E19 Windows source order, not a sorted map; literal-string tests
cover escaping/nulls/all fields independently of serde_json/preserve_order. See
[SOURCE_AUDIT D50](SOURCE_AUDIT.ru.md) and ADR0017. Wire payload/golden and existing
E20 proofs are unchanged. Corrected exact-head Windows verdict remains a separate gate.

Локальный D50 gate (Rust1.99.1 Linux, locked/offline): desktop model tests 13/13
в default graph и 13/13 с serde_json/preserve_order; desktop Clippy --all-targets
с preserve_order / -D warnings и workspace fmt --check PASS. Это проверка обеих
formatter configurations, не подмена нового Windows/MSVC exact-head gate.


D46 recurrence on exact 1111e3c9c7bb56ad123e7c254bb1b621ad05a640:
Rust/MSVC 39268706746 (Scaffold, C# differential and Chrome) and installer contracts
39268706847 PASS; Legacy 39268707217 / job 118115014371 FAIL at the virtual-port
retry wait. State RUNNING / attempt 2 at catch does not establish retry timing before
the 15s deadline. [SOURCE_AUDIT D46 follow-up](SOURCE_AUDIT.ru.md) records the original
failure, controlled runtime 10.0.13 completion-race experiment and separate Linux
focused passes. Same-stack fixture cancellation follows the expected second signal,
retaining actual 8s retry, original assertions and 15s/10s bounds, plus no-third-attempt
assertion and precise recurrence diagnostics. Production .NET remains unchanged;
existing 14-entry ledger cites source commit b4211289e6f94fb5a09f493563bf1fa12e92d0f0.
This is fixture cleanup with demonstrated mechanism, not proof of the exact CI
scheduling cause. All Windows gates must pass again on the final amended PR17 head.

Local focused Linux checks on runtime 10.0.13: baseline and cancellation comparison
passed with actual retry near 8s; the exact staged SerialTee test also passed once,
including no-third-attempt assertion. These are not Windows integration verdicts.
