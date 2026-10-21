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

D33 / Windows packaging: default Host/autostart and emulator tray icons used
CARGO_MANIFEST_DIR paths captured on the build machine. The common
icem-platform-windows::application_asset resolver now requires EXE-adjacent assets
and reports a missing resource explicitly. User settings retain their own paths;
source-tree desktop runs must copy icons to target/debug/assets. A relocated ZIP
supplies sidecars through windows-payload.json. The emulator keeps its tick-worker
stop/join cleanup when asset lookup fails.

Windows release script builds a locked MSVC release payload with static CRT and
checks its manifest/checksums after ZIP extraction. Existing Linux CI build removed
by the user's Windows-only instruction; Chrome/theme contracts moved to Windows.
The packaging changes do not modify legacy source behavior, the baseline manifest or active endpoints.
Legacy CI defects remain outside priority per the user's instruction.

Windows installer follow-up: the verified inventory now also generates a WiX 5.0.2
development MSI, with independent upgrade identity and canonical legacy runtime path. `rust-vX.Y.Z` tags/
release drafts define the N+1 patch sequence; the Windows workflow includes actual
install/upgrade/downgrade/uninstall checks before uploading an MSI or creating an
optional draft prerelease. Production MSI identity/AddIn cutover remains unaccepted.

D34 / observed Windows release blocker: on head 5d464d10f711483dbec5ab6267dc806216cbdba6,
full Windows Rust CI run 39069337634 passed, while release run 39069337765 failed
in unchanged icem-web/tests/windows_pipe.rs (rebind after both owners closed,
PermissionDenied/os error 5), before release/MSI build. IOCP/runtime shutdown
ownership is a hypothesis, not a proven cause. No test was bypassed or mutation
retried. This remains a Rust lifecycle issue, unrelated to legacy CI priority.

D35 / M7 packaging correction: schema-2 payload now requires the unchanged net48 AddIn
with source/SDK/DLL provenance and managed x64/net48 metadata checks. Complete
releases cannot skip a missing SDK/DLL. Runtime/AddIn retain the original
ProgramData / iCem Bin/Modules mounts. The initial whole-template projection has
been superseded by the v3.1 ownership audit below. Fixture MSI contracts do not
prove SDK-backed DLL build or application behavior. See installer/README.md.

D36 / packaging registry ownership: legacy merged its 3507-variable Host registry
and 140-variable emulator registry using an explicit emulator override filename.
The initial Rust dev plan instead shipped only emulator/templates at the shared
Host path. Schema-2 packaging now keeps both original inputs: Host templates/
variable_registry.json and emulator/templates/variable_registry.json. Role paths
are manifest data consumed by launchers/release smoke; hash tests reject either
substitution. No canonical registry contents or runtime protocol code changed.

D37 / candidate MSI desktop evidence: runs 39102512162 and 39102823775
installed payload/autostart but produced Warning 1909 and no desktop link.
Adding CreateFolder did not fix it; absence of the folder was an unproven
hypothesis, not an established cause. The fixture used only a 128-byte PE header,
not a valid shell target. Windows fixture now uses an OS executable; real .lnk
creation/target/removal remain mandatory, never replaced with XML-only acceptance.

D38 / installer ownership audit v3.1: [mechanisms and executable invariants](INSTALLER_AUDIT.ru.md).
Removed whole legacy Product projection, WMI/kill/cancel CAs, force-close Util,
elevated recursive wipe, deletion of StartupApproved, ARP hiding and forced repair
rewrite policy from Rust MSI. Retained original payload/bitmap generators and
Burservis assets. A small Rust product uses native MSI ownership and disables
Restart Manager auto-shutdown. GUID namespace now covers all payload components,
including AddIn, not only opt-in components. Tests compare legacy/Rust GUID sets
and relocated harvests and check opt-out/opt-in, N+1, downgrade and preservation
of unowned data at uninstall. Original .NET sources/installer remain unchanged.
Actual running-process/rollback/visual and SDK integration acceptance remain open.


D39 / уточнение installer lifecycle: запрет любого force-закрытия в D38 был
слишком широк. Пользователь требует явную force-опцию для зависших ресурсов iCem
и повторную полную установку для оператора без IT-навыков. Rust MSI включает
native Restart Manager (file-owner scope, не image-name), per-operation
MSIRMSHUTDOWN checkbox, native saved-path/options recovery и repair ALL/amus только
для установленного продукта вне uninstall/major-upgrade removal. Тесты проверяют
missing/corrupt files, plain repeat, explicit opt-out, grace/force и foreign process
того же имени. Свойства и mutable payload guard не ослаблены; script CAs и wipe
по-прежнему отсутствуют. Реальные vendor/hardware/first-run gates не объявлены PASS.

D39 дополнение: MSI checkbox Off представлен null, не строкой `0`. Все три
checkbox проходят native UI/backend conversion; Windows UI Automation regression
проверяет defaults/CLI/saved choices, Back/Cancel и committed repair opt-out.
Silent MSI проверки отдельно сохраняются; GUI fixture не публикуется.

