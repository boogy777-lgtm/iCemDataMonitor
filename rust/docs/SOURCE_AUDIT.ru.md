# Аудит исходной базы

База: `96e2206562c9389d1543095232115d81eae4b692`, проверено чтением файлов 2026-10-16.
512 файлов получены и проверены по Git blob SHA-1. Отсутствует только
`docs/Wireshark/iCemCrio.pcapng.bin` (1,415,304 bytes, hash в manifest).
Ни live-стенд, ни сборка исходного .NET в этой среде не запускались.

## Расхождения, которые нельзя переносить молча

| ID | Доказательство в legacy/dotnet | Вывод для миграции |
|---|---|---|
| D01 | README пишет FC03/04; Transport/ModbusTcpPublisher.ProcessRequest имеет 03/04/06/10 hex | Обязательно переносить FC06/16, writable зеркало и его hold/overwrite |
| D02 | specs/contracts/CONTRACT_BusV2.yaml упоминает dual writer; Host.Core/HostComposition.RegisterCoreServices прямо удаляет BusZoneWriter/LanBusSink | AddIn — единственный писатель; сниффер только reader. Старое описание dual writer не активировать |
| D03 | Host.Core/Endpoints содержит много маршрутов; HostAppFactory вызывает только MapFrozenOpsSurface | 4 действующих REST GET, остальные — неподключённые обработчики; см. route inventory |
| D04 | telemetry.proto LayerControlRequest.desired_state существует, TelemetryServiceImpl.SetLayer вызывает Toggle и не читает поле | Legacy API v1 — toggle, НЕ идемпотентный set. Не ретраить слепо; desired-state исправление требует отдельной версии/решения |
| D05 | ModbusTcpPublisher.ProcessWriteSingle проверяет Length<5, потом читает индекс 5 | При 5-байтовом теле возможен выход за границу и закрытие соединения. Rust требует ≥6; точный ответ malformed запросу фиксируется как intentional safety delta, не valid-traffic regression |
| D06 | HostComposition.StartLayersAsync запускает зарегистрированный sniffer без проверки layers.LanSniffer | Проверить реальный эффект lansniffer_on=0 в M1; не обещать соответствие таблице конфигурации без прогона |
| D07 | BusReader.IsActive учитывает age<5s; ModbusTcpPublisher.IsZoneActive использует сырое Active | UI может показать stale, а Modbus продолжить mirror при flag=1. В v1 это разные предикаты; не объединять автоматически |
| D08 | CLI get main переинтерпретирует int32 timestamp как float; gRPC отдаёт int32; /api/regs — ISO string; history stores float32 seconds | Сохранить отдельные projection contracts, не заменять одним JSON-типом |
| D09 | BusReader повторяет last-good на некоторых ошибках без выставления Stale; README обещает единый stale-hold | Перед M2 захватить failure traces. Любое улучшение stale-маркировки записать отдельным решением, не приписывать baseline |
| D10 | Modbus комментарий запрещает resize stale, но TickOnce вызывает ResizeToBus до проверки snap.Stale | Зафиксировать фактические traces роста/уменьшения/ошибок; не полагаться на комментарий |
| D11 | specs/INDEX.yaml сохраняет старое embed-описание emulator; Host/Contracts и EmulatorAppContract отделяют приложение | Эмулятор — отдельный процесс/окно/трей/CLI, host его не закрывает |
| D12 | docs пути установки расходятся с EmulatorAppContract/installer/Product.wxs | Перед MSI составить фактическую таблицу Directory/Component/Shortcut. Не выбирать путь по README |
| D13 | AceLanEmitter: BurstFrameIntervalMs=5.25, sleep округлён Math.Round до 5 ms | Проверять фактический pacing/timing, не обещать точность 5.25 ms по комментарию |

Иерархия доказательств: текущая задача пользователя → pinned executable call graph и
контрактные тесты → действующие specs → описательный README/комментарии.
Конфликт кода и требования помечается как открытое решение. Неопределённость не заменяется
предположением «как должно работать». Будущие функции (OPC DA, ace_full, снятые API)
не становятся частью rewrite автоматически.

## Исходники с наиболее важными контрактами

- `Burservis.iCem.DataMonitor/BusLayout.cs`, `BusWriter.cs` — остающийся producer.
- `iCemDataMonitor.Sdk/Bus/BusLayout.cs`, `Data/Bus/BusReader.cs` — layout и reader.
- `iCemDataMonitor.Transport/ModbusTcpPublisher.cs` — фактическая Modbus семантика.
- `iCemDataMonitor.Host/HostAppFactory.cs`, `Host.Core/HostComposition.cs` — active wiring.
- `iCemDataMonitor.Host/Grpc/TelemetryServiceImpl.cs` и оригинальный proto — RPC.
- `iCemDataMonitor.Host/CliMode/CliForwarder.cs` — argv/stdout/exit.
- `iCemDataMonitor.Host/Tray/*`, `iCemEmulator/TrayIconHost.cs` — отдельные tray/lifecycle.
- `iCemEmule.Core/Control/*`, `Emitters/*`, `Dfb/*`, `Core/UnitConversion.cs` — emulator.

