# Host E10: пассивный capture owner

База main: `1f205c1abfdef187821235bd5c37aa085aa6f415` (merge PR #5).
Rust push CI 39093573051 SUCCESS; legacy push CI 39093573086 FAILURE на sampler
и console-show тестах. E10 не меняет legacy. PR #4 `rust/windows-release`
остаётся отдельной работой; installer/упаковщик не затронуты. E10 не сливать без команды.

Class: passive capture (`icem-platform-windows::sniffer`, `apps/host::{main,layers}`);
emulator ACE emitter и bus writer вне класса.
Inv: один владелец handle, nonblocking/bounded receive, фактические states,
pause/fault не RUNNING; capture/layer/RPC/model/native ABI tests проверяют контракт.
N+1: новый CaptureDevice использует тот же lifecycle; новый слой использует прежнюю
Host queue. Core/proto/emulator не приобретают capture-ветвлений.

## Реализованный путь

`SystemCapture` действительно загружает Npcap, перечисляет адаптеры, выбирает Ethernet
и открывает promiscuous DLT_EN10MB capture. Настраивает 64 KiB kernel/user buffers,
minimum-to-copy=1, nonblocking и BPF `ether multicast`. `CaptureSession` имеет только
receive API; send emitter продолжает пользоваться отдельным `PacketSession`.
Capture-only exports разрешаются только при open_capture: emitter load не зависит
от новых capture capabilities. Подробности FFI: [ADR 0007](adr/0007-host-capture.ru.md).

Capture живёт на уже существующем сериализованном Host owner. Нет нового worker,
frame channel, detached task или join. Один poll читает максимум 64 owned packets
по 65535 bytes, не ожидает трафик, затем возвращает управление Host. Пакеты не хранятся.
MAC set ограничен 1024; после переполнения distinctMacs — нижняя граница,
macLimitReached=true. Счётчики насыщаются; rate/age используют monotonic time.
Неизвестное время последнего frame = -1. Idle rate падает до 0.

| Событие | Состояние и ownership |
|---|---|
| До успешного open | STARTING, unavailable, не RUNNING |
| Open + настройка успешны | RUNNING, available, handle принадлежит capture |
| Pause | PAUSED, available; receive продолжает drain/discard, counters не растут |
| Resume | RUNNING на том же handle; counters сохраняются |
| Ошибка open/read/EOF | FAULT, unavailable, close; counters/последний adapter сохраняются |
| Read-only reconnect | Не раньше 30 s после fault; один poll, без циклов немедленного retry; processing gate сохраняется |
| Stop/Drop | STOPPED, unavailable; close до server teardown, повторный stop идемпотентен |

Пауза не хранит application backlog. Данные, ещё находящиеся в bounded native buffers
при resume, не имеют отдельной временной отсечки. Packet-lossless capture и точность
kernel timestamp не заявляются; rate/age — время наблюдения Host.

Native enumerate/open/close остаются синхронными вызовами драйвера. Nonblocking
гарантирует отсутствие ожидания пакета в next_ex; batch ограничивает число вызовов,
но не доказывает wall-clock deadline внутри неисправного драйвера. RPC имеет прежний
deadline 5 s и queue 16; оно не может принудительно отменить зависший OS call.
Аппаратная latency/driver-hang приёмка остаётся открытой. Более строгая изоляция
потребует отдельного процесса или поддержанного драйвером cancellation contract.

## Legacy semantics

Прочитаны AceSnifferService, SharpPcapAceSource, HostComposition, DefaultLayerActuator,
LayerRuntimeController, telemetry.proto и Shell XAML/themes.
Устаревший комментарий manual-only не соответствует коду. По умолчанию выбирается
первый Intel/Ethernet/Gigabit/Realtek/Broadcom, исключая loopback/WAN Miniport.
`--sniffer-adapter TEXT` development Host задаёт name/description override.
Без override успешное отсутствие адаптера не симулируется: FAULT/unavailable.

Как active legacy composition, capture создаётся только с загруженным registry.
Development fixture без station config/явного adapter override физический NIC не открывает.
`--station-config PATH --registry PATH` включает реальный путь; registry ищется также
относительно EXE, как раньше. Config recovery и production argv не входят в E10.

D06 сохранён: startup capture запускается даже при lansniffer_on=0. Session plan
и actual gate различны. При startup plan=false первый toggle ставит plan=true,
capture остаётся RUNNING; следующий toggle ставит PAUSED. Это проверяет regression.
SetLayer desired_state по-прежнему игнорируется. Мутации автоматически не ретраятся.

Witness ACE predicate сохраняет `AceSnifferService.IsAceFrame`: ровно 152 bytes,
LLC 03/03/08 либо 09. Это слабее emitter parser; тест содержит такой frame.
TotalFrames считает обработанный multicast; AceFrames — witness subset.
Decode/signals/equipment/vector catalog и file capture здесь не реализованы.

## Host / RPC / UI

Sniffer toggle идёт через прежний LayerRequest owner. Отсутствующий/неисправный
capture не меняет plan/INI и даёт last_error. Успешный toggle публикует actual state
до ACK. SetAsDefault сохраняет aggregate Modbus/Web/sniffer plan через прежний путь;
save failure сохраняет runtime plan, last-saved projection и видимую ошибку раздельно.
OPC external no-op и startup-effective Web semantics E8 не менялись.

Owned HostSniffer DTO копируется в WebState после каждого poll и toggle. gRPC
sniffer отражает RUNNING; sniffer_available — открытый исправный backend, включая
PAUSED. В legacy это была DI-registration; более строгая availability — намеренное
уточнение для запроса пользователя. Отсутствие пакетов не снимает availability.
Driver error видна в GetStatus.last_error, если нет отдельной ошибки последней операции.
Прежняя операция остаётся sticky до следующей операции; reconnect её не скрывает.

Активный GET /api/status/summary получает snifferStats с прежними семью полями
и additive adapter/macLimitReached. Dormant sniffer REST/file endpoints не активированы.
Shell сохраняет availability/busy и ответ LayerState; Model и renderer отключают
SnifferSwitch при down/unavailable/busy. Click и Enter/Space используют общий enabled
action; disabled не участвует в tab stops. Ошибка видна; last-known facts не стираются.
Полная Windows GPUI/focus/visual приёмка остаётся NOT_RUN. Mapping 176/176 — inventory.

## Доказательства и границы

Software tests покрывают bounded busy receive, empty traffic/rate, witness predicate,
pause/drain/resume, fault/EOF/open failure, retry boundary, retained processing gate,
stop/drop, MAC cap и D06. Host tests используют настоящий Capture lifecycle с injected
frames и реальные INI/filesystem failures. RPC tests проверяют projection и доставку
двух одинаковых desired_state через existing owner queue; это contract seam, не Npcap.
Windows native ABI shims исполняют production setup и проверяют error cleanup,
buffer/nonblocking/filter настройки и exactly-one filter free/session close.
Windows process test проверяет честный unavailable LayerState fixture-only Host.

Без новых dependencies, изменения lockfile, core, canonical proto, legacy, AddIn,
SPA или иконок. В capture API нет ссылки на bus/file writer. AddIn остаётся единственным
писателем MMF/Data.dat; Host не создаёт producer file и не меняет ACL.

Локальные команды: fetch --locked; Verify-Scaffold (fmt/check/build/test/Clippy offline,
25 baseline guard cases, 12 exact approvals, mapping); Windows GNU native/platform
all-targets Clippy; Chrome/theme walkthrough. Полный 513/513 baseline и MSVC verdict
выполняются на GitHub checkout. Локальная копия не содержит единственный original PCAP,
он не подменяется и не удаляется из GitHub tree. Точные результаты/commit/CI — в PR.

Открыты: real NIC/Npcap/ACL/elevation и driver latency, decode/file capture, Windows
operator/DPI/IME/Explorer/timing/visual parity и MSI. Следующий Host этап — production
CLI/ensure-host, config recovery, затем console/window lifecycle.