## E10: уточнение активного sniffer wiring

D06 теперь охарактеризован кодом и regression: HostComposition.StartLayersAsync
запускает зарегистрированный capture независимо от lansniffer_on. Controller хранит
отдельный session plan. При ini=0 capture RUNNING, первый toggle планирует true
и оставляет RUNNING; второй ставит PAUSED. Rust сохраняет это различие.
AceSnifferService выбирает первый подходящий Ethernet автоматически, хотя его
комментарий утверждает manual-only. IsAceFrame слабее AceFrameBuilder parser:
ровно 152 bytes + LLC 03/03/08|09; witness не заменён emitter predicate.
D29 UI gap исправлен по backend availability. Намеренные E10 resource/availability
deltas и оставшийся decode/driver backlog перечислены в HOST_E10/ADR 0007.

Main 1f205c1: Rust push CI 39093573051 SUCCESS. Legacy push CI 39093573086 FAILURE:
MetricsSamplerTests.DropOldest_SlowConsumerLosesSamples_BacklogNeverAccumulates и
ConsoleShowAfterHideProbeTests.Show_AfterHide_OnRealConsole_IsVisible_EvenThoughShowWindowReturnsFalse.
Emulator 103/103, Host 225/226; это не новый quit-ACK failure. Legacy не менялся в E10.

## E12: управляемые логи (актуальное дополнение)

[HOST_E12](HOST_E12.ru.md) и [ADR 0008](adr/0008-managed-file-logs.ru.md):
общий файловый owner, native ON/OFF/clear для Host/Shell и независимого emulator,
сохранённая настройка, bounded rotation и безопасная частичная очистка. Frozen
proto/web/emulator contracts неизменны; internal JSON HTTP/2 control v1 доступен
только на существующем Host pipe. Mapping: 176 legacy + 6 explicit extensions,
visual parity NOT_PROVEN. P27 остаётся PARTIAL до Windows/operator/hardware gates.
Console-close/window lifecycle — следующий самостоятельный инкремент.

## E13: ConsoleCloseGuard — неподтверждённая legacy гарантия

`iCemDataMonitor.Host/Tray/ConsoleCloseGuard.cs` утверждает X → hide и выживание
Host через TRUE из CTRL_CLOSE handler. Документация Microsoft HandlerRoutine
говорит о завершении процесса и при TRUE; ссылки и решение в
[ADR 0009](adr/0009-console-owner.ru.md). Такой порт не считается guard-ом.
E13 реализует общий Show/Hide owner и Ctrl+C teardown; X → hide остаётся OPEN.
Frozen baseline/ledger не изменяются.

## E14: Rust window lifecycle defect

Legacy emulator MainWindow/App требует Hide того же окна и отдельный Quit. Rust
раньше делала destroy/recreate; E14 вводит close interceptor и async request/probe
owned window. GPUI 0.2.2 App::hide на Windows пуст. Bridge join переносится из
View Drop во владельца после UI loop, сохраняя in-flight запись. [ADR 0010](adr/0010-desktop-window-lifecycle.ru.md).
Владелец сообщил об исчерпанном лимите Actions; failure до steps не считается
ошибкой исходников и не переписывается как PASS.


D40 / E16: Host decoder отличается от emulator SignalWireCodec: FF применяет factor,
тип имеет fixed width, HX/UC — text, sentinel после scaling. E16 сохраняет active
Host semantics в отдельном pure codec и проверяет оригинальным C# oracle. Checked
negative/overflow spans и catalog/tracking budgets — explicit safety/resource deltas;
D22 loader limits и deferred firmware ace_full не закрываются. См. ADR 0012.

### D41 — valid JSON decimal precision (E16)

Штатный serde_json float_roundtrip включён явно после Windows oracle 1 ULP failure.
Общий JSON parser workspace получает корректное round-trip f64 parsing; это не
изменение D22 malformed DTO/encoding/Unicode policy. Versions/dependencies прежние.
Factor IEEE-bit oracle и decimal regression обязательны; float comparisons не ослаблены.


## D42 / E17 — emulator control audit

Legacy EmuSettings.Save подавляет исключения. Прежний Rust Service логировал save
failure, но возвращал обычный ok:true. Application API теперь возвращает
AppliedNotPersisted отдельно от frozen v1 projection. GPUI получает результат без
JSON string roundtrip. Failed live endpoint replacement и COM-before-LAN partial
override отмечаются AppliedWithError, без выдуманного rollback. Реальные I/O эффекты
не следует объявлять отсутствующими по одному false envelope.

CLI watch определялся legacy по первому text token; JSON watch завершался после ACK.
Прежний Rust CLI получал только ACK для обеих форм. Общий consumer теперь принимает
text/JSON watch, ACK/status, cancellation и finite deadlines. Потерянный mutation
reply даёт Unknown и не повторяет команду. CLI transport error теперь exit1, usage2
(раньше Rust смешивал их в exit2). Client errors сериализуются, не конкатенируются
в JSON. 2/2.9/NaN coercions остаются в compatibility adapter. Внешний v2 не введён.

