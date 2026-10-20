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


E15: [Host console X → Hide](HOST_E15.ru.md), [ADR 0011](adr/0011-host-diagnostic-window.ru.md).
Собственное диагностическое окно; frozen API и E14 сохранены. Ctrl+C относится к
окну Host, после detach родительский терминал не адресует Host. Windows/operator
результаты фиксируются по exact head; полная parity не объявляется.


E16: [Host passive decode/catalog](HOST_E16.ru.md). Capture owner выполняет
Host-specific decode и bounded cache/tracking; periodic witness идёт в общий logger.
Dormant REST/file capture не активированы; hardware/visual parity не объявляется.


E17: [типизированный application API emulator и общий watch client](EMULATOR_E17.ru.md),
[ADR 0014](adr/0014-emulator-application-api.ru.md). GPUI передаёт Command напрямую;
pipe v1 остаётся compatibility adapter, включая coercions и исторический save ACK.
Внутренний результат отделяет apply/save failure; CLI text/JSON watch получает поток,
а не только ACK. Software CI не закрывает operator/SDK/hardware gates.


E18: [типизированный Shell → Host](HOST_E18.ru.md). Проверенные параметры/status,
generated replies, общий bounded worker. Logging/dashboard JSON — следующие
границы; visual/SDK/hardware gates остаются открытыми.


E19: [типизированное управление логами](HOST_E19.ru.md): Command/Status/Reply,
Incomplete effects и Unknown без retry, строгий неизменённый internal v1 adapter.
Локальные clear counts не выводятся из v1 текста; GPUI получает typed facts.
176+6 mapping прежний; Windows exact-head и operator/SDK/hardware gates отдельно.


E19 D45/D46: legacy CI 39249279178 retains two original FAIL results. Freshness test
now respects capacity-one DropOldest delivery and always stops its producer;
virtual-port retry timeout gains diagnostics only (cause unknown). Original
assertions/deadlines remain; no production parity row is upgraded by test changes.
[Audit/evidence](SOURCE_AUDIT.ru.md) and exact ledger preserve immutable baseline
and 14 existing approvals. New exact-head Windows results are a separate gate.


D48 / PR16: post-merge E19 Legacy run 39253155562 failed the overflow test's initial
full-sample assumption; the original failure is retained. Test-only repair acquires
a loss-free initial sample under the same 15s budget, then preserves actual overflow,
newer heartbeat marker and exact single retained sample assertions. Production
sampling and parity status are unchanged. [Audit/evidence](SOURCE_AUDIT.ru.md).
Windows exact-head PR16 execution is a separate gate, NOT_RUN before CI.


E20: [типизированные ответы emulator](EMULATOR_E20.ru.md),
[ADR0017](adr/0017-typed-emulator-payloads.ru.md): 21 concrete backend payload families,
private ControlResult, typed Execution → derived GPUI View/Signal identity; JSON только
на v1 adapter/display formatting, без response roundtrip. Полные 43 E19 wire frames,
parser C# domain-shaped fixtures и mismatch/nonfinite/partial/save-failure checks.
D49: dashboard status отображает уже typed Model.host, сохраняя last-known при fault.
Dashboard REST/selftest/landiag и visual/operator/SDK/hardware gates остаются открытыми.


E21: [проверенные dashboard snapshots](DASHBOARD_E21.ru.md),
[ADR0018](adr/0018-typed-dashboard.ru.md): owned Registers/History через Shell и GPUI,
полная geometry/value/UTC/liveness validation, independent history failure и
current-selection filtering. REST/SPA/proto, appearance и 176+6 mapping прежние.
Software/exact-head Windows evidence фиксируется отдельно; physical/SDK/hardware
acceptance остаётся OPEN. Далее selftest/landiag и stdin CLI отдельными increments.


E22: [встроенный emulator --selftest](EMULATOR_E22.ru.md): полный mapping T1–T26, first-argument dispatch до startup, read-only EXE-relative emulator catalogs, isolated native TestCtl и полный i32 failCount. Windows process/release smoke tests добавлены; local Linux не доказывает Windows runtime. D22/operator/SDK/hardware gates открыты; E23 — explicit --landiag и stdin CLI.
