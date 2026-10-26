# ADR 0018 — проверенные снимки native dashboard

Class: ответы действующих `/api/regs` и `/api/regs/history` для native Shell в
`icem-web::dashboard_client`; другие REST routes и protobuf metrics вне класса.
Inv: private checked Registers/History допускают публикацию только полного валидного
снимка; executable проверки — web_contract и desktop/tests/dashboard.rs.
N+1: поле меняет wire DTO, TryFrom projection и contract fixture; bounded worker,
Host owner и core не получают дополнительных protocol branches.

## Решение

Shell Response::Dashboard переносит owned Read { registers, history: Result }, а не
Value. Payload хранится в Box: Read занимает 400 bytes и не должен увеличивать
каждый элемент bounded Shell reply queue; одна allocation на dashboard reply.
Exhaustive constructors/consumer и workspace Clippy проверяют representation без
lint allowances. Единственный HTTP adapter десериализует serde DTO и проверяет все четыре
system scalars, 98/100/100 пар COM/LAN/RAW, base/len/index/address/end и вид значения.
Address принимает только чётные 4..598; этот predicate используют Selection, REST
history validation и desktop selection. Имена/labels остаются presentation data.
System value — u16; пары float — finite f64 после прежнего REST widening f32;
Unix register 4 — UTC seconds в signed i32, без долей секунды. Original checked text
сохраняется для display, включая отличающиеся форматы register/history timestamps.

Required nullable name/label/updatedAtUtc отличаются от отсутствующего поля.
System liveness обязан быть null. Fresh требует Some UTC updatedAtUtc, LastGood —
null. Liveness требует bool producer flag/active, nonnegative i64 age и u32 count;
active == producerFlag && ageMs < 5000 проверяется по действующему producer правилу.
History проверяет каждый requested key и все samples, отвергает лишние/отсутствующие
keys, nonfinite values и неверное UTC время; хранит последние 600 samples на series.
Wire HTTP сохраняет прежние loopback-only, 16 MiB response и 3 s request bounds.
Empty selection не делает history request; Selection проверяется до I/O.

Desktop хранит Option<Registers>, поэтому loaded выводится из наличия проверенного
снимка. Хорошие registers публикуются независимо от history failure; прежняя good
history сохраняется при ошибке. History проверяется против request selection, а
при публикации фильтруется по current selection: in-flight ответ не восстанавливает
снятую series и не считает новый выбор ошибкой старого запроса. ProtoMetrics остаётся
самостоятельным typed потоком; dead Value apply_metrics/request paths удалены после
проверки call sites. IEEE NaN/Infinity text и прежняя f32 display precision policy
этого потока сохраняются; REST nonfinite по-прежнему возвращает HTTP 500.

## Совместимость и проверка

REST serialization/routes, frozen SPA/proto, palette/layout/chart, E14 lifecycle и
176+6 mapping не меняются. Original Shell XAML/themes и inventory сверены перед
изменением accessors renderer; physical visual parity этим не устанавливается.
Нет нового listener/Engine/manager/bus. Добавлен прямой edge icem-web → уже locked
serde 1.0.229 (MIT OR Apache-2.0, std/derive); features/versions/checksums graph и
unsafe policy прежние. External serde implementation остаётся dependency boundary.

Actual-server tests проверяют complete 4+298 rows, f32 widened registers против
shortest f32 history numbers, signed Unix extremes, stale timestamp/nullability,
HTTP/nonfinite errors. Malformed HTTP-200 fixtures, geometry/kind/value/liveness
rejects, history 700→600 и actual apply_host partial-retention/current-selection
regressions проверяют границу. Windows/MSVC exact-head gate обязателен отдельно.
Physical keyboard/focus/DPI/scroll/drafts/X/Open, SDK/AddIn/COM/Npcap и installation
остаются NOT_RUN; selftest/landiag/stdin CLI — следующие отдельные increments.


D51 / PR18: installer-contract run 39311030990 / job 118260335941 on
b4df3f3926aa55d828367373945a128c04fc1636 failed on a stale UIA IsEnabled read in
the disposable InstallerUiProbe.Find fixture. Narrow ElementNotAvailableException
handling now skips retired windows/controls during discovery within the existing
wait budget. Invoke/Toggle, assertions, deadlines and production MSI are unchanged.
[Audit D51](../SOURCE_AUDIT.ru.md) preserves the original FAIL and validation limits.
Linux source/diff checks PASS; real UIA/MSI is NOT_RUN locally and amended exact-head
Windows checks remain mandatory. The dashboard Rust code and prior 193-test proof
are unchanged.


D52 / PR18: Rust run 39311807867 / job 118263127720 on 9fe6fb30 failed the
console helper's 10s deadline after a 7.419s dynamic interop compile. The exact
final serialization/exit scheduling remains unknown; Probe has no state wait.
The disposable helper is now compiled once in separately bounded pre-Host setup;
actions keep 10s, state waits 2s and teardown 5s, with original PID/HWND checks,
actions and assertions. [Audit D52](../SOURCE_AUDIT.ru.md) preserves failure evidence.
Windows execution must pass again at the amended exact head; portable checks do
not establish console/UI parity. Dashboard Rust behavior is unchanged.


D52 follow-up: 956498ab Rust run 39313953787 failed before Host startup with
Framework csc CS1504/source-not-found. The compiler now runs in the checked source
directory and receives literal `OwnedConsoleProbe.cs`; helper bytes, timeouts,
actions and assertions remain unchanged. Local source identity, relative-input
compile and missing-source/invalid-invocation checks PASS; actual Framework/Windows
execution remains pending. [Evidence and limits](../SOURCE_AUDIT.ru.md).


D52 build-budget correction: 0743051d Rust run 39315553503 failed the NEW 10s
compiler setup cap before Host launched. That unsupported preparation assumption
is explicitly changed 10→60s as a separate bounded BUILD policy, with compiler
path/PID/start/elapsed/exit/output diagnostics and mandatory nonempty executable.
The original 10s action, 2s state, 5s Host stop and assertions remain unchanged;
there are no retries. Local negative setup checks fail before Host startup.
[Failure and validation limits](../SOURCE_AUDIT.ru.md) retain the original failure;
actual Windows compiler duration and behavior remain required on the amended head.
