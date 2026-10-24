# C10 / L14–L15 — удаление dormant v1: план и implementation evidence

## Текущий статус реализации / 2026-10-23

C10 реализован после фактического merge C9: main
`243ae3b27d2eba8eb1f9c42f507e78ea234885a0`, tree
`66c54bda951bfba965ebb1d29c1e34bdb00722ee`. Предкодовый checkpoint
`2ea8ca6cda00fe8c19b4992cbd783d11a032e4aa` включает этот main; запрет source
edits до merge C9 выполнен. PR C10: #39. Ниже сохранён исторический план;
этот раздел фиксирует результаты реализации.

Actual C9 use-site scan подтвердил таблицу: production ConfigureAsync вызывался
только PipelineWorker; AddCorePipeline и LogsCleanup repository callers не имели.
Остальные ConfigureAsync references — два Host fake и synthetic UI endpoint/test.
Удалены ровно семь перечисленных файлов, инертный ConfigureAsync, synthetic
handler/test и unused mocks/imports. Core сохраняет SDK и active configuration;
Data/Transport/Plugins, Hosting/ServiceController и Core.Tests Moq dependencies
удалены после проверки use sites. Новые ArchTests проверяют project/assembly
references и type signatures (base/interfaces/fields/methods/constructors/generic
arguments/constraints), с negative generic-field vector и visited set для циклов.

Strict schema 2 guard проверяет operation modify/delete, точные поля, hashes,
provenance, canonical paths, duplicate JSON keys и frozen bans. Delete проверяет
exact path и lexical parents на file/directory/reparse/symlink/dangling link;
только ItemNotFoundException доказывает отсутствие, I/O/permission ошибки
распространяются. Existing files и approved deletions считаются отдельно.
Router принимает schema 1 только historical base, schema 2 head, и требует
exact regular base blob + deleted source в verified diff + literal head absence.
Неоднозначность, provenance-only и resurrection сохраняют full workload closure.
Allowlist и source routing rules не расширены. Все 65 существующих approvals
мигрированы только добавлением operation=modify; сравнение каждого baseline/
approved hash и provenance с actual C9 подтвердило идентичность. После публикации кода root внёс actual C10 approvals с PR39 и последними remote
source commits: теперь 78 записей, включая семь explicit deletions.
Source-manifest и pin `96e2206562c9389d1543095232115d81eae4b692` не менялись.

Повторный bounded consumer scan: production assembly/plugin loader этих contracts
не найден, Assembly.Load относится к Transport architecture test; в release/
scripts/installer не найден SDK/Core pack/push/nuspec pipeline. Active Host
composition сохраняет acquisition, ILatestBusState, publisher lifecycle и frozen
ops. В документированных composition/extension/release paths поддерживаемый
внешний consumer v1 APIs не выявлен. Публичные assemblies входят в application
payload; произвольный сторонний binary consumer или вручную опубликованный
package универсально исключить нельзя. Это разрешённая product cleanup,
не универсальное утверждение об отсутствии внешних clients.

Независимый review GPT-6.1 Sol medium нашёл и воспроизвёл strict-schema defect:
PowerShell принимал JSON array вместо scalar source_commit/baseline_blob_sha1.
Commit `1ed9959bead80e587ac86fa760f87d5360e0389e` добавил string checks и три
negative vectors; до исправления fixture обнаружил ошибочное принятие массива.
После исправления 48 baseline cases и complete guard прошли. Других регрессий
review не обнаружил; Python routing/cache 42 tests и cache contract повторно прошли.

### Фактическая проверка

SDK `/workspace/scratch/972115899d93/dotnet-sdk/dotnet` сообщает 10.0.302.
Restore UI/Core/Sdk/Host/Transport Tests выполнены последовательно с
`--packages /workspace/scratch/972115899d93/nuget-packages --disable-parallel -m:1 /nodeReuse:false`.
Все dotnet test (включая build) выполнялись последовательно с
`--no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`.

