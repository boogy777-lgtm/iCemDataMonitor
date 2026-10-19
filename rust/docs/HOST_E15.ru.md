# E15 — Host console X → Hide

База `fcdb65b13d7aa4edd1cc901ac1d7d4b8edcb9823`, tree
`65c1e25e7e9651da8e8912ee78907bd054004ecf`. Открытых PR при старте нет.
Ветка `rust/host-console-close`. Новый PR не сливается без команды владельца.

После merge #10 Legacy CI 39187045060 — success. Rust 39187045090 — cancelled;
внутри ранний emulator `engine_pipe` FAIL: exit 101 до READY. Это не успешная
MSVC-проверка. Child stderr теперь сохраняется в logs для установления причины.
Legacy CI не изменяется. [ADR 0011](adr/0011-host-diagnostic-window.ru.md)
обосновывает ownership, input path, ресурсы, FFI/dependency review и альтернативы.

Host создаёт своё окно диагностики и отсоединяется от прежней console session.
X/Alt+F4 скрывает тот же HWND; сервисы/pipe продолжают работать. Startup, tray и
SetConsoleVisible используют прежнего Controller. Show также восстанавливает
minimized window. GetStatus/tray читают actual visibility. Потеря окна не порождает
автоматический spawn/recreate. CLI не создаёт owner, stdout остаётся JSON.
Ctrl+C в собственном окне запрашивает прежний graceful stop. После detach Ctrl+C
в родительском терминале больше не сигнализирует Host; AttachConsole/console events
не являются API Host. Explicit stop/restart сохраняются.

Native child tests используют настоящий Windows HWND/message pump: X/Show/Hide ×3,
тот же HWND, minimize/restore, Ctrl+C input message ×2, owner collision, transcript
overflow, потеря window loop и повторный owner после cleanup, Quit hidden/visible.
Host process test: headless start → RPC Show → X ×3 → Host/Modbus/pipe живы →
новый client/reconnect без spawn → actual state → restore → Ctrl+C → exit 0 и
освобождённые pipe/socket. Message-level input не доказывает физическую клавиатуру.
Windows CI добавляет ранний native test; полный Scaffold/guards не ослаблены.

E14 код не меняется. Его native/bridge regressions остаются в полном suite. Operator
X/Open emulator ×3 с несохранённым вводом/scroll/selection, Quit visible/hidden,
physical input, tray focus/DPI/session/UAC требуют отдельной интерактивной приёмки.
Mapping 176 legacy + 6 logging controls сохранён; новое Host window + readonly
transcript явно учтено отдельно. Completeness не означает visual parity.

Результаты exact head и ссылки CI будут зафиксированы в PR после выполнения.
SDK runner/artifact не подтверждён; реальный SDK release не запускался. Нельзя
подменять его fixture MSI. Реальные iCem/AddIn, COM/Npcap, reboot/rollback/offline,
другие sessions и payload measurements остаются отдельными gates.
