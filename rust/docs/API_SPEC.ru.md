# Спецификация API миграции — v0.1

Статус: контракт переноса pinned baseline, не реализованный Rust server.
Источник каждого пункта — корень исходного репозитория на `96e2206562c9389d1543095232115d81eae4b692`.
Имена DLL, API routes, package/service/method, field numbers, pipe/event names и
значения байтов сохраняются. Нового C ABI для DLL не требуется.

## 1. .NET AddIn → Rust: Bus v5

| Параметр | Контракт |
|---|---|
| Producer | Burservis.iCem.DataMonitor.dll, net48, внутри iCem.exe |
| Mapping | `Global\iCemDataMonitorBus_v1` |
| Mutex | `Global\iCemDataMonitorBusMutex_v1` |
| Backing file | `C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat` |
| Доступ host | Read-only; создание/миграция producer-файла и запись принадлежат AddIn |
| Снимок | Lock native mutex ≤500 ms → copy всего файла → unlock → decode owned bytes |
| Версия | magic STNB, protocol=1, layout=5; default N=10001 |
| Геометрия | 650 ≤ N ≤65535; точный размер 64+2N bytes; default 20066 bytes |

Захват и освобождение mutex на одном OS thread; await внутри владения не допускается.
Abandoned mutex учитывается как acquired по baseline, но captured image обязательно
проверяется. Timeout возвращает last-good как stale, либо отсутствие данных, если
good snapshot ещё не было. Отсутствующий Data.dat не создаётся host. При удалении
подложки закрывается старый mapping, после нового producer-файла выполняется reopen.
AccessDenied не «лечится» изменением DACL или запуском с повышенными правами.
См. D09: некоторые текущие ошибки не выставляют stale; это отдельная characterization-задача.

| Offset bytes | Тип | Значение |
|---|---|---|
| 0..3 | 4 bytes | ASCII STNB (u32 LE 0x424e5453) |
| 4..5 | u16 LE | protocol=1 |
| 6..7 | u16 LE | layout=5 |
| 8..9 | u16 LE | register count N |
| 10..17 / 19..26 / 28..35 | i64 LE | COM/LAN/RAW DateTime.UtcNow.Ticks, 100 ns с 0001-01-01 |
| 18 / 27 / 36 | u8 | producer active: истинно только при byte==1 |
| 37..40 / 41..44 / 45..48 | u32 LE | COM/LAN/RAW write counters |
| 49..61 | reserved | retired WITS header, не использовать как новую зону |
| 62..63 | reserved | не менять |
| 64..(64+2N-1) | u16 BE[N] | тела регистров, byte image Modbus |

| Регистры | Смысл | Представление |
|---|---|---|
| 0,1,2,3 | comPortActive, caseState, caseLanState, heartbeat | Отдельные 16-bit слова |
| 4..199 | COM, 98 пар | pair 4=Unix seconds int32; остальные float32 CDAB |
| 200..399 | LAN/cRIO, 100 пар | float32 CDAB |
| 400..599 | Последняя COM CSV строка, 100 пар | Позиционные float32, не символы |
| 600..10000 (default) | ace_full reserve | Нет producer, не создавать новый writer |

CDAB: `bits = low_word | high_word << 16`, каждый word хранится BE.
Float декодируется по IEEE bits; NaN payload и −0 не нормализуются в codec.
Heartbeat wrap 32000→0 формирует AddIn, host не генерирует его.
UI liveness = producer_active && max(0,(now_ticks-last_ticks)/10000)<5000.
Это отличается от raw activeFlag для Modbus mirror. Старые layout2/3/4 мигрирует
producer; Rust-reader не переписывает DLL-owned bus в новый формат.

## 2. Modbus TCP

Listener default :502, mirror tick default 500 ms, config range 200..500 ms.
Адреса 0-based. MBAP multibyte поля BE; Transaction ID, Unit ID и protocol ID в
baseline echo. `Register file` отдельно от MMF и изначально имеет config.total;
после snapshot размер определяется N в bus header. Одновременные .NET/Rust listeners
на одном endpoint запрещены самим порядком cutover.

