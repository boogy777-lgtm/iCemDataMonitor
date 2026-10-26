# ADR 0004 — GPUI, GPUI Tray и локальный Host gRPC

Исторический E5: WRY/WebView2 отменены ADR 0005 (E6); native desktop больше не требует browser runtime.

Class: исходные COM/LAN/Units controls → `icem-desktop::{windows,input,theme}`;
Host status/dashboard → `monitor`; domain engine остаётся за `UiPort`.
Inv: bounded command/reply queues, один worker, owned JSON, join при drop,
ответ ошибки виден без оптимистического переключения; model regression tests.
N+1: следующий control использует те же renderer/input/bridge; inventory требует
явной строки mapping, а не изменения core/runtime. Profile передаёт name+deviceType;
variable list/combo selection следует имени+SV; drafts COM/LAN независимы.

Class: оба системных tray → `icem-desktop::tray` на **gpui-tray 0.1.4** (указание пользователя).
Inv: обычные GPUI MenuItem/actions; tray objects живут на foreground executor,
`Tray::close` до остановки сервисов, cross-thread передаются только HostCommand.
N+1: следующий пункт меню добавляет action/menu data; собственных Win32 tray/menu callbacks нет.
`icem-windows-native/src/tray.rs` удалён. Handwritten native exception не расширяется tray-кодом.
Host использует один windowless GPUI application на выделенном foreground thread;
MMF/mutex/service loop сохраняют owning thread. Эмулятор использует уже существующий GPUI App.
Console truth проверяется каждые 50 ms; `refresh_menu` вызывается при изменении
probe, а библиотека обновляет меню после actions. Idle tick не пересоздаёт меню.
Это не утверждение о синхронном probe прямо внутри native popup-open callback.

Зависимости: ранее locked GPUI 0.2.2, WRY 0.57.0, unicode-segmentation 1.12.0.
GPUI Tray: Apache-2.0, crates.io **=0.1.4**, default-features=false, только feature `gpui`.
Используется та же crates.io GPUI 0.2.2, без Git fork/patch и без `menu-state`, который
требует другого upstream API. Native backend/unsafe принадлежат dependency; handwritten
application crates сохраняют forbid unsafe. Review исходников tray/icon/menu/backend:
immutable snapshots, creator-thread checks, deterministic close, Explorer TaskbarCreated.
Это целевой обзор границы, не полный аудит dependencies или UI acceptance.
`image` =0.25.10 (уже в locked GPUI graph), default-features=false, feature `ico`:
декодирует исходные ICO в RGBA для `gpui_tray::Icon`; новые изображения не создаются.
Input основан на Apache-2.0 example GPUI с сохранённой атрибуцией.
WRY использует WebView2; наличие/runtime install входит в MSI gate.

Class: локальные RPC-клиенты Host, не JSON-line control эмулятора.
Inv: исходный proto, HTTP/2, exact default pipe, first-instance reservation,
remote-client rejection, max 32 pipe instances, finite RPC deadlines, no mutation retries.
N+1: следующий клиент переиспользует generated client; named-pipe tests проверяют
roundtrip, exclusive bind, stop/rebind и Modbus toggle через реальный процесс Host.
`SetLayer` v1 игнорирует desired_state: два одинаковых запроса переключают дважды.
`tonic/channel` включён для generated client, hyper-util/tokio для safe pipe adapter.
Добавлен hyper-timeout 0.5.2 (MIT): deadline adapter Hyper, без дополнительных features.
Cargo.lock фиксирует closure; proc-macro-error2 future-incompat warning не подавлен.

Class: HKCU Run entry + StartupApproved в `icem-windows-native::autostart`;
другие registry settings вне класса. Inv: exact `"<exe>" --autostart` truth probe,
read-only GetStatus, clear approved flag при enable, idempotent disable; throwaway-HKCU tests.
N+1: paths/name/exe — data seam как в original AutostartManager, без domain knowledge в core.
Отдельное расширение FFI ADR 0001: registry handles RAII, bounded UTF-16 copies.

Окно эмулятора закрывается без остановки engine; tray Open возвращает ту же model.
Exit отправляет quit через control seam. Host close/restart снимает tray, сигналит
shell и останавливает servers; эмулятор не вызывается. Dev restart повторяет argv
после выхода из run и освобождения mutex. M3/M6 остаются открыты до полной приёмки:
control states/keyboard/DPI, boot replay, Host CLI/ensure, sniffer, Web/OPC toggles,
console close guard, ACL, аппаратный timing и MSI. См. DESKTOP_STATUS.ru.md.
