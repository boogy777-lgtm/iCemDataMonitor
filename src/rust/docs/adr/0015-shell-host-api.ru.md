# ADR 0015 — типизированный Shell → Host

Class: операции Shell (`icem-web::shell`, `apps/shell`, `icem-desktop::{host,monitor}`);
emulator имеет самостоятельный контракт.
Inv: обязательные bool, проверенный Layer/Selection до RPC и проверенный status;
exhaustive request/reply handling, portable model и Windows reconnect/process tests.
N+1: новый вариант требует operation mapping, RPC match и presentation match;
worker/queues/lifecycle не знают предметных команд.

`host::Port` принимает Request и возвращает Response. Generated UiPrefs, LayerState,
BootStage, MetricsSample и NavigateUiResponse передаются напрямую. Status требует
layers и ровно по одной COM/LAN/RAW зоне, хранит зоны в фиксированном порядке.
Неполный protobuf или mismatch ответа не подтверждает connected/fresh.
Старый Model::apply("status", {ok:true}) также больше не подтверждает connected.

Theme/Console/Autostart требуют bool. Layer исключает unspecified/unknown. Selection
проверяет cap=298/even/4…598 до queue. SetLayer сохраняет toggle/desired_state=false.
Console/Autostart передают фактический HostStatus. Metrics сохраняет oneof/timestamp,
int/float, NaN/Infinity и прежнее decimal formatting. Error/missing timestamp сохраняет
last-known, не создаёт sample. Boot replay/reset/fault clearing и Session disposal
сохранены. Ensure result хранится вне Session: reconnect не запускает процессы.
NavigateUi(section=0) — consuming mutation, один attempt per explicit demand.

worker::Worker<Q,R> выделяет прежний bounded цикл, без dispatcher/domain knowledge.
Host и emulator используют один код 16/16 queues, stop/backpressure/WorkerCompletion.
Drop не ждёт I/O; completion после UI loop завершает started work, queue отбрасывается.
Нового manager/listener нет. Logging renderer/owner JSON и dashboard REST payload
остаются изолированными seams до следующих инкрементов. LogCommand уже требует
SetEnabled(bool), но пока проецируется в прежний owner API. Emulator Value не изменён.

Добавлен только edge desktop→существующий web crate, без новых packages/versions/features;
Shell больше не зависит напрямую от serde_json. Lockfile отражает эти связи.
Unsafe/lints/pins прежние. Original XAML/themes/inventory прочитаны; layout/palette и
mapping 176+6 прежние. Visual/operator/SDK/hardware gates остаются открытыми.
