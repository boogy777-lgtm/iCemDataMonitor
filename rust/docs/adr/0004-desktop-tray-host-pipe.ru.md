# ADR 0004 — GPUI, tray и локальный Host gRPC

Class: исходные COM/LAN/Units controls → `icem-desktop::{windows,input,theme}`;
Host status/dashboard → `monitor`; domain engine остаётся за `UiPort`.
Inv: bounded command/reply queues, один worker, owned JSON, join при drop,
ответ ошибки виден без оптимистического переключения; model regression tests.
N+1: следующий control использует те же renderer/input/bridge; inventory требует
явной строки mapping, а не изменения core/runtime.

Class: owned native tray и lifecycle events в `icem-windows-native`;
MMF/COM/Npcap data operations вне этого класса.
Inv: HWND/HICON/HMENU остаются на owning thread; NIM_DELETE до DestroyWindow/DestroyIcon;
menu IDs уникальны/ограничены; clients только OpenEvent/SetEvent; events auto-reset.
N+1: host/emulator используют общий tray с menu data, независимые commands/lifetime.
Это отдельное расширение разрешённой FFI-границы ADR 0001. Workspace forbid не ослаблен.
Рассмотрены прямые Win32 APIs из уже locked windows-sys; новый UI/tray framework не добавлен.

Class: локальные RPC-клиенты Host, не JSON-line control эмулятора.
Inv: исходный proto, HTTP/2, exact default pipe, first-instance reservation,
remote-client rejection, max 32 pipe instances, finite RPC deadlines, no mutation retries.
N+1: следующий клиент переиспользует generated client; named-pipe tests проверяют
roundtrip, exclusive bind, stop/rebind и Modbus toggle через реальный процесс Host.
`SetLayer` v1 игнорирует desired_state: два одинаковых запроса переключают дважды.

Зависимости: используются ранее locked GPUI 0.2.2, WRY 0.57.0, unicode-segmentation
1.12.0. Input основан на Apache-2.0 example GPUI с сохранённой атрибуцией.
WRY использует установленный WebView2; наличие/runtime install ещё входит в MSI gate.
`tonic/channel` включён для generated client, hyper-util/tokio для safe pipe adapter.
Добавлен hyper-timeout 0.5.2 (MIT): deadline adapter Hyper, без дополнительных features;
Cargo.lock фиксирует closure. Handwritten unsafe там отсутствует; это не аудит всех
transitive dependencies. `proc-macro-error2` future-incompat warning не подавлен.

Окно эмулятора закрывается без остановки engine: GPUI entity/bridge сохраняются до
завершения application; tray Open создаёт окно с той же model. Exit отправляет quit
через тот же control seam. GPUI работает на main thread, ticks — отдельный worker.
Host tray независим, console caption вычисляется непосредственно перед показом меню.
Host close/restart сначала снимает tray, затем сигналит shell и останавливает servers;
эмулятор не вызывается. Dev restart повторяет argv после выхода из run и освобождения mutex.

Это реализация development increment, не proof visual/lifecycle parity. M3/M6 не PASS.
Открыты: исходные control template states/keyboard/DPI, boot splash/replay, Host CLI/
ensure/reconnect, sniffer, autostart, persist-only Web/OPC toggles, console close guard,
многопользовательские ACL, аппаратный timing и MSI. Tray modal menu и Explorer/UAC
проверяются на целевой Windows. Source XAML/assets/proto не изменяются.
