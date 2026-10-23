# C8 / L10–L11 — lifecycle Modbus и владельцы Host IPC

## Статус и исходные данные

План до существенного кода, по L10/L11 документа `b973e50ca5853f3804ace7853183a9f3bc94ec92:docs/LEGACY_REFACTORING_PLAN.ru.md`. Проверен исходный tree C7 `eb45dc58342e8f22cbd37ef3ee6a25dd0a141bfe`. Первый checkpoint содержал только документ, без production/tests, restore/build/tests. После green merge C7 план перенесён на фактический main `8dd9dccf207b6d67be79dc0e4fa26f92a7c11223`; реализация отдельно разрешена. Исходные решения ниже сохранены как план до кода, фактическая реализация и проверки описаны в конце.

## Class / Inv / N+1

Class: один конкретный `ModbusTcpPublisher` и принадлежащие одному его запуску listener, CTS, accept/refresh/client tasks; отдельно — пара Host named-event listeners в `Host/Program.cs`. Boot retry относится к `Host.Core/HostComposition.cs`; универсальный lifecycle framework не создаётся.

Inv: lifecycle операции одного publisher сериализованы; повторный start работающего экземпляра идемпотентен; завершившийся stop отменил и дождался всех workers предыдущего запуска, освободил его ресурсы и разрешает rebind. IPC owner отменяет и ждёт ОБА workers до disposal shared stop/event handles и регистрации. Boot/runtime используют один DI singleton; graceful close и намеренно blunt restart сохраняют свои разные контракты.

N+1: новый Modbus client принадлежит текущему запуску и автоматически включается в его join; новый существующего класса IPC listener добавляется под того же конкретного owner и проверяется на join/disposal. Новые протоколы, services и shutdown policies не встраиваются в общий механизм по сходству формы.

Ближайшие nonmembers: Shell `App` с собственными wake/shutdown workers, emulator ACK/drain/activation, malformed FC06 length=5/index=5, устаревшие комментарии о restart. Они фиксируются как границы, не исправляются C8. C9 emulator/AddIn и C10 v1 deletion/architecture tests выполняются отдельно.

## Фактическая карта вызовов и владения

| Путь | Текущее поведение и целевой seam |
|---|---|
| `HostAppFactory.Build` → `HostComposition.RegisterCoreServices` | `IModbusPublisher` — singleton `ModbusTcpPublisher`; получает C6 `ILatestBusState`, reader не создаёт и им не владеет |
| Единственный boot caller: `Host/Program.cs` → `HostComposition.StartLayersAsync` | Сначала запускает sniffer, затем включённый Modbus. Сейчас 5 попыток и 5 секунд между попытками, `Thread.Sleep`, эвристика текста `address`/`port`/`already in use`/`EADDRINUSE` |
| `RuntimeTelemetryServiceImpl.SetLayer` → C2 controller → `DefaultLayerActuator` | Сериализованная runtime mutation; один вызов publisher start/stop, при failure прежний opposite actuator rollback. Cancellation controller — admission-only |
| `ModbusTcpPublisher.StartAsync` | Сейчас bind синхронно выполняется до запуска loops: отсутствие await у boot caller само по себе не является текущим дефектом. Повторный start перезаписывает CTS/listener; последующая bind failure теряет ссылку на прежний listener |
| `StopAsync` / `Dispose` | Stop ждёт accept/refresh, но не discarded per-client `HandleClientAsync` tasks и не освобождает/сбрасывает CTS/tasks. Dispose отменяет и освобождает без join |
| `Program.StartTray` после layers/READY | Создаёт restart/shutdown auto-reset named events, сбрасывает stale signals; отбрасывает два task handles и cancellation registration. Shutdown worker сам disposes shared `ManualResetEventSlim`, которым ещё пользуется второй worker |
| tray menu и named events → `HostLifecycle` | Оба dispatch используют те же `RequestRestart`/`RequestClose`. Close сначала сигналит Shell shutdown, затем `StopApplication`; restart spawn → tray dispose → `Environment.Exit(0)` без graceful drain |

## Минимальный дизайн до реализации