Названия `Data/Bus` в сокращённых ссылках выше означают `iCemDataMonitor.Data/Bus`.
Полный список файлов и точные хеши — `migration/source-manifest.json`.

D14/D15 — новые явные safety/resource deltas Rust transport: [implementation status](IMPLEMENTATION_STATUS.ru.md).

D16 — baseline index.html inline SetUiTheme вычисляет data-theme !== light после того,
как main.js уже выполнил визуальный toggle. Node VM trace: visual light, RPC false.
В Rust-копии HTML исправлено на === light; внешний вид не меняется. Проверка baseline
допускает только две правки HTML: D16 и D17. Оригинальные .NET assets остаются неизменными.

D17 — inline GetUiPrefs требует literal "grpc-status: 0", отвергая валидный trailer
"grpc-status:0" из tonic-web. Добавлен строгий line-based parser с optional SP/HTAB,
без изменения внешнего вида или proto. Original .NET HTML не меняется.

D18 (open characterization): main.js parseTrailerStatus ожидает protobuf-like пары
с first byte 0x0a, тогда как gRPC-Web trailer body содержит ASCII headers. Для ASCII
он возвращает пустой status, а SubscribeMetrics client возвращается без немедленного
onError/onEnd; watchdog остаётся отдельным путём reconnect. Bundle не менялся ради
миграции. Нужен исходный .NET vs Rust browser disconnect/reconnect trace перед полным P10 PASS.

D19 — AceFrameBuilder.TryParseFrame XML обещает ровно 152 bytes и заголовочную
length-проверку, фактический код принимает length≥152 и не проверяет frame[12..13].
Rust codec сохраняет этот фактический predicate, включая LLC control 08/09.

D20 (open, full DFB network): DfbNetwork.Add инициализирует insertAt=_blocks.Count,
потом проверяет producer position≥insertAt; у существующих blocks position<count,
поэтому Add фактически append-only, несмотря на комментарий о topological insertion.
Execute выполняет блоки вне lock после создания копии списка. E2 сохраняет фиксированную
factory composition; C# oracle проверяет block order и полную signal/wire inventory.
Произвольный DfbNetwork.Add runtime не является реализованным Rust API.

D21 — SignalWireCodec.TryDecodeNative в исходнике складывает signed int offset/size
до проверки; overflow может дойти до исключения AsSpan. Rust codec использует
checked ranges и возвращает None для такого malformed definition; тест включает
byteOffset=i32::MAX и отрицательную size. Valid wire widths/offsets не меняются.

D22 (E3 input compatibility limits): Rust LAN DTO loader возвращает Err при
повреждённом JSON; original AcePayloadProvider/LantableRegistry подавляют parse errors
и могут удержать частично загруженные записи. Rust принимает UTF-8/BOM и ASCII case
lookup, полные File.ReadAllText encoding/OrdinalIgnoreCase semantics не перенесены.
Valid pinned ASCII catalogs проверяются отдельно; loader error parity не объявлен PASS.

D23 — E4 pipe вводит явные caps (32 sessions, request 64 KiB, response 8 MiB)
и запрещает remote clients. Это resource/security delta относительно baseline,
не полная error/ACL parity. См. EMULATOR_E4.ru.md.

D24 — E4 Engine::tick вызывает LAN send_superframe синхронно под engine gate;
исходный EmuEngineHost.Tick отправляет подготовленные vectors через Task.Run.
Bytes/pacing adapter проверяются отдельно, command latency/timing parity открыта.

D25 — .NET CI run 39029173037: D2GuardTests.ExactlyOne_ProtoFile_InSolution
сканирует весь repository и считает frozen Rust proto вторым contract source.
Rust-копия существует уже в main E3. Также зафиксирован MetricsSamplerTests
DropOldest failure в неизменённом C# тесте; причинность/flakiness не установлены.
Оба out-of-scope findings описаны в EMULATOR_E4.ru.md, исходники не менялись.

E6: D24 software cause устранена QueuedAceLanEmitter; аппаратный timing gate остаётся открыт.
D26 — explicit resource delta: один pending LAN superframe, busy вместо backlog, максимум
4096 vectors (по 124 bytes payload). See ADR 0005.
D25: на head d4830a2 Rust CI success; legacy run 39040898192 упал на duplicate proto
и ControlPipeServerTests.Dispose_DrainsInFlightReply_QuitAckReachesClient (empty ACK).
MetricsSampler в этом run не падал. E6 использует единственный original proto непосредственно,
не меняя .NET sources/guard. Происхождение legacy pipe failure не установлено.

