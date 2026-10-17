# ADR 0006 — Host boot replay и навигация Shell

Class: стадии запуска Host, `icem-web::boot`, wiring в `apps/host::main`, представление
в `icem-desktop::Model/monitor` и gRPC adapter shell. Telemetry/emulator вне класса.
Inv: ordinal стабилен, максимум 64 стадии, complete неизвестной стадии — ошибка,
снимок владеет данными; replay snapshot и live subscription захватываются одним lock.
N+1: следующая стадия добавляет begin/complete в месте фактической операции; API не меняется.

Host создаёт recorder до INI/options и записывает реальные ini/composition/layers/web/ipc/ready.
READY публикуется после bind и создания lifecycle, не по elapsed time. Незавершённая стадия
остаётся unchecked. Native GPUI startup panel читает original SubscribeBootStages; это не
утверждение о визуальной эквивалентности отдельного legacy BootSplashWindow.
Обновления begin/complete не coalesce. D27: bounded broadcast/live queues по 128 updates;
lag возвращает RESOURCE_EXHAUSTED с требованием reconnect/replay, а не теряет прогресс молча.
Подписки используют существующий общий stream limit 64. Новых dependencies нет.

Class: Host-owned pending navigation (`icem-web::navigation`) и existing Shell events;
эмулятор и layer lifecycle вне класса. Inv: last-write-wins, query read-and-clear без show,
failed/disappeared show сохраняет pending, unknown enum отвергается RPC.
N+1: новый раздел добавляется в canonical enum и GPUI apply; storage/signalling общий.

Windows adapter использует original SHELL_SHOW. Non-query сначала сохраняет pending,
проверяет наличие Shell-owned show event и сигналит его; query только проверяет наличие.
Native event_exists открывает существующий event с SYNCHRONIZE и сразу закрывает handle;
не вызывает wait/reset/set и не создаёт event. Чужой singleton mutex не захватывается.

Это явное отличие внутреннего probe от legacy MutexProbe.IsTaken: ui_alive опирается
на доступность show event, который живёт вместе с singleton Shell. В переходных окнах
startup/shutdown и при другом token/ACL полная эквивалентность не доказана. Failed show
возвращает ui_alive=false и сохраняет pending, диагностика пишет причину. Схема RPC,
имена IPC и query-arm не изменены. Cross-user/elevation acceptance остаётся открытой.
Новая read-only операция расширяет native boundary ADR 0001; RAII/open helper общий
с signal_existing_event. Тест доказывает, что probe не потребляет auto-reset signal.

Shell потребляет pending при первом подключении и при show event; переключает нативные
Состояние/Панель. Статус связи не подменяется результатом навигации. UI/bootstrap
запросы выполняются через bounded Bridge; main thread не читает RPC.

D24 refinement: LanEmitter::is_sending удерживает LAN model/elapsed при занятом worker,
как original _lanSending. COM/status продолжаются; regression проверяет обе стороны.
Tab/Shift+Tab явно вызывает GPUI focus_next/focus_prev; root имеет постоянный focus handle.

MSVC failure на 6cbbd74: windows_pipe rebind встретил AccessDenied, пока stopped client
runtime мог удерживать transport task/handle. Тест теперь явно закрывает обе стороны до
exclusive rebind, сохраняет все RPC/exclusive assertions и повторяет цикл 4 раза.
ACL не менялись, повторы bind/sleep вместо устранения lifetime не добавлялись.
Новый exact-head Windows CI должен подтвердить это объяснение; Linux его не доказывает.
