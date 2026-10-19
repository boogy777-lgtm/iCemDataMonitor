# Матрица совместимости

**Ни одна строка пока не получила полный PASS.** Pure Rust и TCP tests выполняются; полного Windows/differential acceptance ещё нет.
`Source` ниже указан относительно `../`; Cargo.toml — корень нового workspace.

| ID | Функция | Сохраняемое поведение | Source | Этап | Доказательство |
|---|---|---|---|---|---|
| P01 | net48 AddIn | DLL остаётся без изменений; SDK/vendor references сохранены | Burservis.iCem.DataMonitor | M2 | Byte identity DLL + original iCem load + bus trace |
| P02 | MMF/read lifecycle | v5/64+2N, mutex500ms, DACL, abandoned, file disappear/recreate | iCemDataMonitor.Data/Bus/BusReader.cs | M2 | Windows fault matrix + source hash |
| P03 | Byte codec | LE header / BE words / CDAB / int32 timestamp / NaN bits | iCemDataMonitor.Sdk/Bus/BusLayout.cs | M0/M2 | Pure fixture tests + real AddIn differential |
| P04 | Modbus read/write | FC03/04/06/16, bounds, dynamic size, separate mirror | iCemDataMonitor.Transport/ModbusTcpPublisher.cs | M4 | Request/response golden bytes |
| P05 | Modbus mirror policy | active raw flags / inactive hold / stale hold / reserve write | iCemDataMonitor.Transport/ModbusTcpPublisher.cs | M4 | Deterministic tick/write-conflict traces |
| P06 | Web SPA | Original dashboard, charts, theme, web_on gating | iCemDataMonitor.Host/wwwroot | M4/M6 | Browser scenarios and DOM/UI comparisons |
| P07 | REST | 4 active GET routes and exact projections/errors | iCemDataMonitor.Host.Core/HostComposition.cs | M4 | OpenAPI + golden JSON |
| P08 | History | 600 samples/addr,1Hz,4..598 even,503-before-400 | iCemDataMonitor.Data/Bus/RingHistory.cs | M4 | FIFO/cadence/query tests |
| P09 | gRPC/pipe/Web | 10 RPC, proto tag/oneof/reserved stability | iCemDataMonitor.Contracts/Protos/telemetry.proto | M4 | Old C# clients against Rust + wire fixtures |
| P10 | Metrics/boot streams | initial full/deltas,clamp200,replay boot,disconnect | iCemDataMonitor.Host/Grpc/TelemetryServiceImpl.cs | M4 | Reconnect/slow client/cancel traces |
| P11 | Host CLI | pure JSON,0/1/2 exits, --start,snapshot,get,header,stop,restart | iCemDataMonitor.Host/CliMode/CliForwarder.cs | M3/M4 | argv/stdout/stderr/exit golden |
| P12 | Host tray | same .ico,5 menu commands,console captions,tooltip63,1Hz | iCemDataMonitor.Host/Tray/HostTray.cs | M3 | Windows tray/menu/Explorer test |
| P13 | Host lifecycle | single instance,event ownership,close shell only,restart budgets | iCemDataMonitor.Host/Tray/HostLifecycle.cs | M3 | Repeated stop/restart + no orphan processes |
| P14 | Autostart/console | HKCU Run, --autostart,console close guard | iCemDataMonitor.Host/Tray/AutostartManager.cs | M3 | Registry/current-state/console scenarios |
| P15 | Shell | Состояние/Панель,ensure host,splash,reconnect,status lamps | iCemDataMonitor.Shell.WinUI/MainWindow.xaml | M6 | Operator walkthrough + DPI/focus |
| P16 | Layer switches | runtime Modbus/sniffer; persist Web; OPC external no-op; actual truth | iCemDataMonitor.Host.Core/LayerRuntimeController.cs | M3/M4 | Failure/start/stop/toggle parity + D04/D06 |
| P17 | ACE sniffer | wire reader,decode/counters/status,Npcap availability | iCemDataMonitor.Transport/AceSnifferService.cs | M4 | pcap replay/live capture, no bus writes |
| P18 | INI | read-once,strict validation,retired keys,migration,active path | iCemDataMonitor.Core/Config/IniConfig.cs | M2 | Original config test vectors |
| P19 | XML/registries | emulator config,templates,firmware/variable/lantable assets | iCemEmule.Core/Core/EmuSettings.cs | M2/M5 | Original load/save/default roundtrips |
| P20 | Emulator isolation | own process/folder/tray/CLI; host never spawns/stops it | iCemEmule.Core/Control/EmulatorAppContract.cs | M3/M5 | Independent start/close + singleton tests |
| P21 | Emulator UI | COM/LAN/Units,all controls,close-to-tray,open/exit | iCemDataMonitor.Emulator.WinUI/EmulatorPage.xaml | M6 | Operator UI checklist covers each control |
| P22 | Emulator control | 21 commands,text/JSON,watch,quit ACK,exit codes | iCemEmule.Core/Control/CommandProcessor.cs | M5 | Command and error golden lines |
| P23 | FLECS COM | 32 fields,1Hz,serial settings,port selection/reconnect | iCemEmule.Core/Emitters/ComSerialEmitter.cs | M5 | Virtual COM capture + byte compare |
| P24 | ACE emitter | raw802.3,152bytes,SV order,equipment,firmware,pacing | iCemEmule.Core/Emitters/AceFrameBuilder.cs | M5 | pcap and frame fixture equality |
| P25 | Simulation/DFB | all existing blocks,manual/frozen/native overrides | iCemEmule.Core/Dfb | M5 | Fixed dt/input trajectories + tolerance report |
| P26 | Unit conversion | 4 presets,per-group overrides,LAN conversion order | iCemEmule.Core/Core/UnitConversion.cs | M5 | Units cross product golden vectors |
| P27 | Logs/diagnostics | existing paths,rotation cap,startup errors,selftest/landiag | iCemDataMonitor.Sdk/Diagnostics/StationFileLog.cs | M3/M5 | Failure evidence and capped file tests |
| P28 | Installer | icons/shortcuts/paths/upgrade/uninstall/rollback/offline | installer/Product.wxs | M7 | Clean VM install matrix |
| P29 | Size/safe code | no .NET10/WinAppSDK in rewritten payload,application unsafe forbid + native ADR 0001 | ../Cargo.toml | M7/M8 | Full payload measurements + cargo gates |
| P30 | Dormant surfaces | COM tee/com0com/old REST handlers preserved in baseline; active wiring absent | iCemDataMonitor.Host.Core/Endpoints | M1 | Call graph evidence; revive only explicit scope decision |