| Проверка | Результат |
|---|---|
| `pwsh -NoProfile -File rust/tools/Test-BaselineGuard.ps1` | PASS, 48 cases, включая returning file/directory/symlink/dangling/parent link и synthetic provider I/O error |
| `python -m unittest discover -s rust/tools/ci -p 'test_*.py'` | PASS, 42 tests: 28 routing + 14 cache |
| `python rust/tools/ci/cache_contract.py --help` | PASS: script проверяет contract независимо от аргумента; invocation/order/owner identity сохранены |
| Core.Tests full | 58 passed, 0 failed |
| Sdk.Tests full | 100 passed, 2 skipped (net48/CLR4 contracts), 0 failed |
| UI ContractTests/B5ContractTests/LayerRuntimeControllerTests/TrayToggleRulesTests | 49 passed, 6 failed, total 55 |
| Host LayerRuntimeStatusTests/LayerCommandTests/HostBootContextTests/ModbusStartupTests | 28 passed, 0 failed |
| Transport TransportArchitectureTests/CachedPublication_/ModbusLifecycleTests | 11 passed, 0 failed |
| Real `Verify-Baseline.ps1 -RequireComplete` | PASS после actual root approvals: 506 existing + 7 approved deletions / 513 entries, 78 exact approvals |
| `git diff --check`; source-manifest diff против actual C9 | PASS; manifest diff пуст |

Selected test filters используют `FullyQualifiedName~` с указанными class/method
fragments через `|`. Наблюдались nullable warnings в unchanged linked AddIn sources
SDK tests и xUnit1031 в unchanged UI HostClientTests.

Шесть UI failures — B5ContractTests.Com0Com_Degraded_WithoutDriver,
Com0ComStatus_ReturnsFullShape, Com0ComRefresh_ReturnsOk,
Com0ComOpenConfig_ValidTool_ReturnsOk, Com0ComOpenConfig_EvilTool_ReturnsBadRequest,
Com0ComOpenConfig_PathTraversal_ReturnsBadRequest. Получен HTTP 501 вместо
ожидаемых OK/BadRequest: unchanged production Com0ComEndpoints явно возвращает
501 при !OperatingSystem.IsWindows(). Отдельное baseline SDK исполнение не
проводилось; baseline-pass/fail не заявляется. Tests не ослаблялись.

Linux не доказывает Windows native IPC/MMF/WinUI/net48/vendor/hardware acceptance.
Обязательны Windows CI.slnf Category!=Live, MSI и native/operator gates. Cargo
не запускался. Rust production, MMF, proto, frozen SPA/assets, historical oracle
fixtures и original dirty worktree не менялись. Complete guard повторно прошёл после actual root approvals. Exact-head Windows CI
остаётся обязательным; его фактический результат фиксируется в PR39.

## Исторический предкодовый план

Основание — L14/L15 и разделы 2–5, 9–12 плана
`b973e50ca5853f3804ace7853183a9f3bc94ec92:docs/LEGACY_REFACTORING_PLAN.ru.md`.
Read-only карта сначала проверялась на C7; C8 consumers — на final tree
`9f1fdda9b0c1ff251cc4ed838f4dae703cb87a97`; pre-code C9 reference — remote
`a98718b555d5fc49729872a34a9d21cf7fb26eac`, PR #38. Первичный восстановленный
checkpoint не менял source/projects/tests/ledger и не запускал SDK. Формулировки
ниже описывают план до реализации; фактические результаты находятся выше.

## Class / Inv / N+1

Class: dormant v1 orchestration, её DI channels, эксклюзивные models/tests и инертный ConfigureAsync: `Core/{Extensions,Models,PipelineWorker.cs,HealthCheckService.cs}`, `Sdk/IModbusPublisher.cs`, `Transport/ModbusTcpPublisher.cs`. Ближайшие nonmembers — активные Core.Config, acquisition, publisher lifecycle/periodic publication и внешние протоколы.

Inv: документированные application entry points сохраняют композицию и поведение; каждое удаление historical file имеет exact baseline identity, explicit deletion approval, provenance и absence proof. Неодобренный missing file продолжает проваливать complete gate; frozen source manifest/proto/assets не переопределяются.

N+1: новая Core configuration feature добавляет собственные behavior/tests без data/transport/plugin dependency и constructor-count metric. Следующее удаление требует отдельной exact-path записи и absence proof, а не exemption каталога.

## Call map и proposed deletion

