# Windows UI / аппаратная приёмка E5

Статус: NOT RUN. E6 удаляет WebView2 из Rust desktop; браузерная SPA остаётся отдельной. Нужны целевая Windows 10/11, Npcap, COM loopback/устройство,
исходное .NET-приложение и AddIn. Автоматический CI не заменяет эти измерения.
Запуски Rust и legacy на одинаковых singleton/ports выполняются последовательно.
Для каждого результата сохранять commit, EXE SHA256, ОС, DPI, token/UAC, driver versions,
фактические endpoint/config paths, UTC interval и ссылку на evidence. PASS только с evidence.

| ID | Действие | Критерий / evidence |
|---|---|---|
| UI01 | Legacy/Rust: COM/LAN/Units, light/dark, 100/150/200% DPI | Парные PNG одинакового viewport; mapping всех controls, focus/hover/pressed/disabled/selected |
| UI02 | Выбрать/изменить/сбросить COM и LAN signal; сменить profile/firmware во время selection | Отсутствующий signal сбрасывает selection; запись не меняет соседний signal; refusal рядом с tabs |
| UI03 | Unicode/IME, paste, select/cut/copy, keyboard-only navigation и длинные adapter names | Нет обрезки controls; inputs и scroll работают; screenshot + operator trace |
| TR01 | Закрыть X эмулятора, проверить CLI status, tray Open, затем Exit | PID/pipe/engine живы после X; тот же state после Open; после Exit нет icon/port/pipe |
| TR02 | Host close/restart при открытом/закрытом shell и живом emulator | Host icon снят до drain; shell закрыт; emulator PID/stream сохраняются; successor единственный |
| TR03 | Перезапустить Explorer, открыть/закрыть меню; stop при открытом popup | Icons возвращаются; captions соответствуют console truth; нет зависания/ghost icon |
| IPC01 | Обычный/elevated token, второй пользователь/session, чужой singleton/pipe | Доступ соответствует утверждённой ACL; нет второго owner; отказ явно диагностирован |
| COM01 | 9600/8N1 loopback, start/stop/freeze/manual/units, disconnect/reconnect | Побайтовый FLECS+CRLF; successful-send counters; capture timestamp/delta vs baseline |
| LAN01 | Выбранный adapter/Npcap: equipment/firmware/preset/overrides | PCAP: 152-byte ACE, SV/order/endian/MAC совпадают; отсутствующий Npcap не даёт ложный streaming |
| TIME01 | 1 Hz COM и LAN superframe при одновременных status/vars/set/stop | Измерить p50/p95/max latency, burst spacing/jitter; D24 worker software tests не заменяют измерения задержек |
| BUS01 | Реальный неизменённый AddIn → Data.dat/MMF → Host → gRPC/Modbus/dashboard | Сравнить snapshot под mutex и wire; deletion/recreate/timeout/abandoned/AccessDenied traces |
| HOST01 | gRPC toggle/console/theme, Host restart, native panel reconnect | Desired_state legacy toggle, port truth, INI persistence, theme station-wide; unsupported RPC не PASS |

Не заполненные строки = NOT RUN; при несовпадении фиксировать FAIL и ссылку на дефект.
M3/M6/M8 считаются открытыми до устранения pending controls и Host integrations.

E10 / HOST02 — NOT RUN: реальный Npcap/NIC, auto/manual adapter, empty/busy wire,
снятие адаптера, AccessDenied, pause/resume, stop/restart. Сравнить actual RUNNING,
availability, summary counters и PCAP; проверить отсутствие записей MMF/Data.dat.
Измерить native enumerate/open/close latency и Host command latency под load.
Software ABI shims/nonblocking poll не доказывают deadline внутри неисправного драйвера.