| FC | Операция | Условия | Ответ |
|---|---|---|---|
| 03,04 | Read holding/input | 1..125 words, start+qty≤N | byte count + u16 BE words |
| 06 | Write single | address<N, полное тело | Echo address/value |
| 16 (0x10) | Write multiple | 1..123, byteCount=2qty, start+qty≤N | Echo start/qty |
| Другой | Unsupported | — | FC|0x80, exception 01 |
| Ошибка адреса | — | Выход за текущую N | exception 02 |
| Ошибка длины/значения | — | Invalid qty/byte count | exception 03; см. D05 для исходного бага |

FC06/16 меняют **только зеркало**, никогда MMF/AddIn. Каждый tick:
1. Нет snapshot: header words 0..3 обнуляются; прочие слова удерживаются.
2. Есть snapshot: header 0..3 копируется, даже last-good/stale.
3. Stale: данные зон не перезаписываются.
4. Fresh + raw activeFlag==1: вся зона копируется без delta optimization.
5. Fresh + raw activeFlag!=1: внешние записи в зоне сохраняются.
6. Reserve не имеет mirror row: внешние записи в него сохраняются до reinitialization/shrink.

Read/write выполняются атомарно относительно операции mirror над register file.
Byte framing, fragmented TCP и malformed data имеют bounded implementation в Rust;
отклонения от небезопасных ошибок старого parser записываются в D05, не маскируются.

## 3. gRPC и gRPC-Web

Канон: `contracts/proto/telemetry.proto` — точная копия исходника.
Package `icemdatamonitor.v1`, service `TelemetryService`.
Local transport: `\\.\pipe\iCemDataMonitor.Host`, HTTP/2 gRPC.
TCP web port default 8090; gRPC-Web поддерживается на том же порту при web_on=1.
Protobuf tags, enum numbers, oneof и reserved сохраняются; v1 не заменять REST.

| RPC | Ответ / поведение |
|---|---|
| GetStatus | HostStatus: bus/addin/heartbeat/zones/layers, console и HKCU autostart truth |
| GetRegisters | Fresh-at-call bus read; UNSPECIFIED=live 0..599, COM=4..199, LAN=200..399, RAW=400..599 |
| SubscribeMetrics | Первый полный sample, затем deltas; interval_ms≥200 после clamp; disconnected cancellation |
| SetLayer | В фактическом v1 **toggle** Modbus/Web/Sniffer/OPC; desired_state игнорируется (D04) |
| SubscribeBootStages | Replay с начала процесса, затем live stages; UI не парсит stdout |
| SetConsoleVisible | Idempotent requested state, ответ actual HostStatus |
| SetAutostart | HKCU Run iCemDataMonitorHost; ответ actual HostStatus |
| NavigateUi | WEB=1/STATUS=2; signal живому shell либо pending для следующего show |
| GetUiPrefs | Чтение station ui-theme: light=true, иначе false |
| SetUiTheme | Записать light/dark → poke shell event → вернуть заново прочитанную правду |

GetRegisters: header 0..3 как int32 из u16, pair4 как signed Unix int32,
остальные пары float32; один элемент на base address пары. Не весь N-element словарь.
Unknown zone/layer → INVALID_ARGUMENT. Bus недоступна → UNAVAILABLE.
Metrics при невозможности чтения завершается UNAVAILABLE, не молчаливым EOS.
Web/OPC toggles persist-only; Modbus/sniffer реально start/stop. `setAsDefault`
управляет записью конфигурации, UI обязан показывать реальные probe states.
В API нет ShutdownHost/ControlEngine, снятые поля не возвращаются.
Завершение host инициируется Windows events/tray через общий lifecycle.
Baseline anonymous closed-contour API сохраняется с default loopback binding;
новая auth-схема не вносится незаметно в совместимый API.

## 4. HTTP/REST и SPA