| Путь | Доказательство и действие |
|---|---|
| HostAppFactory → HostComposition.RegisterCoreServices | Активны BusReader, C6 BusAcquisitionService/ILatestBusState, BusHealthMonitor, singleton IModbusPublisher → ModbusTcpPublisher, optional sniffer/history. Сохранить |
| Host/Program → StartLayersAsync; C2 controller → DefaultLayerActuator | Используют publisher lifecycle/IsBound, а не ConfigureAsync. Сам publisher и интерфейс сохраняются |
| CoreServicesExtensions.AddCorePipeline | Repository call sites не найдены. Только он регистрирует unbounded Channel<TimeSeriesSample>, Channel<HealthEvent>, PipelineWorker и HealthCheckService |
| PipelineWorker → IModbusPublisher.ConfigureAsync | Единственный production call site inert v1 метода; concrete publisher возвращает CompletedTask. Удалить вместе с slice |
| CoreOptions/HealthEvent | Найденные consumers ограничены этим slice и Core.Tests/ContractTests. Активный health monitor — другой BusHealthMonitor |
| Core.Tests/ContractTests | Все пять tests относятся к slice: data flow, configure activation, Mongo health, shutdown, continuous harvest. Удалить с реализацией |
| Host.Tests/LayerRuntimeStatusTests | Fake ConfigureAsync только удовлетворяет интерфейсу; удалить метод, сохранить status behavior |
| C8 Host.Tests/ModbusStartupTests | Fake Publisher.ConfigureAsync также только interface implementation. Удалить метод/неиспользуемый Sdk.Models import, сохранить все startup/composition tests |
| UI.Tests/ContractTests | Synthetic test host сам создаёт /api/config/activate и вызывает ConfigureAsync. Удалить этот handler и ActivateConfigTriggersPublishers, затем unused publisher mock/registration. Остальные tests сохранить |
| HostAppFactory → MapFrozenOpsSurface | Действующая композиция не регистрирует /api/config/activate; synthetic route не доказательство production usage. Frozen ops routes сохраняются |
| Data/LogsCleanup | Только declaration, repository callers не найдены. Не путать с активными TapLog/CrashLog.DeleteOldLogs |

Точный список семи удаляемых файлов (полные project prefixes):

1. `iCemDataMonitor.Core/Extensions/CoreServicesExtensions.cs`
2. `iCemDataMonitor.Core/PipelineWorker.cs`
3. `iCemDataMonitor.Core/HealthCheckService.cs`
4. `iCemDataMonitor.Core/Models/CoreOptions.cs`
5. `iCemDataMonitor.Core/Models/HealthEvent.cs`
6. `iCemDataMonitor.Core.Tests/ContractTests.cs`
7. `iCemDataMonitor.Data/LogsCleanup.cs`

В SDK interface и concrete ModbusTcpPublisher удалить **только ConfigureAsync**, obsolete v1 comments и действительно unused imports. Удалить fake methods выше. В UI.Tests/B5ContractTests есть ещё publisher mock: проверять конкретные uses после rebase, удалять только при их отсутствии. В Sdk/IConfigStore поправить stale PipelineWorker comment, сохранить contract.

## Dependencies и architecture rules

Core проект остаётся: Config/IniConfig, LayerFlags, UiThemePreferenceStore активно используются и зависят от BCL/SDK.Bus. После удаления concrete use sites убрать из Core.csproj ProjectReference на Data/Transport/Plugins.Icem и PackageReference на Microsoft.Extensions.Hosting/System.ServiceProcess.ServiceController. SDK reference и config tests остаются. Moq в Core.Tests использовался только удаляемым ContractTests; package удалить после проверки actual C9 base. Новые assemblies/packages не нужны.

Не расширять deletion на IDataSource, ITimeseriesStore, IConfigStore, ISourceReader, TimeSeriesSample, ModbusRegisterEntry: retained Data/Transport/endpoints/tests имеют ссылки. Data → Plugins остаётся concretely нужен InMemoryConfigStore; Data → Contracts — MetricsSampler. Зависимости не чистятся по эстетике.

Core.Tests/ArchTests переключается на surviving anchor typeof(IniConfig).Assembly. Убираются >=6 constructor arguments, domain-keyword heuristic и vacuous startup Assert.True(true), относящиеся к удаляемым типам. Вместо них:

* ProjectReference guard запрещает Core → Data/Transport/Plugins/Host/Host.Core/UI; SDK допускается.
* Assembly-reference guard запрещает concrete data/transport/plugin/database/UI dependencies.
* Type/namespace dependency scan учитывает base/interfaces, fields, method return/parameter signatures и generic arguments; constructor-only scan не ловит Generic<ForbiddenType>.
* Малый negative vector доказывает обнаружение forbidden dependency, включая generic argument. Не вводить LOC/count/style constraints.

IniConfigTests/UiThemePreferenceStoreTests сохраняют behavior guards. Активная orchestration проверяется Host/C8 composition/status/lifecycle tests, а не восстановлением legacy pipeline tests.

