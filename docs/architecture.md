# Архитектура

Компонентная карта из `iCemDataMonitor.sln` (все проекты — в корне репозитория).

## Компоненты

| Компонент | Роль |
|---|---|
| `iCemDataMonitor.Host` (→ `iCemDataMonitor.Host.exe`, net10.0-windows) | Headless host: BackgroundService-процесс пользователя, трей, консоль; **CLI-режим слит в exe**: `snapshot|get|header [--start]` (и `stop|restart`) — stdout = чистый JSON, RPC по named pipe, шину CLI не читает. Разрешение соседних exe (HostProcessLauncher), одноэкземплярность, `--autostart` (HKCU Run), `--port`, `--ini` |
| `iCemDataMonitor.Host.Core` | Композиция host'а (HostComposition): регистрация слоёв — modbus publisher, web (Kestrel + SPA + gRPC-Web + кольцевая история 600 с @ 1 Гц), sniffer; LayerRuntimeController — runtime start/stop слоёв (чекбоксы shell); StationHealthState, пути логов (StationLogPaths) |
| `iCemDataMonitor.Core` | Закон конфига: IniConfig (поиск/миграция `station.ini`, строгий парс, создание с дефолтами), LayerFlags |
| `iCemDataMonitor.Sdk` | Контракты шины: IniSchema/IniOptions (дефолты + валидация), BusLayout (compile-time геометрия зон), диагностика |
| `iCemDataMonitor.Data` | Шина данных: чтение/запись зон MMF, BusRingService (кольцевой сэмплер истории), `data-bus.log`, чистка логов |
| `iCemDataMonitor.Transport` | Транспорт: ModbusTcpPublisher (FC03/FC04, тик `modbus_tick_ms`, полная перезапись); AceSnifferService (SharpPcap: ACE-кадры прямо из провода; LanBusSink в v5 deferral-gated — шину не пишет) |
| `iCemDataMonitor.Contracts` | gRPC-контракты (HostStatus, Registers, LayerControl, MetricsSample, ...), имена каналов (named pipe host'а) |
| `iCemDataMonitor.HostClient` | Клиент host'а: gRPC over named pipe, FSM реконнекта, ensure-host (используется shell'ом и CLI-режимом) |
| `iCemDataMonitor.Plugins.Icem` | Проекция данных шины в представление iCem (реестры переменных, шаблоны) (уточнить) |
| `iCemDataMonitor.Shell.WinUI` (→ `iCemDataMonitor.WinUI.exe`, WinUI 3, unpackaged) | Оболочка — **клиент** host'а (без собственного бэкенда): статус-область = живость host'а; вкладка «Дашборд» = WebView2 поверх веб-SPA host'а; сам запускает host'а, никаких диалогов «Запустить хост?» |
| `iCemDataMonitor.Emulator.WinUI` | UI-библиотека эмулятора (EmulatorPage и др.), используется приложением `iCemEmulator` (уточнить детали embed) |
| `iCemEmulator` (корень репо, → `iCemEmulator.exe`, WinUI 3) | Независимое приложение эмулятора станции: читает `emule_config.xml` при старте, все тумблеры эмуляции по умолчанию OFF; host его не порождает и не надзирает |
| `iCemEmule.Core` | Движок эмуляции: FLECS-эмиттер COM (32 поля, 1 Гц, через com0com → COM1) и ACE LAN-эмиттер (raw 802.3, кадры 152 байта, суперфрейм 6 SV, burst-pacing 5,25 мс); реестры переменных (см. `templates\`, PROTOCOL.md) |
| `Burservis.iCem.DataMonitor` (net48 AddIn, dll в iCem.exe) | «DUMB TRANSLATOR byte-exact» (контракт v5.1): транслирует внутренние данные iCem в регистры шины 1:1, пишет MMF `Global\iCemDataMonitorBus_v1` с файловой подложкой `Data.dat`; единственный писатель шины |
| Тестовые проекты (`*.Tests` в sln + `iCemEmule.Tests`, `Burservis.iCem.DataMonitor.Tests`) | Юнит/контрактные тесты: pin-тесты дефолтов конфига, инварианты шины, CLI-контракт, логика трея |

## Поток данных

```
iCemEmulator --COM (com0com, FLECS CSV 32 поля, 1 Гц)--> COM1 -> iCem.exe (FLECS parser)
iCemEmulator --LAN (ACE raw 802.3, 1 Гц) --------------> iCem.exe
iCemEmulator --LAN (ACE) ------------------------------> AceSnifferService (сниффер host'а)
iCem.exe -> AddIn (in-proc) --регистры--> шина MMF + Data.dat
шина -> BusReader -> ModbusTcpPublisher (:502, FC03/FC04) -> SCADA-мастер
шина -> Kestrel (:8090: SPA + gRPC-Web + /api/regs/history) -> браузер
шина -> WinUI shell (gRPC по named pipe) / CLI (snapshot|get|header)
```

## Шина: MMF + Data.dat

- Имя MMF: `Global\iCemDataMonitorBus_v1` (`mmf_name`); файловая подложка: `C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat` (`mmf_path`) — AddIn открывает MMF через `CreateFromFile` на `Data.dat`, поэтому данные переживают рестарты.
- Размер: `total` = 10001 регистров (runtime-данные ∈ [650..65535]); значения float32 CDAB.
- Зоны (контракт v5.1): `0..3` — состояние (в т.ч. comPortActive пишет сам AddIn); `com_stream 4..199` (4..5 — int32 unix-время); `lan_stream_crio 200..399` (73 lan-алиаса); `raw_com_line 400..599`; `ace_full 600..10000` — резерв, отложен (никто не пишет, sink сниффера deferral-gated).
- Геометрия зон — compile-time (`BusLayout`); retired-ключи зон в ini принимаются и игнорируются (см. [config.md](config.md)).

## Веб-дашборд и порты

- Kestrel слушает `:web_port` (8090) **всегда**; `web_on` управляет только SPA-поверхностью: при `web_on=0` ops-поверхность `/api/status*`, `/api/regs` продолжает работать.
- SPA-дашборд: index.html + gRPC-Web + REST `/api/regs/history` + кольцевой сэмплер 600 с @ 1 Гц.
- `web_remote_on=0` — привязка только loopback; `1` — ListenAnyIp (закрытый контур, ни одного IP-guard'а в коде).
- Modbus TCP: `:modbus_port` (502), FC03/FC04, зеркало шины обновляется каждые `modbus_tick_ms` (полная перезапись, Δ-детекция запрещена).
- Named pipe (Contracts.HostChannelNames) — gRPC-канал host'а для shell'а и CLI-режима.

## Жизненный цикл и надзор

- Host — процесс пользователя (не Windows-служба: отклонено владельцем из-за UAC); рестарт/стоп — через именованные события (tray, CLI `stop|restart`, RPC).
- Shell сам обеспечивает запуск host'а (ensure-host в HostClient), хост-надзор: одноэкземплярность, разрешение соседних exe (HostProcessLauncher) (уточнить детали надзора).
- Эмулятор — независимое приложение: никаких «спавнов» со стороны host'а; живой гейт (run-live-gate) запускает его вручную/скриптом.

## Спеки

- Топология стенда: [../specs/stand_topology.mmd](../specs/stand_topology.mmd); входная точка — [../specs/INDEX.yaml](../specs/INDEX.yaml).
- Wire-формат ACE: [../PROTOCOL.md](../PROTOCOL.md) (канон).
- Конфиг: [../specs/dotnet-server/Э5_config.csv](../specs/dotnet-server/Э5_config.csv) (канон).
