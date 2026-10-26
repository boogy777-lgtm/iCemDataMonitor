# Host E9: boot replay, cancellation и navigation faults

Исторический отчёт указанного инкремента. Текущее software-состояние:
[N+1 tracker](NPLUS1_TRACKER.ru.md), [implementation status](IMPLEMENTATION_STATUS.ru.md).
Прежние NEXT/PARTIAL описывают тот checkpoint; hardware/UI/operator/release gates
остаются отдельными. [C11](../../docs/LEGACY_REFACTORING_C11.ru.md) удалил dormant
REST/serial wrappers и их эксклюзивные tests; com0com driver сохранён.

База: main `12303e1e79d43d8011b1812cbfb2f783f3108b00`, Rust CI
39066331105 SUCCESS. Legacy CI 39066333747 FAILURE отмечен отдельно: диагностика,
исправления и повторные запуски legacy CI не входят в этот инкремент.
PR этого этапа не должен сливаться без отдельной команды владельца.

Class: boot subscription и consuming navigation Shell (`apps/shell`,
`icem-desktop::{lib,monitor,navigation}`, `icem-web::rpc`); telemetry/layers вне класса.
Inv: новый replay не наследует READY, принятый consume не ретраится по ошибке,
shutdown отменяет backpressured boot send; model/policy/RPC/Windows pipe fault tests.
N+1: navigation demand имеет один policy owner; все boot sends используют общий
cancellation helper, canonical proto и независимый emulator не меняются.

## Boot lifecycle

Monitor продолжает polling существующей boot-подписки после READY (один outstanding
запрос, прежний poll budget 100 ms / максимум 128 сообщений). Иначе быстрый restart,
полностью прошедший между status probes, оставлял старый READY навсегда.
Каждая новая подписка возвращает внутренний `data.reset=true`: модель заменяет старые
стадии, включая пустой или неполный replay. Live batches дополняют текущий replay.
Это внутреннее представление UiPort, не изменение gRPC/proto.

Boot error/EOF/lag сразу очищает READY и стадии, сохраняя boot_error. Ошибка status
также инвалидирует boot view. Shell при ошибке boot/status освобождает всю Session:
обе подписки и Tokio runtime с background transport tasks/pipe handles. Следующий
read-only poll создаёт новый session/replay. Это предотвращает удержание старой pipe
при exclusive bind следующего Host. Метрики last-known остаются в модели с их timestamp;
новая подписка получает полный первый sample. Мутации при этом не повторяются.

Host использует прежние limits: 64 стадии, broadcast и response buffer по 128,
общий лимит 64 streams. Replay/live/lag-error send теперь наблюдает shutdown даже
при заполненной response queue. Drop downstream также завершает send; permit
освобождается вместе с task. Подписка после установленного shutdown отвергается
UNAVAILABLE. Lag по-прежнему RESOURCE_EXHAUSTED и требует нового replay.

Отдельный process identity в frozen proto отсутствует. Смена процесса обнаруживается
по ошибке/EOF старого transport, затем заменяется replay; мгновенная идентификация
процесса не заявляется. Инициализация Host до bind IPC остаётся отдельным startup gate.

## NavigateUi(section=0): явная политика неопределённого исхода

Legacy query — read-and-clear, то есть мутация. Exactly-once восстановление потерянного
ответа без operation ID/ACK невозможно; canonical proto сохраняется.

| Событие | Действие Shell |
|---|---|
| Первый запуск Shell | Один demand, ожидающий успешного status connection |
| Bridge queue full/disconnected до принятия | Demand сохраняется: запрос заведомо не отправлен |
| Bridge принял запрос | Demand снят до ответа, максимум один in-flight consume |
| Ошибка connect/RPC или потеря ответа после consume | Текущая вкладка сохранена, видимая ошибка «результат неизвестен»; автоматического повтора нет |
| Status failure/reconnect | Не создаёт нового navigation demand |
| Новый SHELL_SHOW | Новый explicit demand; события coalesce как last-write-wins pending Host |
| Show во время предыдущего запроса | Новый demand сохраняется после старого ответа |

Это at-most-one **attempt per demand**, не гарантия доставки перехода. При потерянном
ответе один переход может потеряться; повторный выбор раздела/show задаёт новый запрос.
Shell не читает pending постоянно и не компенсирует неопределённый исход скрытой
мутацией. Host pending/query/signalling и значения enum не изменены.

## Доказательства и границы

- Desktop policy fault: ответ теряется после read-and-clear; следующий pending
  переживает status/reconnect, потребляется только новым show. Queue refusal,
  disconnected startup и show-during-flight проверяются отдельно.
- Model: boot errors, новый неполный/пустой replay и live completion; старый READY
  не наследуется даже без failed status probe.
- Host RPC: deterministic broadcast overflow → lag error → replay → live;
  cancellation при полном send buffer, stream cap и возврат permits при drop/shutdown.
- Windows Shell adapter: четыре stop/rebind цикла настоящего named pipe, boot/status
  errors, освобождение Session и replay другого WebState без прежнего READY.
  Это transport-owner restart test в одном test process, не аппаратная приёмка.
- Windows fault использует настоящий `Navigation` owner и production `NavigationPoll`:
  ответ отбрасывается между consume и применением Shell. Потеря пакета ОС не имитируется.

Команды: cargo fetch --locked, затем fmt/check/build/test/Clippy --locked --offline;
актуальные baseline/mapping/guard-сценарии и browser/theme checks. Результаты exact-head
Windows MSVC/Linux CI записываются в PR. Локальный Linux не исполняет Windows tests.

Без новых зависимостей, изменения Cargo.lock, legacy, canonical proto, SPA или native
unsafe boundary. Mapping остаётся 176/176 inventory, visual_parity NOT_PROVEN.
Sniffer, production CLI/ensure-host/config recovery, console-close, полные GPUI control
states, аппаратные MMF/ACL/COM/Npcap/DPI/IME/Explorer/timing и MSI остаются открытыми.
Следующий самостоятельный Host-инкремент — read-only sniffer capture owner и actual-state
projection с availability; подробный backlog сохраняется в HOST_E8 и MIGRATION_PLAN.
