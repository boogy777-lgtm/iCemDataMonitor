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