1. Внутри concrete publisher сериализовать start/stop/dispose и явно владеть ресурсами запуска. Listener/CTS сначала локальны до успешного bind; failure освобождает только создаваемый запуск. После bind accept/refresh/client workers используют стабильные ресурсы своего запуска. Stop отменяет, прерывает accept/client I/O, ждёт accept/refresh и все зарегистрированные client tasks, затем освобождает и сбрасывает ресурсы. Приём клиентов должен завершиться до окончательного снимка join, чтобы не потерять последний accepted client. Dispose использует тот же завершённый teardown; повторные stop/dispose безопасны, start после dispose отклоняется. Нельзя удерживать register lock через await/socket I/O.
2. Сохранить `IPAddress.Any`, register buffer между stop/start, чтение options.Port на каждом новом start; Total и tick interval остаются construction-time настройками. Повторный start работающего экземпляра не создаёт второй bind/loops. Не вводить runtime INI reconfiguration.
3. Выделить типизированную bind failure, оборачивающую фактическую `SocketException` на bind и сохраняющую port/error/inner exception. Host retry — async 5 попыток, 5 секунд между retry через cancellable `Task.Delay`; retry только `SocketError.AddressAlreadyInUse`, никакого поиска слов в exception message. Boot передаёт `ApplicationStopping` token; awaits start и delay. Внутренний управляемый delay seam допустим для тестов policy без 20 секунд ожидания. Runtime actuator остаётся одношаговым и пользуется тем же singleton publisher, не boot retry.
4. Конкретный Host IPC owner удерживает оба event handles, shared stop, оба workers и cancellation registration. Start/partial-start failure имеют cleanup; graceful disposal сначала signal/cancel, затем join ОБОИХ, затем unregister и dispose handles. Worker не освобождает shared resource. Создание/Reset имён и dispatch verbs сохраняются; тестовый seam позволяет реальные уникальные Windows имена.
5. Program удерживает owner до finally. Tray icon disposes первым по существующему ghost-icon контракту; IPC owner join завершается до disposal DI host resources. HostLifetime shutdown остаётся источником отмены. `RequestRestart` намеренно НЕ превращается в graceful stop: при реальном `Environment.Exit` handles освобождает ОС; unit test с fake exit не доказывает процессный teardown.

## Намеренные изменения и замороженные контракты

Намеренные дельты: идемпотентный repeated start вместо потери ресурсов; stop/dispose ждут client workers; отменяемое ожидание boot bind retry; retry только реального address-in-use вместо широкой message heuristic; ошибки другого типа не повторяются. Если stop и command event сигналят одновременно, stop имеет приоритет: уже начавшийся shutdown не dispatch-ит новую команду. Диагностика сохраняет фактический port и исходную причину; нельзя утверждать пять выполненных попыток после раннего non-retryable failure. Поведение cancellation не маскируется как bind failure.

Заморожены C2 requested/persisted/observed status, toggle, Web persist-only, OpcDa noop и rollback; C6 single acquisition с периодом 200 ms и expiry 200 + 500 = 700 ms, reader join перед Dispose; MMF layout v5, int32/CDAB time regs 4–5, header/reserve 49–56, независимый LAN/RAW; Modbus PER-TICK overwrite даже при той же publication, resize/header/stale gates и wire/PDU semantics. C7 owned log timestamps/common core сохраняются. CLI, single instance, headless/tray, console-X guard, proto/SPA/assets и Rust production не меняются. Новых assemblies/packages, lifecycle/retry framework и UI/config возможностей нет.

Historical manifest остаётся pinned на `96e2206562c9389d1543095232115d81eae4b692`; provenance/ledger сейчас не меняются. Позднее root фиксирует только фактические remote code hashes/PR и обоснованные дельты; старые oracle fixtures сохраняются.

## План проверок после разрешения реализации

| Граница | Проверяемое поведение |
|---|---|
| Transport `ModbusLifecycleTests` | Portable loopback: stop до start, repeated/concurrent start/stop/dispose, bind failure cleanup и retry того же экземпляра, restart/rebind, clients в blocked header/body read, сохранение register buffer и отсутствие workers прошлого запуска. Handshakes/dedicated workers и bounded cleanup вместо короткого timing SLA |
| Host boot retry | Управляемый delay: ровно 5 попыток/4 задержки по 5 секунд при busy; success останавливает retry; cancellation во время busy delay; permission/argument/non-bind ошибки не повторяются; исходная socket cause/port сохраняются; настоящий async start awaited |
| Composition/runtime | Boot и runtime actuator получают один DI instance; runtime не наследует boot retry; C2 rollback/status contracts сохраняются |
| Windows native IPC owner | Реальные уникальные named events, оба blocked workers отменяются и завершаются до shared disposal; повторные lifetimes/dispose, partial-start failure cleanup, reset stale signals, restart/close dispatch verbs. Tests не заявляют гарантию drain после blunt process exit |
| Host regression | `LayerCommandTests`, `LayerRuntimeStatusTests`, `HostLifecycleTests`, `CliForwarderTests`, `SingleInstanceGuardTests`, `TrayLogicTests`, `CompositionSmokeTests` |
| Transport/Data regression | `ModbusContractTests`, `TransportArchitectureTests`, `AcquisitionMeasurementsTests`; общий фильтр `Category!=Live` |

