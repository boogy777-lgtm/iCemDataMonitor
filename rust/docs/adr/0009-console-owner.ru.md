# ADR 0009 — один владелец консоли Host

Статус: принято для E13; Windows runtime/operator acceptance ещё открыта.

Class: видимость консоли Host из startup/tray/SetConsoleVisible, а не консоли CLI или
emulator (`icem-platform-windows/src/console.rs`, `apps/host/src/main.rs`).
Inv: единственный mutable owner, свежий HWND и actual visibility после эффекта;
callback Ctrl+C только устанавливает atomic flag. Проверки: policy faults, RPC queue,
изолированный native child и headless Host process test.
N+1: новый источник команды использует Controller/Host queue; таблица шести состояний
проверяется целиком. Новое поведение OS control event требует отдельного review.

## Причина

Ранее tray и RPC независимо вызывали Win32; AllocConsole не восстанавливал std handles,
а возвращаемое ShowWindow значение могло быть ошибочно принято за успех. Legacy
`ConsoleVisibilityPolicy`, `ConsoleVisibilityService`, `ConsoleWindow` прочитаны вместе
с `ConsoleCloseGuard` и process probe tests. Сохраняется таблица None/Hidden/Visible ×
Show/Hide. Hide не отсоединяет консоль; повторный Show видимой консоли — no-op.

Legacy ConsoleCloseGuard утверждает X → hide и продолжение Host, возвращая TRUE из
CTRL_CLOSE handler. Это не поддерживаемая Windows гарантия: по документации
[HandlerRoutine](https://learn.microsoft.com/en-us/windows/console/handlerroutine)
CTRL_CLOSE завершает процесс и при TRUE. Нельзя объявлять такой порт guard-ом.
X → hide остаётся открытым требованием. Нужна другая архитектура окна, например
собственное окно или отдельный console process; она не включена в E13.

## Решение и граница FFI

`icem-windows-native::ConsoleControl` — !Send/!Sync RAII capability с process-local
atomic singleton. Без raw HWND/handle в прикладном коде. Mutable операции выполняет
Host service thread; read-only status/tray могут читать фактическую видимость.
HWND не кешируется. Demand attach создаёт собственную консоль; ERROR_ACCESS_DENIED
при windowless attachment допускает FreeConsole → AllocConsole этого процесса.
Намеренный safety delta: нет legacy fallback AttachConsole(parent), который мог бы
затронуть чужую консоль. Ошибка attach не маскируется успехом.

После AllocConsole переустанавливается handler (Alloc/Free сбрасывают таблицу),
открываются CONIN$/CONOUT$, три owned standard handles живут до Drop, UTF-8 задаётся
best effort. Hidden/born-hidden/minimized Show использует Hide/Show либо Restore,
затем best-effort foreground; успех означает видимость, а не гарантированный фокус.
Отказ OS или несовпадение actual/requested возвращается вызывающему коду.
Новая FFI находится только в локально разрешённом `console.rs` native crate;
workspace forbid и native deny/unsafe_op/undocumented_unsafe_blocks не ослабляются.
Используется уже выбранный windows-sys 0.61.2 и существующие features; зависимостей нет.

Handler установлен после победы существующего Host singleton и до config I/O.
Owner сбрасывает унаследованный process-local ignore-Ctrl+C после установки callback.
Для Ctrl+C callback выставляет atomic flag и возвращается без lock/alloc/log/window/wait.
Host потребляет сигнал на service loop и проходит прежний teardown capture, tray,
Shell, pipe, Web, Modbus и logger. Это graceful path, не гарантия верхней границы
времени всех существующих драйверов/teardown. Ctrl+Break, Close, logoff/shutdown
оставлены OS; это не новые гарантии консольного shutdown.
Ограничения handler table:
[SetConsoleCtrlHandler](https://learn.microsoft.com/en-us/windows/console/setconsolectrlhandler).

## Transport и ресурсы

Frozen SetConsoleVisible(bool) и HostStatus.console_visible не меняются: версия v1,
новый wire/control contract не нужен. SetConsoleVisible задаёт состояние;
SetLayer по-прежнему legacy toggle. RPC ожидает ack owner через bounded queue 16,
deadline 5 s, затем возвращает actual GetStatus. Timeout/disconnect после начала
эффекта означает неизвестный результат: автоматического повторения нет, следующий
read-only status сверяет truth. Отменённая до исполнения команда пропускается.
Tray использует тот же Controller; callback Ctrl+C не создаёт очередь/потоков.
`--autostart` скрывает консоль после singleton; CLI queries/help и проигравший
singleton contender её не изменяют. Reconnect не запускает Host повторно.

E12 log ownership, E11 config/ensure-host, AddIn/Data.dat, native MSI/Repair/force,
independent emulator и GPUI mapping 176 + 6 не изменяются.
