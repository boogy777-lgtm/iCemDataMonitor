# ADR 0010 — окно emulator и завершение UI bridge

Статус: E14; реальная Windows UI-приёмка открыта.

Class: Show/Hide окна GPUI текущего процесса; conhost вне класса
(`icem-windows-native/src/owned_window.rs`, `icem-desktop/src/windows.rs`).
Inv: borrowed handle проверяется по IsWindow/PID и не кешируется; принятие async
запроса не равно actual-state ack. Native Windows test и portable deadline test.
N+1: новый caller использует request/probe; native не знает emulator/tray/Shell/logger.

Class: освобождение UI bridge, а не отмена принятой backend операции (`icem-desktop/src/lib.rs`).
Inv: Drop не ждёт I/O, очередь не воспроизводится; in-flight завершается перед exit.
Проверки: stalled-port/full-reply-queue/retained-view; bounded queues 16 + 16 + 1 in-flight.
N+1: оба UI caller получают WorkerCompletion и завершают его после UI loop; пропуск caller ломает build.

Legacy emulator MainWindow/App отменяет Closing и скрывает то же окно; Quit — отдельный
engine-owned выход. Shell X завершает только Shell. Rust emulator ранее уничтожала
HWND и восстанавливала view в новом окне. E14 отменяет X/Alt+F4 через GPUI
on_window_should_close и скрывает существующее окно; Open использует тот же HWND.
Appearance и mapping 176 + 6 не меняются. Исторический PLAN_lifecycle_ABD сверён
с актуальными App/MainWindow, XAML/themes/inventory и GPUI 0.2.2 source.

GPUI App::hide на Windows 0.2.2 пуст. Синхронный ShowWindow внутри GPUI update может
вызвать nested callbacks. Используется ShowWindowAsync, как в GPUI Windows adapter.
Один pending запрос проверяется каждые 50 ms по actual visible/minimized и имеет
deadline 2 s. Show подтверждается после restore, а не по visible=true у minimized
окна. Отказ/timeout сохраняется как ошибка; автоматического повтора нет.
Focus/foreground после подтверждения — best effort, отдельный operator gate.
Прежний should_stop → tray.close → cx.quit → engine shutdown/save сохраняется.
Windows GPUI quit использует PostQuitMessage, а не оконный Close interceptor.

Native принимает &impl HasWindowHandle; raw HWND не передаётся прикладным кодом.
До каждого эффекта/probe проверяется IsWindow и текущий PID. Handle заимствуется
на вызов, не кешируется; native не уничтожает окно. Console/conhost другой процесс
этой способностью не управляется. Scoped native FFI exception и safety comments
сохраняют прежние forbid/deny/Clippy; новый unsafe не является формальным аудитом.

## Dependency review

Windows-only direct raw-window-handle =0.6.2, features=[std], уже зафиксирован через
GPUI в Cargo.lock; изменена только dependency edge native crate, версии/checksum прежние.
Лицензия MIT OR Apache-2.0 OR Zlib. Роль — borrowed typed handle на GPUI/Win32 границе,
без поиска окон по имени и без новой FFI библиотеки. Crate содержит unsafe raw-handle
compatibility impls/borrow_raw; production использует safe HasWindowHandle/as_raw.
borrow_raw применяется только в native test с owned HWND или live desktop HWND.
Новых unsafe impl Send/Sync, dereference или externally mutable Rust references нет.

## Bridge

Bridge::Drop прежде делал worker.join на UI thread. Теперь Drop ставит stop и
отсоединяет reply receiver. Worker не держит UI-ссылок; reply backpressure проверяет
stop не реже 50 ms. Непринятые команды отбрасываются. Уже принятая set/clear операция
может завершиться после закрытия окна, не повторяется и не выдаётся за отменённую.
WorkerCompletion явно принадлежит caller вне View; finish вызывается после
Application::run и дожидается in-flight перед ExitCode. Completion сам ставит stop
и будит worker, поэтому retained view/full reply queue не задерживают завершение.
Это не новый shutdown thread: остаётся один worker. UiPort по-прежнему обязан
ограничивать I/O deadline; зависший драйвер вне этого контракта не объявляется решённым.

E11 ensure-host/config, E12 file logger, SetLayer toggle, proto/web, независимый
emulator, templates/AddIn/Data.dat и MSI/force/Repair не меняются. Console Host
X → hide остаётся OPEN по ADR 0009; это другой владелец/CTRL_CLOSE semantics.