Машинная спецификация: `contracts/http/openapi.json`. Ровно четыре активных GET:

| Route | 200 | Ошибки/условия |
|---|---|---|
| /api/status | busOpen, addInAlive, heartbeat, comPort, lanStatus, zones, updatedAtUtc | lanStatus = reg2, не reg8 |
| /api/status/summary | busOpen, addInAlive, heartbeat, zones, layers, snifferStats nullable, updatedAtUtc | modbus/sniffer отражают actual listeners |
| /api/regs | system.scalars, zones.com/lan/raw, liveness, updatedAtUtc nullable, stale | 503 {error:"bus not available"} |
| /api/regs/history?addr=4&addr=200 | object addr-string→[{ts,v}], oldest→newest | 400 missing/odd/out-of-range; 503 history disabled |

History addr even 4..598; повторяемые query параметры, повтор одного addr создаёт
один JSON key. Каждый addr — 600 samples при 1 Hz; неизвестная история — пустой массив.
Отсутствующая history при web_on=0 проверяется **раньше** валидации addr.
`/api/regs` pair4: строка UTC `yyyy-MM-ddTHH:mm:ssZ`, kind=unix; другие pairs:
value=float, kind=float. Timestamp history, в отличие от gRPC, хранится как f32 seconds
и имеет исходную потерю точности; это нельзя исправлять незаметно.
Поля nullable и различия ISO-фоматов сохраняются. Non-finite float serialization
подлежит golden characterization (D08); не придумывать JSON NaN как новое API.

web_on=0 выключает SPA/history, **не** HTTP ops и local gRPC.
Config/web toggle применяется после restart. SPA файлы `web/dashboard` и маршрутизация
сохраняются; API ошибки не должны превращаться в HTML index.html.
HTTP functions из dormant inventory не публикуются автоматически.

## 5. Host CLI

Production filename сохраняется `iCemDataMonitor.Host.exe`; M0 использует другое имя.
CLI dispatch происходит до singleton/tray/listeners. stdout=одна строка чистого JSON,
diagnostics→stderr; `--start` означает launch host, ожидание READY≤30 s, затем RPC.
CLI сам не открывает MMF.

| Команда | stdout | Exit |
|---|---|---|
| snapshot | u16[600], только live words | 0 |
| get main / lan / raw | floats[98 / 100 / 100]; pair4 main reinterpreted bits | 0 |
| header | {comPortActive:0/1,caseState,caseLanState,heartbeat} | 0 |
| stop | {action:"stop",result:"signalled" или "notRunning"} | 0, idempotent |
| restart | {action:"restart",result:"restarted" или "spawned"} | 0; dead host→start |
| RPC answered but failed | stderr | 1 |
| Usage / host absent / READY timeout | stderr | 2 |

Обычный host argv: `--port N`, `--ini PATH`, `--autostart`, `--help/-h`.
Port override=0 разрешён только тестовым запуском; неизвестные flags fail-fast.
Не заменять irregular CLI timestamp representation семантически «улучшенным» числом.

## 6. Emulator API и wire

Приложение `iCemEmulator.exe` независимо от host. Local pipe
`\\.\pipe\iCemEmule.Control`: UTF-8 text/JSON, одна строка→одна JSON строка.
Request JSON поля `cmd`, `channel`, `name`, `value`, `deviceType`, `sv`.
Имена с пробелами группируются двойными кавычками в text grammar; decimal invariant.
Prefix `com.`/`lan.` в name задаёт channel. SV допускает decimal/0x, хранится uint32.

| Команды | Назначение |
|---|---|
| status / watch | Snapshot / ACK и 1 Hz status stream до disconnect |
| start com|lan / stop com|lan | Независимые каналы потока |
| set name value / get name / clear name | Ручные native overrides; LAN override до unit conversion |
| profiles / profile name / vars [channel] | Templates и текущие значения |
| ports / adapters / setport name / setadapter name | Реальные I/O endpoints |
| setequipment uint32 / setfirmware 0|1 | ACE equipment и firmware SV |
| setemulate com|lan 0|1 | Генерация/удержание значений отдельно от включения потока |
| setpreset 0..3 / setunit group [unit] | Presets / group overrides |
| help / quit | Command catalog / graceful exit с ACK до закрытия pipe |