[ADR 0014](adr/0014-emulator-application-api.ru.md) определяет границы и intentional
client/resource deltas. SelfTest:null не меняется; app selftest/landiag — следующий
кодовый инкремент. D22, dormant capture-to-file и software/hardware gates прежние.


D43 / E18: Shell HostPort подставлял false/0 для отсутствующих параметров,
а Model::apply("status", Ok({ok:true})) подтверждал связь без data. Typed Request
исключает missing bool/invalid layer/selection; Status требует layers и три distinct
zones. Mismatch/malformed status не подтверждает freshness. См. ADR 0015.

D44 / E18 Legacy CI: три UiNavigationServiceTests синхронно ждали mutex holder,
запущенный через Task.Run, затем holder жил fixed sleep 10 s. Windows run
39221977532 не дождался старта holder. Общий dedicated-thread fixture подтверждает
acquisition и держит mutex до explicit release; bounded join и ownership regression
проверяют teardown. Assertions и production navigation неизменны; hash ledger точный.


E19: [типизированное управление логами](HOST_E19.ru.md): Command/Status/Reply,
Incomplete effects и Unknown без retry, строгий неизменённый internal v1 adapter.
Локальные clear counts не выводятся из v1 текста; GPUI получает typed facts.
176+6 mapping прежний; Windows exact-head и operator/SDK/hardware gates отдельно.


## D45 / E19 — legacy freshness test assumes delivery of the first sample

Legacy run 39249279178, job 118048857901, head ba61f4262aac5fe10e5a9868feea22190c50fee1:
`Freshness_ValueChangesBetweenSamples_HeaderAndZone` FAIL, line127, first consumed
sample has no addr200. Producer capacity-one DropOldest can evict the full sample
before consumer execution; first consumed is not guaranteed to be first produced.
The fixture has 10001 words, so the actual initial full sample includes addr200.
This is a confirmed harness assumption, not evidence of a production sampler defect.

Class: freshness test in `iCemDataMonitor.Data.Tests/MetricsSamplerTests.cs`, not
production sampling/backpressure. Inv: original float type/1→2 and heartbeat 100→101
assertions remain; only observed sample loss permits another bounded attempt.
N+1: reuse the adjacent delta test's bounded loss-free pattern; cancel/join every
subscription and dispose the SectionWriter. Mutation of words 200/201/heartbeat 3 is
one real mutex-guarded MMF transaction. Budget 15s and producer join 10s bound cleanup.
Original FAIL remains evidence. Source-only provenance commit
6d2d87c2ac8daaad555e886bb5f673bc531aa30a; PR15, exact ledger approval replaces the prior
approved blob, with immutable baseline unchanged.

## D46 / E19 — legacy virtual-port retry timeout, cause unknown

Same run/job/head: `TeeLoop_VirtualPortMissing_RetriesConnection` FAIL after 17s,
line468 (second-attempt wait). PAIR_MISSING/pair=false assertions completed; the
second virtual factory invocation was not observed within 15s. Production path
awaits 5s in catch and 3s in finally before retry. These facts do not prove scheduler
starvation, a production defect, or another race; root cause remains unknown.

Class: timeout diagnostics in `iCemDataMonitor.Transport.Tests/SerialTeeServiceTests.cs`;
production retry timing is outside this edit. Inv: exact state/attempt=2 assertions
and 15s/10s waits unchanged; timeout reports elapsed, actual state/pair/up,
virtualAttempts, ExecuteTask status/exception. N+1: existing catch preserves the
original TimeoutException as inner evidence and existing finally performs cleanup.
NullLogger remains; no new event recorder or behavioral retry was introduced.
Source-only provenance is the same 6d2d87c commit. Ledger still contains 14 existing
approvals, baseline hashes/source manifest unchanged. Windows verdict requires a
new exact-head CI run; neither patch turns the original FAIL into PASS by itself.


## D47 / E19 — mapping guard still expected wire action strings

Rust run 39249279337 / job 118048857814 on ba61f4262aac5fe10e5a9868feea22190c50fee1
failed at Verify-DesktopMapping.ps1:39: `Unimplemented native extension: HostLogsSwitch`.
The renderer correctly moved to Command::SetEnabled/Command::Clear; the guard still
searched for removed literal wire strings. This was a guard defect, not missing UI.

Class: native logging extension source mapping (`tools/Verify-DesktopMapping.ps1`),
not the frozen 176 controls or HTTP grammar. Inv: all six surface/composition and
unique identity checks remain; index 0 must be set/SetEnabled, index 1 clear/Clear.
The guard reads the real enumerated renderer array and rejects other command order.
N+1: the existing fixture mutation pattern now runs eight mapping cases from Scaffold:
actual mapping, missing SetEnabled, missing Clear, swapped renderer order, swapped
mapping indices, unknown action, missing composition surface, duplicate identity.
No dummy wire strings were added to the renderer. Python-equivalent validation is
local evidence only; actual PowerShell and Windows exact-head acceptance remain
separate. The original FAIL remains in the evidence history.


