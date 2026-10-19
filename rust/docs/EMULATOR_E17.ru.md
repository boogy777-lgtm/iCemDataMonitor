# E17 — application API и watch клиента emulator

База main: `1b06a19888ccc5f4d450f501dd87ab2594c0ecd7`, tree
`f6aacd0010000d897a393e2e41fee4b2bcf81618`. Post-merge Rust/MSVC
[39207160572](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/39207160572)
и Legacy [39207160513](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/39207160513)
завершились SUCCESS. Открытых PR/неоконченных продолжений при старте нет.
Ветка `rust/emulator-application-api`; владелец разрешил merge после успешных проверок.

[ADR 0014](adr/0014-emulator-application-api.ru.md) фиксирует Class/Inv/N+1,
типизацию, границу v1 и новые внутренние исходы. Все 21 команды проходят через
один application match и прежний Engine. Typed GPUI не выполняет string JSON
roundtrip; периодический read не повторяет failed save. Presentation data остаются
owned Value для существующих views; это не внешний wire contract.

Внутренний API/UI отличает Rejected, Applied, AppliedNotPersisted и partial effects;
pipe v1 сохраняет исторический envelope. Его ok не обещает disk persistence.
Compatibility parser сохраняет bool/fractional/NaN coercions. Typed callers имеют
finite NativeValue, enum channel/preset и u32 equipment/SV. JSON создаёт serializer.

CLI использует общий bounded subscription consumer. Text/JSON watch получает ACK,
затем 1 Hz status того же server/engine. Ctrl+C отменяет watch, а не engine.
Connect/write/ACK deadline=3 s; watch data deadline=3 s. Accepted commands не
ретраятся, в том числе после timeout/потери ответа. Unknown доступен typed client,
operator text сохраняет категорию/исход. stdout JSON; exit0 success/cancel watch,
exit1 command/client error, exit2 usage. Сохранены singleton, exclusive bind,
pre-engine CLI dispatch, Quit ACK/drain и E14 completion.

Добавлены software проверки:

- Полнота 21 typed/v1 команд, backend traces и согласованность presentation data;
  existing original C# differential и malformed/IEEE characterization сохранены.
- Typed finite/budget rejection до backend эффекта; apply/save failure/read-no-retry.
- Watch ACK + два status для обеих форм, explicit cancel во время ACK/data wait,
  idle/write/ACK deadlines, invalid/oversized/truncated/UTF8/wrong-stream frames.
- Потеря mutation reply даёт Unknown, backend counter=1 без replay.
- E14 typed in-flight завершается после close; queued typed/log mutations отброшены.
- Windows pipe subscription/rebind и реальный engine→CLI text/JSON watch; JSON stdout,
  exit0/1/2, save failure с фактическим memory state, singleton/Quit/disconnected Quit.

Exact-head fmt/check/build/test/Clippy, baseline/browser/mapping/native/MSI результаты
фиксируются в PR после CI. До завершения run они не считаются PASS. Локальный Linux
не исполняет Windows-only тесты и не является Windows acceptance.

Не закрыты: retained emulator X/Open ×3 с unsaved input/scroll/selection, физические
keyboard/focus/DPI/session и COM/Npcap/AddIn, SDK release/install/reboot/rollback/offline
и measurements. SDK runner/artifact не подтверждён; fixture MSI не публикуется.
Host E11–E16, logs/typed LogFile/pins, SetLayer toggle и frozen legacy/proto/web
сохраняются. Capture-to-file и D22 остаются отдельными scope/compatibility решениями.
Следующий код: app selftest/landiag; legacy selfTest:null не является пробелом.


Class: Rust payload changes в `.github/workflows/rust-windows-release.yml`;
docs вне класса. Inv: apps/crates/Cargo/toolchain запускают существующий native MSI
contract job. N+1: новый модуль в этих roots не требует изменения filter.
Раньше runtime-only PR пропускал этот job. Расширен только pull_request paths;
manual SDK/release conditions и отсутствие fixture publication сохранены.