Success envelope: `{"ok":true,"cmd":"...","data":{...}}`.
Failure: `{"ok":false,"cmd":"...","error":"..."}`.
Полный operation-specific data shape зафиксировать из IEmuControl/EmuEngineHost
golden traces M1; контракт command names и parser сохранён в baseline, не заменять
непроверенными invented DTO. Status включает `proto: 1` из CommandProcessor,
uptimeSec/pid/version/selfTest/com/lan/units/overrides из EmuEngineHost.
Malformed command не останавливает emitters; watch владеет соединением до disconnect.

CLI: `--cli <command>` обращается к живому экземпляру (0 success / 1 error / 2 usage).
`--selftest`, `--landiag <adapter>`, `--help` сохранены; без args/unknown args — UI.
COM: FLECS 32 fields / 9600 baud / 1 Hz, точный line format из ComSerialEmitter.
LAN: raw Ethernet IEEE802.3 ACE, не UDP; LLC 03 03 08/09, 152-byte frames,
SV composition/firmware и pacing (номинально 5.25 ms, исходный код округляет sleep до 5 ms) проверяются по исходному PROTOCOL.md
и AceFrameBuilder/AceLanEmitter. Таблицы equipment/units не переносить в core.

## 7. Windows IPC, tray и настройки

| Объект | Имя | Owner |
|---|---|---|
| Host singleton | Global\iCemDataMonitorHost_v1 | Host |
| Shell singleton | Global\iCemDataMonitorTray_v1 | Shell |
| Emulator singleton | Global\iCemEmulator_v1 | Emulator |
| Shell show | Local\iCemDataMonitorTray_v1_ShowWindow | Shell |
| Shell shutdown | Local\iCemDataMonitorTray_v1_Shutdown | Shell |
| Host restart | Local\iCemDataMonitorTray_v1_Restart | Host |
| Host shutdown | Local\iCemDataMonitorTray_v1_HostShutdown | Host |
| Theme changed | Local\iCemDataMonitorTray_v1_UiTheme | Shell |

Host-owned restart/shutdown — AutoReset, reset при boot, клиент только signals.
Не брать чужой singleton mutex для проверки живости: owner roles сохраняются.
Tray команды — HostCommand в icem-core; shell/CLI/tray вызывают одну политику.
Restart освобождает порты/mutex и стартует successor с прежними relevant args;
исходный graceful drain budget 1 s, successor mutex grace 15 s, CLI ready wait 30 s.

config.ini читается раз при старте, defaults из IniSchema, unknown/duplicate/missing
required keys — явная ошибка; retired keys принимаются, игнорируются, не записываются.
Миграция station.ini копированием без удаления source и без overwrite config.ini;
неудачная копия сохраняет active legacy path и диагностируется. Toggling persistence
пишет именно active path. Emulator XML принадлежит emulator, streaming defaults OFF.
Визуальный набор и lifecycle matrix перечислены в PARITY_MATRIX.ru.md.

## 8. Внутренний Rust API

Реальные стартовые контракты: `icem-bus::Snapshot::decode(&[u8])`, `Snapshot::words`,
`Snapshot::pair_bits/float_pair/unix_seconds`, `icem-core::SnapshotSource::snapshot`,
`SnapshotRead {snapshot,freshness,origin}`, `SnapshotSource::last_error`, `HostCommands::execute(HostCommand)`.
Windows MMF reader и Modbus TCP реализованы в development-инкременте.
`origin` различает Captured/Retained, не меняя legacy Stale semantics D09.
HTTP/gRPC и полная Windows-приёмка остаются следующими этапами. Отдельные application services эмулятора не входят
в HostCommands; общий runtime не должен знать управление cement DFB.