## D48 — post-merge E19 overflow test also assumed first-sample delivery

Legacy run 39253155562 / job 118061709492 on merged E19 main
c1c4c0daf2addc59d517e853d4a8faa3a0ba5922 failed
`DropOldest_SlowConsumerLosesSamples_BacklogNeverAccumulates`, line 187: the first
consumed sample had no addr200. D45 freshness and Serial diagnostics tests passed.
The capacity-one DropOldest producer can evict its initial full sample before the
consumer runs; this test shared the confirmed D45 harness assumption. The original
post-merge FAIL is retained, not described as a successful E19 legacy run.

Class: initial full-sample observation in the legacy overflow test
`iCemDataMonitor.Data.Tests/MetricsSamplerTests.cs`, not production backpressure.
Inv: initial float 1.0 assertion remains; retry is allowed only after observed
initial DroppedCount != 0 within the same total 15s budget. Wrong/missing values on a
loss-free initial attempt fail. Actual drops >= baseline+2, marker heartbeat 3 AFTER
queue-full evidence, producer stop/join, Assert.Single retained, retained heartbeat 3,
and drops >= markedBaseline+2 remain exactly the original proof; cleanup 10s remains.
N+1: adjacent freshness/delta tests already use loss-free observation; remaining
flag-only/empty-stream cases do not require the initial full sample. Other methods
are unchanged. Each discarded attempt cancels/joins its producer; writer is disposed.

Only the existing Metrics ledger approval changes: source-only commit
ba8d1af1ca49e4ade40070ff13a358b93cb9a850, PR16. Immutable baseline hashes and 14 approval
count remain unchanged; production .NET sources are untouched. Local source checks
confirm unchanged assertions/overflow proof and other methods; Windows runtime is
NOT_RUN before exact-head PR16 CI. This change does not upgrade hardware/UI parity.


## D49 / E20 — Host dashboard status retained obsolete JSON snapshot reads

E19 `dashboard_view.rs` read Model.snapshot[busOpen/addinAlive/zones/layers], but
E18/E19 Model::apply_host writes only the acknowledged typed Model.host. Thus those
cards could remain unknown/false despite a valid Host response. Source evidence:
E19 production tree 8b70979f512a5e0daeac3025a6d4a97d3d4199c9; this was not a Host RPC
or producer state defect. The original display seam is recorded, not claimed parity.

Class: status display scalar reads in desktop dashboard_view/host; dashboard REST
register/history adapters are outside this edit. Inv: display derives from the
latest typed Host status, unknown is explicit, transport fault keeps last-known
facts while connected=false. Portable regression changes actual status twice,
checks bus/AddIn/zones/age/layers/error, then checks retained facts after fault.
N+1: existing StatusView derives from the already checked Host status; no new RPC,
listener, Engine, dispatcher or queue. Layout/controls/palette unchanged; physical
UI acceptance remains NOT_RUN. Dead Model.snapshot/JSON Model.apply removed; old
Host fixture assertions now exercise apply_host instead of that inactive dispatcher.


## D50 / E20 — units display order depended on JSON map feature unification

PR17 Rust/MSVC run 39265963972 / job 118105477073, exact head
c4bbc1fa16a8d341c6d8f30684375959fd519c86, failed
`typed_display_retains_nonfinite_and_escaped_units_policy` at model.rs:379.
BTreeMap display sorted keys; the Windows GPUI graph enables serde_json/preserve_order,
so the legacy Value display retained insertion order. Escaping matched. Linux's
sorted Value accidentally concealed the production display order regression.
The failed expected fixture itself put density before temperature, also differing
from E19 production; its order is not used as a new contract.

Source: rust/crates/icem-emulation/src/runtime.rs:324–327 at E19 head
400cdc6c6ef77602cae6b48584b5b95afb850486 / production tree
8b70979f512a5e0daeac3025a6d4a97d3d4199c9: preset, flowOverride,
temperatureOverride, densityOverride, volumeOverride, levelOverride, pressureOverride.
Class: seven scalar unit-summary display fields in desktop/emulator.rs, not pipe
response JSON or dashboard REST. Inv: private ordered Serialize subject preserves
that exact string order plus standard serde escaping with and without preserve_order;
full literal-string regressions include all seven values and null/control/Unicode.
N+1: exhaustive UnitStatus destructuring fails compilation for an unreviewed field;
no dispatcher/Engine/worker changes. Direct desktop → existing serde std/derive edge
only; dependency packages/versions and unsafe/pins unchanged. Physical appearance
acceptance remains NOT_RUN; actual Windows regression requires the new exact-head CI.


## D46 follow-up / PR17 — retry signal versus deferred WaitAsync completion