E7 / 6cbbd74 CI: canonical proto guard PASS, legacy Host 220/220 и emulator 101/101 PASS.
Legacy ModbusContractTests.TickLoop_OpenFail_RecordsError_LoopSurvives_AndRecovers FAIL
(run 39045925516), C# не менялся; причинность не установлена. Rust Linux/full browser PASS;
MSVC process tests PASS, full Windows run 39045925359 FAIL на windows_pipe rebind code 5.
E7 явно освобождает client runtime и повторяет lifetime/rebind cycle 4 раза; ACL не ослаблены.
D27 — boot live resource bound 128 updates с явным lag RPC error. ADR 0006 фиксирует
read-only event-presence probe ui_alive вместо acquisition чужого singleton mutex.

## E8: legacy maintenance относительно immutable baseline

Main `a38d372911fa6972f9620a458588023141e6ef13` включает принятый PR #2
(`ad70c31c007a6b51ae6ca18f38d97c11ceec1b7f`); legacy CI run 39059431109 SUCCESS.
Неизменённый Verify-Baseline воспроизведён: Hash mismatch .github/workflows/ci.yml.
Исходный source-manifest/source_commit сохранён. `migration/legacy-divergences.json`
содержит ровно десять путей, baseline/approved Git blob SHA-1 и provenance PR/commit/reason.
Guard требует approved hash для каждой записи (в том числе отвергает случайный откат),
проверяет baseline hash и источник, запрещает дубликаты и записи вне manifest.
Proto, исходные assets и SPA не могут быть переопределены этим ledger.
D16/D17 проверки Rust SPA остаются отдельными и неизменными.
`tools/Test-BaselineGuard.ps1` проверяет 25 положительных/отрицательных сценариев;
Scaffold вызывает его перед полным guard. Rust CI теперь запускается при всех изменениях
в main/PR, включая legacy: прежний paths filter пропустил drift после PR #2.

Локальное дерево сверено с GitHub по blob SHA. Connector не выдаёт бинарный файл
`docs/Wireshark/iCemCrio.pcapng.bin` размером 1,415,304 bytes: локальный baseline
проверяет 512/513 и RequireComplete ожидаемо отклоняет неполную копию. Файл не
подменяется; полный 513/513 gate выполняется на checkout Windows/Linux CI.

D28 / E8: чтение LayerRuntimeController.Toggle(OpcDa) показывает внешний no-op, а не persist-only toggle. Уточнены API_SPEC и P16; Rust owner возвращает неизменённое OPC state без запуска server и записи INI. Web plan отделён от startup-effective routes/history; SetLayer desired_state по-прежнему игнорируется. Ошибки операций отражаются в LayerState.last_error, как legacy, вместо прежнего Rust INTERNAL. Полный sniffer и Modbus rollback/busy fault matrix остаются открытыми.

D29 / E8 audit: icem-desktop::monitor показывает активный SnifferSwitch, хотя Rust Host возвращает UNIMPLEMENTED и sniffer_available=false. Этот UI gap не исправлялся в Host-инкременте и не считается disabled-state parity. Следующий UI этап должен использовать фактическую availability, сверившись с inventory/XAML.

D30 / E9 Rust defect: monitor rearmed NavigateUi(section=0) on navigation/status
error, хотя Host query уже мог consume pending. Исправлено at-most-one accepted attempt
per explicit demand; lost-reply fault сохраняет следующий pending до нового show.
Exactly-once и гарантированная доставка потерянного перехода не заявляются.

D31 / E9 Rust defect: polling прекращался после READY, boot error сохранял прежние
стадии, новый stream replay сливался со старым. Polling продолжается; error/reset
инвалидирует прежний READY. Boot/status faults закрывают Session/runtime с transport
handles. Model и Windows pipe restart tests проверяют отсутствие старого READY.

D32 / E9 Rust defect: replay/live/lag-error send в boot RPC мог ждать заполненный
response buffer без наблюдения shutdown. Все boot sends используют общий cancellation
helper; fault tests проверяют полный buffer, lag/replay, stream cap и возврат permits.
Canonical proto, Host pending semantics и legacy sources не меняются.

После E9 пользователь разрешил исправить legacy quit-ACK CI failure и слить PR #5.
Исправлены registration-before-execute и shutdown-test timing: [разбор и границы](LEGACY_QUIT_ACK.ru.md).
Source manifest остаётся immutable; ledger дополнен двумя точными хешами и provenance.
Это отдельное последующее разрешение на legacy maintenance, не изменение исходного scope E9.