## Public/external consumer proof: bounded conclusion

Проверены README, docs/architecture.md, development.md, release.md, install.md; SDK/Core csproj и Directory.Build.props; release/legacy-build workflows; scripts/make-release.ps1; installer/build-msi.ps1 и gen-payload.ps1; production source на assembly/plugin discovery.

Документированная поставка — application MSI. Release workflow строит MSI и публикует MSI/checksums. Installer публикует Host/Shell/Emulator, при наличии vendor SDK строит net48 AddIn, harvest собирает application payload. Не найдены .nuspec, dotnet pack, nuget push, GeneratePackageOnBuild, PackageId или отдельный SDK publication pipeline. SDK/Core имеют default SDK packability: отсутствие pipeline не означает невозможность самостоятельной упаковки.

Production Assembly.Load/LoadFrom/LoadFile, AssemblyLoadContext, exported-type scan или DLL plugin directory discovery для этих contracts не найдены. Plugins.Icem статически referenced. Assembly.Load hit относится к Transport architecture test; Activator.CreateInstance в Rust test oracle не является production loader. Vendor AddIn — отдельная net48 boundary без Core pipeline/SDK publisher configuration consumer. README содержит некоторые исторические self-contained/release инструкции; фактический publish проверен по scripts и более новым install/release docs.

Вывод: **в документированных composition/extension/release paths репозитория не выявлен поддерживаемый внешний consumer этих v1 APIs**. Сборки публичны и входят в application payload; произвольный сторонний binary consumer или вручную опубликованный ранее package универсально исключить нельзя. Это bounded product cleanup в разрешённой пользователем области, а не доказательство отсутствия любых внешних clients. Оговорка должна остаться в implementation evidence/PR. Активные wire/REST/gRPC/CLI/plugin contracts не меняются.

## Baseline schema 2 и строгая проверка удаления

Текущая schema1 требует approved_blob_sha1 из 40 lowercase hex, отличный от baseline; missing file всегда incomplete. **Она не выражает approved deletion.** Нельзя удалить manifest entries, изменить historical hash или выключить RequireComplete. Manifest с pin `96e2206562c9389d1543095232115d81eae4b692` неизменен. Текущее количество approvals и их identities сверяются на actual C9 base; старые hashes/provenance сохраняются.

Минимальный формат: schema_version=2, каждый entry имеет explicit operation modify|delete. Modify сохраняет approved_blob_sha1; delete его **не содержит**, но сохраняет exact baseline_blob_sha1 и provenance {pull_request, commit, reason}. Unknown operation/fields, mixed forms, duplicates, noncanonical paths и wrong baseline hash отвергаются. Existing entries мигрируют в modify без изменения hashes/provenance. Router может принимать historical schema1 только как modification-only base; current baseline guard требует schema2. Неоднозначная миграция безопасно ведёт к full-workload fallback.

Verify-Baseline.ps1 сначала валидирует entry/frozen bans, затем для delete проверяет отсутствие exact path до обычной missing-file ветки. Returning file, directory, symlink/reparse entry, dangling link — failure. Проверить path parents на symlink/reparse traversal: отсутствующий descendant через redirected parent не является доказанной absence. Не вводить path traversal, directory exemptions или blanket allowlist. Unapproved missing baseline files по-прежнему fail RequireComplete. Вывод отдельно считает verified existing files и approved deletions, не выдаёт удалённые за существующие 513/513.

Frozen proto/assets/SPA override bans, immutable baseline identity, canonical Git/CRLF modification hashing, D16/D17 и REST inventory сохраняются. Provenance указывает actual PR, последний remote source-code commit и конкретную причину. Удаление не оформляется hash пустого файла. Никаких historical manifest edits.

## Router/cache/schema consumers

Прямые consumers: rust/tools/Verify-Baseline.ps1, Test-BaselineGuard.ps1, ci/routing.py (approvals/delegated_approvals), ci/test_routing.py. Router сейчас требует strict schema1 и regular blobs в обеих ревизиях; deletion branch должна быть узкой:

1. Manifest остаётся одним неизменённым regular Git blob; strict UTF-8 JSON/duplicate-key/canonical-path/provenance validation и metadata size bounds сохраняются.
2. New delete: regular base blob имеет exact previous approved hash либо immutable baseline hash для нового approval; exact deleted source path входит в verified diff.
3. Head exact path действительно отсутствует по literal ls-tree. Tree/symlink/submodule/ambiguous result не равны absence. Missing в обеих ревизиях не доказывает deletion.
4. Unchanged existing delete допустим. Provenance-only edit, ledger-only deletion и delete→modify resurrection не получают metadata delegation. Modify сохраняет прежний exact base/head proof.
5. Любая ambiguity оставляет ledger в classification и включает полный workload closure. Exemption касается только proven ledger metadata path; source deletion paths продолжают обычные JSON routing rules. **Никакого eligibility allowlist или path exemption ради skip.**

Verify-ScaffoldInputs.ps1 продолжает вызывать baseline suite и complete guard. ci/cache_identity.py не парсит ledger: COMMON_SCAFFOLD_INPUTS уже содержит оба baseline scripts, изменения автоматически меняют cache compatibility digest. ci/cache_contract.py фиксирует порядок guard invocations и coverage command owners. Новый cache path/workflow bypass не нужен. Если нужен новый helper, он тоже должен входить в identity/contract; предпочтительно ограничиться существующими owners.

В rust/docs/CI_CD.ru.md обновить schema/base-head deletion proof: сейчас текст требует regular blobs с обеих сторон. Historical counts в SOURCE_AUDIT не переписываются; C10 evidence дополняется фактическими данными. Rust production не меняется; необходимая baseline-guard adaptation следует явно разрешённому legacy refactor и сохраняет frozen policy.

## Regression vectors и acceptance после реализации

| Набор | Обязательные vectors |
|---|---|
| Baseline guard suite | Approved delete проходит RequireComplete; unapproved missing fails; wrong baseline/provenance/frozen path; unknown schema/op/fields; delete+hash; returning file/directory/symlink/dangling link/parent link; сохраняются modification/CRLF/proto/icons/SPA/D16/D17/REST cases |
| CI routing | Baseline delete; previously modified approved file deletion; mixed modify/delete; wrong/missing base; head file/tree/symlink/submodule; absent both revisions; source/ledger без counterpart diff; changed deletion provenance; resurrection; immutable manifest drift; schema1 base/schema2 head; malformed metadata и existing CRLF proof. Ambiguity всегда full fallback |
| Cache/guards | Python routing/cache suites и cache contract; invocation/order и owner identity coverage; real Verify-Baseline -RequireComplete; manifest unchanged |
| Portable .NET | Полные Core.Tests/Sdk.Tests; UI ContractTests/B5ContractTests/LayerRuntimeControllerTests/TrayToggleRulesTests; Host LayerRuntimeStatusTests/LayerCommandTests/HostBootContextTests/ModbusStartupTests; TransportArchitectureTests/CachedPublication_/ModbusLifecycleTests |
| Windows | CI.slnf build/test Category!=Live и MSI; C8 native HostCommandListeners cases, full Modbus/MMF/Host regression, WinUI build; vendor AddIn/live operator boundaries отдельно при доступном окружении |

Restore/build/test только после C9 merge и реализации, последовательно, в отдельном worktree: SDK 10.0.302, общий NuGet cache, restore --disable-parallel -m:1 /nodeReuse:false; build/test --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false. Фактические commands/results/counts/failures записать после исполнения; этот plan checkpoint проверок C10 не заявляет. Linux не доказывает Windows native IPC/MMF/WinUI/vendor/hardware acceptance.

## Сохраняемые активные контракты

C8 concrete generation owner, start/stop serialization, accept/refresh/client joins, completed-client removal и same-instance rebind сохраняются. Typed ModbusBindException сохраняет socket cause/фактический port. Boot retry — пять attempts, четыре cancellable delays по пять секунд только AddressAlreadyInUse; runtime actuator остаётся одной попыткой на том же DI singleton. HostCommandListeners владеет обоими workers/shared handles: cancel/join до disposal. Fake ConfigureAsync removal не ослабляет эти tests.

MMF layout5, time4–5 int32/CDAB, header/reserve49–56, независимый LAN/RAW; Modbus overwrite каждый tick включая same publication, resize/header/stale gates; C2 mutation serialization/status/toggle/Web persist-only/OpcDa no-op; C6 single acquisition и join до Reader.Dispose; C7 owned logging; C9 codecs/engine/AddIn seams сохраняются. Proto/SPA/assets, production Rust и historical oracle fixtures не меняются. Перед source edits после rebase повторить deletion/call/dependency proof на actual merged C9 tree.