Exact E20 head 1111e3c9c7bb56ad123e7c254bb1b621ad05a640: Legacy run 39268707217 /
job 118115014371 FAIL at SerialTeeServiceTests line469, the second-attempt wait.
D46 diagnostics reported elapsed 15.5113553s, virtualAttempts=2, RUNNING,
pairOk=true/up=true, ExecuteTask=WaitingForActivation and no exception. This proves
progress by catch time, not before the 15s deadline. Previous c4bbc1fa Legacy run
39265964072 passed 941 tests. Original failures remain in the history; neither
retry progress at catch nor a later passing run establishes the exact timeout cause.

Class: lifetime of the retry-only serial fixture in
`iCemDataMonitor.Transport.Tests/SerialTeeServiceTests.cs`; production COM forwarding
and retry timing are outside the change. Inv: preserve the first PAIR_MISSING /
pair=false snapshot, exact second-attempt assertion, both 15s waits and 10s cleanup;
cancel immediately on the same second-factory call stack after signaling its TCS.
An additional post-Stop assertion requires exactly 2 attempts, excluding a third.
N+1: existing disposal/cancellation boundaries own cleanup; recurrence diagnostics
record Stopwatch frequency, before/after TrySetResult ticks, returned bool, source
TCS status, wait-start and deadline ticks. No new fake framework, global worker
setting, test parallelism change, exception masking or production change was added.

Mechanism evidence is narrower than the CI hypothesis. In runtime 10.0.13,
[Task.cs CancellationPromise](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs)
registers a completion action (lines 2853–2930, InvokeMayRunArbitraryCode=true);
RunContinuationsAsynchronously makes RunOrQueueCompletionAction queue that action
with preferLocal=true (lines 3464 and 3604–3612). A completed source TCS can therefore
still have a pending WaitAsync wrapper. The fixture previously entered synchronous
steady-state reads immediately after TrySetResult. Cancellation stops that unrelated
work on its existing token. The Hosting.Abstractions 10.0.0
[BackgroundService.StartAsync](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/BackgroundService.cs)
already uses Task.Run; a captured test synchronization context was not established.

A scratch-only console experiment ran SDK 10.0.402 / runtime 10.0.13 on Linux. It
limited its own process to two workers, registered a RunContinuationsAsynchronously
TCS with WaitAsync(1s), signaled 2 on worker A, then held A in synchronous work. Worker B
was gated until a dedicated controller released B after 1.5s, allowing the timeout to
compete with A's queued completion. Finally released A and joined all work. A variant
canceled after signaling and skipped A's synchronous wait. These worker controls
were never added to repository code. Frequency was 1,000,000,000 ticks/s:

| Controlled case | Wait start / deadline ticks | Before / after TrySetResult ticks | Wrapper result |
|---|---|---|---|
| Baseline | 885818 / 1000885818 | 19223376 / 19379852 | Timeout/Faulted; total 1.5597983s |
| Same-stack cancellation | 509487 / 1000509487 | 29788765 / 34800144 | Result 2 at 44292582 ticks (~44ms); total 1.5417121s |

Both signals returned true and source tasks were RanToCompletion. Cancellation's
44ms wrapper latency is distinct from total 1.54s, which includes the controller
join. A one-worker experiment also completed successfully, showing this is an
ordering-sensitive mechanism. Controlled workers/shortened 1s wrapper prove a
possible race and its release, not the original Windows 15s scheduling timeline.
Reproduction commands were `dotnet build Repro.csproj -c Release --no-restore`,
then `dotnet Repro.dll` and `dotnet Repro.dll --cancel-after-signal` for this bounded
scratch algorithm; no new executable or worker-tuning framework is part of PR17.

Focused isolated Linux tests using the real production SerialTeeService and its
original 8s retry path passed once each: instrumented baseline (signal 8.212s,
wait-return 8.222s, total 8.240s), cancellation variant (signal 8.029s,
wait-return 8.038s, total 8.040s). Both kept original assertions and 15s/10s bounds;
these passing cold tests do not reproduce the Windows failure or prove its cause.
The exact staged SerialTeeServiceTests.cs blob 22ab3d12fa03aa94b673ca6c621f2d8ddca3d3f6
also passed its focused Linux test once (8s), including the post-Stop count 2 check.

Only the existing SerialTee ledger approval changes (count 14 and immutable baseline
b59afbbc4b2282e1aaa5817b8457c2415b8c3647 unchanged), source-only provenance
b4211289e6f94fb5a09f493563bf1fa12e92d0f0 / PR17. On old 1111 head, Rust/MSVC
39268706746 and installer contracts 39268706847 passed; Legacy 39268707217 failed.
All mandatory checks must run again on the final amended head. Hardware/operator/
SDK/real-install gates remain separate; exact CI timeout timing remains unproved.