COM tee/com0com код сохранён как исходная база. В действующей host-композиции он не подключён;
если целевой установленный релиз имеет отличающуюся ветку, M1 обязан заменить baseline
до заявления об идентичности. OPC DA сейчас stub, не реализованный OPC server.

Частично реализованы P02/P03/P04/P05/P06/P07/P08/P09/P10/P18: Windows MMF reader, pure decoder,
FC03/04/06/16, mirror/TCP, строгий INI parser, web REST/history и SPA RPC subset.
[Web-инкремент](WEB_STATUS.ru.md); GPUI и desktop visual acceptance ещё не выполнены. [Границы M2](M2_STATUS.ru.md).
Тесты и намеренные отклонения D05/D10/D14/D15: [implementation status](IMPLEMENTATION_STATUS.ru.md).

P19/P23/P24/P25/P26 начаты частично: explicit template loading и COM waveform/formatter,
ACE frame/signal codec, три DFB-примитива и unit conversion. Физических emitters и
полной DFB network пока нет. Проверки/границы: [emulator E1](EMULATOR_STATUS.ru.md).

E8: baseline maintenance ledger и Host Web/OPC owner integration — [код, проверки и открытые пробелы](HOST_E8.ru.md). P16 остаётся PARTIAL: sniffer/Modbus failure rollback/busy и Windows аппаратная приёмка не доказаны. Список старых частичных реализаций выше — исторический, не текущий backlog.

E9: P10/P13/P15 дополнены [boot/navigation fault checks](HOST_E9.ru.md): replay/reset,
lag/cancel/stream permits, Shell session release и at-most-one consuming navigation
attempt per explicit demand. Partial software evidence не закрывает Windows operator,
startup-before-IPC, ensure-host, console, hardware или visual/MSI gates.

E10: P16/P17/P15 дополнены [Host capture owner](HOST_E10.ru.md): реальный
Npcap receive, pause/resume, bounded counters, actual RPC/summary, UI availability.
Статус PARTIAL. Decode/signals/service-vectors, capture-to-file, real NIC/Npcap
и Windows operator/visual acceptance остаются открытыми.

E11: [первый запуск/ensure, CLI, config discovery и MSI ACL](HOST_E11.ru.md).
Shell автоматически запускает отсутствующий Host один раз; reconnect не повторяет
spawn. Строгая повреждённая конфигурация сохраняется и блокирует запуск; отсутствующая
создаётся из схемы. CLI/partial software evidence не закрывают SDK/UI/hardware gates.

## E12: управляемые логи (актуальное дополнение)

[HOST_E12](HOST_E12.ru.md) и [ADR 0008](adr/0008-managed-file-logs.ru.md):
общий файловый owner, native ON/OFF/clear для Host/Shell и независимого emulator,
сохранённая настройка, bounded rotation и безопасная частичная очистка. Frozen
proto/web/emulator contracts неизменны; internal JSON HTTP/2 control v1 доступен
только на существующем Host pipe. Mapping: 176 legacy + 6 explicit extensions,
visual parity NOT_PROVEN. P27 остаётся PARTIAL до Windows/operator/hardware gates.
Console-close/window lifecycle — следующий самостоятельный инкремент.

E13: [console owner и Ctrl+C](HOST_E13.ru.md) дополняют P10/P15 без изменения
статуса PARTIAL. Show/Hide/software faults покрыты; Windows process scenarios
добавлены, но runtime NOT_RUN. X → hide не доказано и не реализовано: legacy
CTRL_CLOSE TRUE не является гарантией Windows. Visual/window parity OPEN.

E14: [emulator X/Hide/Open и UI shutdown](HOST_E14.ru.md) реализованы без новых
контролов. Windows UI/process gates NOT_RUN; window/visual parity PARTIAL.
Console Host X → hide отдельно OPEN.