Restore/build/test только последовательно, отдельный unrestored worktree без shared obj: SDK `dotnet-sdk/dotnet` (версия 10.0.302), NuGet `nuget-packages`; restore `--packages <path> --disable-parallel -m:1 /nodeReuse:false`; build/test `--no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`. Точные результаты записываются после исполнения. Windows CI должен подтвердить native named events/MMF/WinUI; Linux loopback и unit tests не доказывают Windows/vendor/hardware acceptance. Original dirty C3 worktree не трогается.


## Реализованный checkpoint

`ModbusTcpPublisher` владеет конкретным generation с неизменными listener/CTS и accept/refresh tasks. Semaphore сериализует start/stop/dispose; повторный start активного generation не делает новый bind. Client task регистрируется до completion-removal continuation и удаляется сразу после завершения, поэтому history всех соединений не накапливается. Stop отменяет I/O, делает IsBound=false при закрытии listener, ждёт accept, затем refresh и финальный набор client tasks; только после этого освобождает generation. Dispose использует тот же join. Register image живёт вне generation и сохраняется при restart. Port захватывается на каждый новый start и используется также в typed bind diagnostics.

`HostComposition.StartModbusWithPortRetryAsync` awaits каждый start и cancellable delay, имеет ровно пять попыток / четыре задержки при постоянном AddressAlreadyInUse. Иные причины завершаются после фактического числа попыток; `ModbusBindException` сохраняет SocketException, код и фактический port. Program передаёт ApplicationStopping. Runtime actuator и C2 controller не менялись.

`HostCommandListeners` удерживает два LongRunning workers, оба named events, shared stop и регистрацию отмены. Partial-start cleanup и обычный Dispose ждут workers до освобождения handles; callback failure также освобождает все handles после join. Program хранит owner, очищает tray первым, затем listeners до host disposal; ошибка старта tray также освобождает созданный tray. Настоящий `HostLifecycle.RequestRestart` остаётся spawn → icon dispose → Environment.Exit(0); его реализация не менялась.

## Локальная проверка реализации

Linux, SDK 10.0.302, отдельные последовательные restores Transport.Tests и Host.Tests с указанными выше flags прошли. Обе production graphs и test assemblies собраны. Portable проверки выполнялись с `Category!=Live`:

* Transport: `ModbusLifecycleTests|TransportArchitectureTests|CachedPublication_` — 11 passed. Это 6 lifecycle tests, 3 architecture guards и 2 pure cached-publication vectors. Lifecycle включает dedicated concurrent start/stop/dispose callers, blocked-refresh handshake с queued restart и IsBound=false во время join, accepted clients без полного request, busy-bind cleanup, cancellation admission, same-instance rebind, сохранение reserve register и удаление завершённых client tasks. TCP shutdown допускает EOF либо connection reset/IO close, но timeout остаётся ошибкой.
* Host: `ModbusStartupTests|HostCommandListenersTests|LayerCommandTests|LayerRuntimeStatusTests|HostLifecycleTests|CliForwarderTests|SingleInstanceGuardTests` — 66 passed / 5 skipped. Девять startup cases включают exact attempt/delay/cancel policy, awaited async start, отсутствие message heuristic, actual-port diagnostics и реальный production DI registration: boot и runtime actuator меняют один loopback publisher с заменой только latest-state source.
* Пять skipped cases требуют Windows native named events: повторные lifetimes и dispatch обоих verbs, join обоих заблокированных callbacks до dispose, partial-start cleanup при конфликте имени с Mutex, reset stale signals / stop priority, cleanup после faulted callback.

Дополнительно первоначально запущенный Transport regression filter с полным `ModbusContractTests` дал 30 passed / 20 failed. Все 20 failures относятся к MMF-derived integration scenarios: actual LastError содержит `Bus open failed: the station bus mapping requires ...`; `BusMapping.OpenReadOnly` явно бросает PlatformNotSupportedException вне Windows. Первоначальный более широкий Host filter дал 118 passed / 5 skipped / 9 failed: 4 CompositionSmoke cases требуют Windows named-pipe transport, 2 TrayLogic cases вызывают kernel32, ещё 3 зависят от Windows exe/path layout. Эти результаты не объявляются baseline comparison и не скрываются; production MMF, named-pipe и path semantics не изменялись ради Linux тестов.

После последних малых правок оба portable набора повторно прошли на code checkpoint `a9eabe7a98e7917bf51d3f60aed092b4e4a2a44c`: Transport 11 passed; Host 66 passed / 5 Windows-only skipped. `git diff --check` проходит. Windows CI должен исполнить полный Modbus/Host regression, пять native-event cases и сборку WinUI. Linux не доказывает native IPC/MMF, proprietary AddIn loading, live hardware или process-exit cleanup. Source manifest, ledger и исходный dirty C3 worktree не изменены.