E21: [проверенные dashboard snapshots](DASHBOARD_E21.ru.md),
[ADR0018](adr/0018-typed-dashboard.ru.md): owned Registers/History через Shell и GPUI,
полная geometry/value/UTC/liveness validation, independent history failure и
current-selection filtering. REST/SPA/proto, appearance и 176+6 mapping прежние.
Software/exact-head Windows evidence фиксируется отдельно; physical/SDK/hardware
acceptance остаётся OPEN. Далее selftest/landiag и stdin CLI отдельными increments.


## D51 / E21 PR18 — stale UIA observation in installer contract fixture

Exact head b4df3f3926aa55d828367373945a128c04fc1636, installer-contract run
39311030990 / job 118260335941: FAIL. The first `ui-saved-options-on` probe passed
at 19:28:02.068; `ui-explicit-zero-is-unchecked` failed at 19:28:02.480 with
`System.Windows.Automation.ElementNotAvailableException` (target UI closed).
Stack: `AutomationElementInformation.get_IsEnabled` → `InstallerUiProbe.Find`
→ `Wait` → `Click` → `Main`; `Test-WindowsMsiUpgrade.ps1:147` rejected probe exit.
This is evidence of an unavailable element during discovery, not a failed option
state/product assertion. The exact wizard transition timing is not established;
previous passing E20 MSI runs do not prove the scheduling cause of this failure.

Class: observational UIA snapshots in `installer/tests/InstallerUiProbe.cs::Find`,
not MSI mutation/packaging policy. Inv: retain client PID scope and enabled/name
filters; catch only `ElementNotAvailableException` around window descendant
query and control enabled/name reads, then skip stale observations. Existing Wait
polling re-enumerates within its original budget; no absent-control or exited-client
success is introduced. N+1: discovery remains in Find; Invoke/Toggle operations
remain outside these catch scopes and continue to fail immediately on exceptions.

The 15s/100ms control wait, 180s operation wait, 240s outer probe deadline,
client-exit failure, expected MSI exit codes, checkbox state assertions and scenario
sequence are unchanged. No mutation retry, MSI relaunch, scenario retry, timeout
increase or broad catch was added. Production installer/payload and original .NET
sources are unchanged; immutable baseline and 14-entry ledger are untouched.

Local verification: exact source comparison confirms only Find changed; its two
new catches are specific to stale-element observations. All code before Find and
from Wait through cleanup is byte-identical to the failed head. Diff whitespace
checks PASS. UIA/PowerShell/MSI runtime execution is NOT_RUN on Linux. Existing real
MSI scenarios must pass again on the amended exact head; the original FAIL remains
recorded. No dashboard Rust code changed, so the existing 193 portable tests remain
prior E21 software evidence, not a substitute for Windows installer acceptance.


## D52 / E21 PR18 — console fixture recompiles interop within every action deadline

Exact head 9fe6fb30b400690ca0f9da4fdd5ffba865ca510a, Rust run 39311807867 /
job 118263127720: FAIL in console_stop (10.65s). Helper PID6264 targeted Host PID3208,
Action=Probe, expected HWND=0. Logs: startup/compiling at 19:43:33.725, interop
compiled/finding window at 19:43:41.144, action posted/checking state at
19:43:41.163; parent reported the 10s helper child deadline at 19:43:42.241.
Probe has no 2s state loop: only Close/Minimize wait for visibility/minimized state.
The 7.419s Add-Type phase plus process startup consumed most of the child budget;
the precise remaining serialization/process-exit scheduling cost is not established.
This does not prove a Host teardown failure, nor a compiler-only cause for the final
second. The older E19 diagnostic-only failure record remains in HOST_E19.
Legacy 39311807737 and installer contracts 39311807909 passed at this same head;
they do not replace the failed Rust gate or a new exact-head run.

Class: disposable window-action helper setup in `apps/host/tests/console_stop.rs`
and `tests/windows/OwnedConsoleProbe.cs`; production Host/native APIs are outside
this change. Inv: compile once BEFORE launching Host, under a separately named
10s compiler-child deadline; each helper action keeps its original 10s child budget,
Close/Minimize keep 2s state/10ms polling, Host teardown remains 5s. N+1: one helper
Main validates the four actions; action dispatch/Win32 behavior lives there, not in
production native code. Unknown/malformed helper invocation fails before effects.

The Rust fixture invokes the existing Windows .NET Framework64 v4.0.30319 csc.exe
(the compiler family already used by MSI contract fixtures), compiling one x64 EXE
in a process-specific directory. Missing compiler/compile failure/timeout aborts the
test before Host startup. Owned compiler/action processes are killed/joined on exit;
helper directory cleanup runs on success/error. No retry or timeout extension was
added. PowerShell startup, repeated Add-Type and ConvertTo-Json are removed from
the operation path; the old PS-only helper is removed after call-site audit.

C# retains the same Win32 signatures, diagnostic window class, null-title search,
PID ownership and expected HWND check, WM_CLOSE/Minimize/WM_CHAR Ctrl+C actions,
state predicates and destroyed-window failure. Stdout has only the same four JSON
facts (integer hwnd/pid, bool visible/minimized); invariant numeric formatting and
fixed keys avoid serializer setup. Diagnostics/errors go to stderr, with nonzero
exit on failure. Original reconnect/three X/Open/minimize/same HWND/listener/pipe/
restart/autostart/Quit assertions and limits remain unchanged. No production source,
.NET baseline/ledger, dependency, workflow or unsafe-policy change is involved.

