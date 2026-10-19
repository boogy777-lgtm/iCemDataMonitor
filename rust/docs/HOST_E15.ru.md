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
Контролируемый Win32 ERROR_CLASS_ALREADY_EXISTS проверяет startup refusal,
освобождение owner/отсутствие HWND и recovery после снятия собственной test registration.
Host process test: headless start → RPC Show → X ×3 → Host/Modbus/pipe живы →
новый client/reconnect без spawn → actual state → restore → Ctrl+C → exit 0 и
освобождённые pipe/socket. Дополнительно: manual CREATE_NEW_CONSOLE, explicit
restart с выходом прежнего процесса, headless --autostart с настоящим tray, Quit
при скрытом и видимом окне. Message-level input не доказывает физическую клавиатуру.
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

## Следующие работы: код и отдельные gates

Этот список сверён с действующим wiring, а не с ранними TODO E1–E8.

| Порядок | Фактическое состояние | Следующий ограниченный результат |
|---|---|---|
| 1 | `icem-platform-windows/src/sniffer.rs::observe` считает кадры/MAC; полный Host signal/equipment/SV decode отсутствует. Legacy `AceSnifferService` использует LantableRegistry, per-equipment trackers, latest values и AceFrameDecoded | Перенести Host read-only decode/catalog/projections, не добавлять bus writer или dormant routes; codec/emulator providers уже существуют |
| 2: решение scope | `StartCaptureAsync/StopCaptureAsync` вызываются из `Host.Core/Endpoints/SnifferEndpoints.cs` и tests. `HostAppFactory` подключает только `MapFrozenOpsSurface`, который эти file endpoints не вызывает. StopCapture в Dispose — cleanup, не startup capture | Capture-to-file отсутствует в Rust, но действующий production wiring записи не найден. Не активировать REST/download/file lifecycle без отдельного решения |
| 3 | Legacy `ControlClient.RoundTrip` после watch ACK читает строки до EOF. Rust `apps/emulator/src/engine.rs` вызывает one-roundtrip `emulator_control::request`; pipe server watch loop уже реализован | Отдельный CLI streaming consumer с bounded framing/cancellation и корректными exit/stdout, без второго engine |
| 4 | Legacy `iCemEmulator/Program.cs` до UI/singleton dispatch вызывает `FrameRoundTrip.RunAll` для --selftest и `LanLoopbackDiag.Run` для --landiag. Rust app modes отсутствуют; часть wire проверок уже существует как tests | Перенести app selftest/диагностику с реальными error/exit semantics. LanDiag активно передаёт кадры: отдельная явная команда, не автоматическая проверка на произвольной NIC |
| 5: compatibility decision | D22: malformed JSON, UTF-16/32 и Unicode OrdinalIgnoreCase явно ограничены | Characterization и решение по каждому расхождению перед изменением loader; parity пока не заявлена |
| Сквозная приёмка | GPUI/tray/controls, E14 retained HWND и in-flight completion уже реализованы; operator state/visual matrix ещё не принята | Windows X/Open ×3 с unsaved input/scroll/selection, Quit visible/hidden, themes/DPI/focus/keyboard и ошибки устройств |

Важное уточнение пункта selftest: legacy `EmuEngineHost.GetStatus` тоже возвращает
`selfTest = null` (`iCemEmule.Core/Core/EmuEngineHost.cs`). Само null не доказывает
пробел parity; отсутствуют именно app entry modes и runtime diagnostics. Не менять
status schema/null на выдуманный результат теста.

OPC DA остаётся external no-op. Произвольный динамический DfbNetwork API и dormant
COM tee/com0com не становятся требованиями из наличия старого класса. Полные DFB,
LAN providers, emitters, engine/control pipe, E11 config/ensure, E12 logs, E13 lifecycle
и E14 emulator/bridge повторно не переносятся. Hardware/SDK gates отделены от
отсутствующего программного кода.

Проверены последние 100 из 170 Actions runs: rust-addin-build в этой странице нет;
найденные успешные rust-windows-release — pull_request fixture contracts, не SDK
релиз. Inventory runner не доступен через имеющийся endpoint; отсутствие runner
не утверждается. До подтверждённого SDK первый real run остаётся NOT_RUN.

## Найденный блокирующий дефект E12

На первом head `8094df73f35800c9210aa040896baa4a3d0c1fcd` native Windows
проверки прошли. [Run 39189512950](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/39189512950)
затем воспроизвёл base failure: первая запись emulator logger вызвала panic
`a Display implementation returned an error unexpectedly` и exit 101 до READY.
`icem-logging` использовал неподдерживаемый Chrono `%.7f` (parser поддерживает
фиксированные 3/6/9). Теперь общая функция явно формирует UTC seconds + nanos/100
с семью цифрами, сохраняя прежние 100 ns/UTC/CRLF. Тест проверяет 0/99/100 ns,
123456789 ns, верхнюю границу секунды и переход следующей секунды. Поиск остальных
Rust format strings обнаружил только поддерживаемый %.3f в web projection.
Это устранение failure общего logger, а не расширение emulator scope.

## Дополнение владельца: Legacy CI и merge

2026-10-19 владелец явно разрешил исправить Legacy CI и продолжать merge/миграцию.
[Исправление наблюдения retry snapshots](LEGACY_CI_E15.ru.md) меняет только
Legacy test helper и его точный ledger hash. Production COM tee не активирован.
Source-fix commit: `d29180be0b55c200ad4902394f725e41aa242ce1`. Merge PR #11 допускается после новых проверок.
