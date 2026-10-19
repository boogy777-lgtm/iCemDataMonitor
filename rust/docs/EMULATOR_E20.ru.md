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