Local validation: pinned Rust1.99.1 fmt --check and host --tests check/Clippy
locked/offline / -D warnings PASS. Exact C# source compiled without warnings under
SDK10.0.402 / net10.0 with LangVersion=5; no-args and unknown-action cases returned
2, malformed PID returned 1, all with empty stdout under a 10s subprocess bound.
No invalid invocation reached user32. A literal source comparison confirmed that
the original Rust test body is identical after one pre-Host setup line and caller
rename; Win32 signatures/messages and state limits were checked against the old
helper. Diff whitespace checks PASS. These are fixture/source checks, not a new
execution of the dashboard's prior 193 portable tests.

Validation limits: local Linux checks do not compile cfg(windows) Rust or execute
user32/Framework/msiexec; the SDK compiler check does not establish the Framework
compiler/runtime result. Real Windows exact-head execution remains mandatory;
no passing UI/teardown result is inferred from source inspection or prior CI.


### D52 follow-up — Framework compiler source argument

Head 956498ab3d5412d4dc04d7caa519afd073b520af, Rust run 39313953787 /
job 118270460165: FAIL before Host startup. At 19:56:43.008 Framework csc
reported CS1504: source `d:\a\iCemDataMonitor\iCemDataMonitor\rust\apps\host\OwnedConsoleProbe.cs`
could not be opened (missing file). The fixture had passed an absolute source
argument constructed with `../../tests/windows/OwnedConsoleProbe.cs`; the reported
path differs from that intended location. This establishes failed source resolution,
not the exact internals of the older compiler's mixed-slash argument parser.
Installer 39313953824 and Legacy 39313953790 (941/941) passed at the same head.
The Rust failure remains recorded and all checks must run on the amended head.

The compiler now runs with current_dir=`rust/tests/windows` derived through the
manifest directory's two parents and explicit `tests`/`windows` components. Its
source argument is the literal basename `OwnedConsoleProbe.cs`. No canonicalize/
verbatim path prefix is introduced. Output path, compiler/setup/action/state/
teardown limits and every assertion/action remain unchanged. Literal comparison
verified that only source-directory construction and compiler arguments changed;
the C# helper remains byte-identical, SHA256
3a375e1eece42d3f43fe052b3cf1b078173b66035cfcbed9076ad1c24a3ff71e.

Local Linux: invocation-layout/source-identity checks and diff/fmt PASS; host
--tests check/Clippy locked/offline / -D warnings PASS (cfg(windows) excluded).
Direct SDK10.0.402 Roslyn compilation from the actual source directory with the
literal basename passed in C#5 mode under a 10s subprocess limit. The same command
with `AbsentConsoleProbe.cs` failed (CS2001/exit1); no-args/unknown-action/malformed-PID
helper invocations again returned 2/2/1 with empty stdout. These prove the checked
layout, source identity and fail-closed local behavior, not Framework path handling,
Windows Rust compilation or user32 behavior. Actual Framework/Windows exact-head
execution is still mandatory; no production/dependency/ledger/workflow change.


### D52 follow-up — unsupported new fixture-build budget

Head 0743051dcdc6c37154314f4d389a28015c3afd39, Rust run 39315553503 /
job 118275987649: FAIL. console_stop began at 20:10:05.494 and failed at
20:10:15.672: `console fixture compilation before Host startup: child PID9040
deadline exceeded after 10s`. No compiler stderr was reported. Host was never
launched. Unlike 956498ab, this run did not report CS1504; absence of that error
does not establish compiler completion or the cause of its delay. Installer
39315553386 and Legacy 39315553408 (941/941) passed on the same head. The failed
Rust gate remains recorded; there is no blind rerun or claim of console parity.

The 10s setup cap was NEW in D52, copied from the runtime-action cap without build
time evidence. It is now explicitly corrected from 10s to 60s for pre-Host BUILD
preparation only. This is a bounded build policy, not a measured guarantee that
compilation completes in 60s or proof of a compiler-stall cause. Repository practice
already separates compilation from operation: MSI UI fixture csc runs before its
scenarios under the 20-minute installer job; C# oracle compilation runs under the
35-minute Rust job. This fixture has a tighter independent preparation cap within
that job. The original 10s action, 2s state, 5s Host stop, assertions and no-retry
behavior remain unchanged. None of the product behavior is allowed more time.

Preparation logs include start time, compiler path/PID, cwd, source path/size,
output path, inherited stdout/stderr, elapsed time, wait result/exit state and output
bytes. Compilation errors/timeouts remain fatal before Host startup. A successful
compiler exit additionally requires a nonempty regular executable. Owned process
and directory cleanup remain in place. No production/dependency/workflow/ledger
change was added, and the C# helper bytes and runtime action method are unchanged.

