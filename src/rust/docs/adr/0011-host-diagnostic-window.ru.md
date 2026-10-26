# ADR 0011 — окно диагностики Host вместо conhost

Статус: реализуется E15; Windows evidence фиксируется в PR. Уточняет ADR 0009.

Class: окно диагностики работающего Host (`icem-windows-native/src/console.rs`),
а не stdout CLI и не GPUI emulator. Inv: один owner HWND, X → Hide без console
attachment, actual-state ack и bounded lifecycle; native child/Host pipe regressions.
N+1: startup/tray/RPC используют прежний Controller, новые источники не владеют HWND.

## Выбор

CTRL_CLOSE завершает каждый присоединённый процесс даже при TRUE handler. Поэтому
Host не остаётся присоединённым к консоли. Legacy ConsoleCloseGuard не копируется.
Выбрано собственное Win32 окно в том же процессе. WM_CLOSE/Alt+F4 скрывает его;
оно не уничтожается и не посылает CTRL_CLOSE. Host не ищет и не меняет чужие окна,
не subclass-ит conhost. FreeConsole отсоединяет только вызывающий процесс. Если
консоль разделяют другие процессы, она остаётся им. CLI/help/проигравший singleton
выполняются до detach и создания owner, stdout/exit codes сохранены.

Отклонён отдельный console helper: добавил бы child/pipe/EOF/reap/recovery и shipping
contract; X завершал бы helper вместо настоящего Hide. ConPTY добавил бы ещё один
terminal runtime и transport без необходимого инварианта. GUI subsystem для всего
EXE изменил бы запуск CLI. Эти механизмы здесь не требуются.

Ctrl+C **в окне диагностики Host** (`WM_CHAR=3`, от TranslateMessage) выставляет
прежний atomic stop; service loop выполняет прежний graceful teardown. Это уже не
Windows console и не терминал/интерпретатор команд. Ctrl+C в оставшемся родительском
терминале после detach не адресован Host. GenerateConsoleCtrlEvent/AttachConsole(PID)
больше не являются control API Host. Это явная смена OS input path, необходимая для
X → Hide; frozen RPC, explicit stop/restart и UI mapping не меняются. Заголовок окна
объясняет X/Ctrl+C. WM_QUERYENDSESSION не блокирует logoff/shutdown; WM_ENDSESSION
просит graceful stop. Завершение к deadline OS и reboot остаются отдельным gate.

## Владение и ресурсы

Один !Send/!Sync ConsoleControl живёт на service thread. Единственный выделенный
window thread создаёт/меняет/уничтожает HWND и дочерний readonly multiline EDIT.
Класс зарегистрирован и снят этим потоком; child уничтожается с parent. Нет borrowed
Rust pointers в WndProc/userdata, unsafe impl Send/Sync или новых FFI modules.
Публикуемый atomic HWND — только для read-only actual-state probes; все effects идут
к собственному HWND на создающем потоке. Потерянное окно не создаётся повторно:
Show/Hide возвращает ошибку, сервисы продолжают работать. Нового Host/helper spawn нет.

UI не выполняет файловый/pipe/network I/O и не вызывает logger. Очередь visibility
16, nonblocking enqueue, ack/deadline 2 s, протухший запрос не выполняется. RPC queue
16/5 s сохранена; потерянный ответ не повторяет мутацию. Status/tray читают actual
HWND visibility; Show восстанавливает minimized, также когда visible уже true.
Startup сохраняет initial visible inherited console, --autostart/headless скрыт.
Restart передаёт только текущий initial visibility demand через process-local env;
это не сохранённая настройка и не второй owner состояния.

Diagnostic transcript отдельно от E12 файлового logger: очередь 64 × максимум 1026
UTF-16 units, tail максимум 65536 units после batch; overflow явно отмечается в тексте.
UI получает только owned строки. File logging OFF не выключает экран, Clear файлов
не очищает экран и не запускает emulator. Host service diagnostics best-effort также
пишут stderr (redirected pipes/files), не stdout. Нет recursive logger errors.

Explicit finish уничтожает окно до restart, ожидает максимум 2 s, затем сообщает
ошибку и блокирует successor spawn. Drop повторно не запускает мутаций; при зависании
OS thread остаётся process-owned до exit, без helper/orphan process. Обычный shutdown
join подтверждает освобождение HWND/class/queue. OS-hung window thread не объявляется
устранимым безопасным force-destroy с чужого потока.

## Dependency/appearance review

windows-sys 0.61.2 уже выбран; добавлен только feature Win32_UI_Controls для EDIT
message constants. License MIT OR Apache-2.0, lockfile versions/checksums прежние.
Прочитаны original Shell/Emulator XAML/themes и inventory. GPUI appearance и mapping
176 legacy + 6 logging controls прежние. Новое отдельное Host diagnostic surface:
parent window + readonly transcript/scrollbars, описанные в mapping host_console.
Это OS diagnostic surface, не замена Apple controls. Visual/DPI/focus acceptance
не доказывается mapping completeness или message-level tests.

Первичные источники:
- https://learn.microsoft.com/en-us/windows/console/handlerroutine
- https://learn.microsoft.com/en-us/windows/console/freeconsole
- https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-close