Local Rust1.99.1 fmt/host --tests check/Clippy locked/offline / -D warnings PASS;
Linux cargo excludes cfg(windows). A scratch-only harness compiled the exact setup,
wait and RAII code with only Windows creation_flags omitted. Failed compiler exit7,
missing source, exit0 with no output, and exit0 with zero-byte output all aborted
before a subsequent Host-start marker. The zero-byte case recorded output bytes0
and the explicit no-executable failure. Literal source comparison confirmed all
Host test assertions, functional bounds and runtime helper invocation unchanged.
These checks exercise fail-closed preparation; they do not prove Windows Framework
compilation or actual Host behavior. New exact-head Windows evidence must show
compiler duration/result and the full behavior checks passing before merge.

## D53 / E21 — accepted HTTP fixture stream has no explicit blocking mode

Head 7c406efe25a56d5561bc7cce1afc906580deca2a, Rust run 39317752694 /
job 118283533993: web_contract FAILED, 15/16 passed. The malformed HTTP-200
partial-outcome test returned Windows socket error 10035 (WouldBlock). The fixture
sets its listener nonblocking for bounded accept, then performs synchronous reads
on each accepted stream without explicitly restoring blocking mode. A read timeout
does not itself choose blocking mode. The log does not identify the exact failing
socket call; inherited nonblocking mode is the source-level defect addressed here.
Production async WebServer and dashboard client are outside this change.

Class: synchronous accepted streams in the disposable dashboard HTTP fixture in
`crates/icem-web/tests/web_contract.rs`, not Tokio production socket handling.
Inv: explicitly set blocking mode before the existing 3s read timeout; complete a
fragmented request header and return the exact declared HTTP response. The new
real-TCP regression sends the terminating header fragment separately, with bounded
client reads/writes. N+1: every malformed/empty-selection scenario uses the same
fixture; the original partial-result and exact request-path assertions remain.
Accept/read deadlines, 16KiB request cap, response bodies and production contracts
are unchanged. No WouldBlock retry loop or ignored error is introduced.

On the failed old head, console_stop passed, as did the earlier native console,
logger and integration gates. Legacy run39317752797 and MSI run39317752827 passed.
These results preserve D52 evidence but do not replace a complete new exact-head
Rust/Legacy/MSI verdict. Physical UI/hardware and genuine SDK release gates remain
separate. Local Rust binaries must be restored in this resumed environment before
claiming new local cargo results.

Resumed validation: restored exact Rust1.99.1; workspace fmt --check and focused
`cargo test --locked -p icem-web --test web_contract` passed (17/17, including the
new fragmented-header and original malformed-success scenarios). Dependencies were
fetched from the unchanged lockfile. Then `cargo clippy --locked --offline
-p icem-web --all-targets -- -D warnings` and git diff --check passed. These Linux
results do not establish Windows behavior; full exact-head CI remains required.


E22: [встроенный emulator --selftest](EMULATOR_E22.ru.md): полный mapping T1–T26, first-argument dispatch до startup, read-only EXE-relative emulator catalogs, isolated native TestCtl и полный i32 failCount. Windows process/release smoke tests добавлены; local Linux не доказывает Windows runtime. D22/operator/SDK/hardware gates открыты; E23 — explicit --landiag и stdin CLI.


## D54 / CI-2a — canonical INI read/create race

CI-1 head `0283817`, Rust run `39433195261` / job `118664064048`: unchanged
station concurrent-creation test FAIL at 13:56:31.850 with Windows os2/NotFound
config.ini. The old predicate used a later exists probe rather than the captured
read error. A deterministic real NotFound → real winner-publication regression
fails with that predicate and passes when only the original canonical NotFound
permits defaults. Exact original Windows interleaving remains inferred. Parse/access
errors stay fail-fast; no-clobber publication and legacy policy are unchanged.
[Scope, red/green evidence and Windows limits](CI2_CONFIG_RACE.ru.md).


## D55 / CI-2b — synthetic register mirror fixture and CI diagnostics

PR21 head `e8f99925620afa47fe752c217012d96773f672d0`, Legacy run `39436420724`: 940/941
PASS, RegisterMirrorTests.GetRegs_ReturnsZones_And_DecodedFloats FAIL line87, LAN
active=false after about 6 s. A seed using UtcNow before TestServer startup can age
past the 5000 ms gate; original age/startup timeline was not logged, so that causal
explanation remains inferred. Synthetic future-clock headers now explicitly check
clamped age 0, raw flag and count without claiming ordinary fresh-producer coverage.
Original decode assertions, stale LAN negative control and existing exact-threshold
health-monitor matrix remain. Async fixture ownership stops/disposes apps before
reader/file cleanup; phase/liveness output plus attempt-unique TRX/console artifacts
make recurrence reviewable. Production .NET and the immutable manifest are unchanged.
[Scope, four prior repairs, artifact policy and pending Windows proof](CI2_TEST_INFRA.ru.md).
