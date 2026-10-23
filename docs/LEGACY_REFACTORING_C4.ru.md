# C4: L06 / L07 — registry snapshot и каталоги

## До существенных изменений

Class: DeviceCatalogRegistry и family catalogs — проекции метаданных. Они не являются владельцами wire/MMF форматов. UnifiedVariableRegistry уже хранит immutable записи и frozen dictionaries; его internals и строгий Load не требуют изменений.

Inv: один owned snapshot на lifetime DeviceCatalogRegistry; все четыре API согласованы с ним. Каталоги кешируются, опубликованные коллекции read-only; Lookup согласован с коллекцией. Отсутствие snapshot также фиксируется на lifetime. Проверки должны различать injected registry и deployment registry.

N+1: новая family задаётся локальным описанием/policy (offset, fallback, palette) и использует общий projector/один optional loader. Не нужен новый framework или assembly.

Baseline до edits (передан владельцем задачи): Plugin.Tests 147/147; общий portable baseline 513/513; 27 известных divergences. Итог независимого review и provenance приведён в конце документа.

## L06 — единый snapshot

DeviceCatalogRegistry при construction один раз получает immutable UnifiedVariableRegistry и создаёт три catalog instances и общий unknown fallback. GetCatalog/GetModbusBase/GetFamilies/GetRegisterEntries используют этот snapshot. Public optional/null constructors сохраняют default-load поведение; internal FromSnapshot передаёт уже установленное отсутствие без повторного I/O. OptionalRegistryLoader общий, строгий UnifiedVariableRegistry.Load не изменён. Read-only wrappers защищают cached catalog, family names и register map от cast/mutation; Lookup остаётся согласован.

Intentional delta: missing/unreadable/malformed/unsupported optional deployment registry теперь даёт согласованный degraded режим: static family catalogs, metadata families/register map empty, base 2. Раньше Modbus getters могли бросать при malformed, повторяли loading при missing, и injected registry не управлял Modbus APIs. Отсутствие или успешная загрузка фиксируется на lifetime: изменение/удаление файла не влияет; recovery требует нового instance. Никакого hot reload.

L06 validation: Linux .NET 10, Plugin.Tests **156/156** (`dotnet test iCemDataMonitor.Plugins.Icem.Tests --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`). Новые проверки: injected registry отличается от deployment (names, aliases, sparse indexes, bases, extra family), четыре API, mixed case, cache identity, mutation rejection и Lookup consistency, all healthy entries всех трёх families, empty successful family, file mutation/deletion, deterministic optional faults + one-attempt lifetime/recovery. После review найден и сохранён контракт variable-only family: GetFamilies его не объявляет, GetRegisterEntries возвращает записи с default base 2; regression включён в 156.

Scope: Data adapters пользуются IDeviceCatalogRegistry; production DI wiring здесь не добавляется. Dormant Host.Core MapRegistryEndpoints / MapModbusPreviewEndpoints вне C4; активный MapFrozenOpsSurface не меняется. Это не обещание snapshot поведения всего Host REST. MMF layout 5/time 4–5/reserve 49–56, frozen proto/SPA, AddIn, Rust, deps и CI не меняются. Windows COM/AddIn/MMF runtime проверки на Linux недоступны. Итог review и provenance приведён ниже.


## L07 — общий projector через composition

Internal CatalogProjection выполняет registry projection, owned read-only catalog, canonical-name OrdinalIgnoreCase lookup, palette cycling и unit guessing. FlecsVariableCatalog / PlcVariableCatalog / LanVariableCatalog остаются public thin wrappers: у каждого локальны family, offset, fallback vectors и palette. Никаких новых deps/assemblies/base framework. Registry order по Index, Display=Alias, offsets Flecs Index+1 / PLC Index+100 / LAN Index+400 сохранены; alias ключ не добавляется. Catalog включает ExcludeFromTelemetry, map исключает. LAN fallback сохраняет все 8 entries в прежнем несортированном порядке; успешный пустой registry не включает static fallback.

N+1 проверен synthetic `extra` family непосредственно через общий projector с локальным offset 17, fallback и palette. Test также проверяет ownership входных mutable policy arrays. Exact fallback vectors (все поля и порядок) и полные 10/10/20 palettes с wrap зафиксированы отдельными regression tests.

L07 portable validation: Plugin.Tests **161/161**; targeted Data `CatalogSnapshotIntegrationTests` **1/1**. Команда Plugin та же, что для L06. Data: `dotnet test iCemDataMonitor.Data.Tests --no-restore --no-build -m:1 /nodeReuse:false -p:UseSharedCompilation=false --filter FullyQualifiedName~CatalogSnapshotIntegrationTests`. Integration проверяет synthetic injected snapshot после удаления source file: FamilyAwareCatalog переключает flecs → plc, InMemoryConfigStore combined map включает extra family и исключает ExcludeFromTelemetry, без deployment values.

Дополнительный полный Data.Tests запуск: build successful (0 warnings), **56 passed / 60 failed / 116 total** на Linux. Failures относятся к Windows named maps/sections и зависимым Bus assertions; полный suite здесь не является зелёной portable проверкой. Эти Windows runtime проверки не исправлялись и не входят в заявленный C4 validation. Итог review и provenance приведён ниже.

## Независимое review — malformed optional data

До fix: Class — OptionalRegistryLoader является boundary необязательного deployment file; strict Load не меняется. Inv — malformed данные, включая JSON null в обязательных DTO nodes, фиксируют degraded snapshot на lifetime. N+1 — существующий общий loader обслуживает следующие wrappers без нового механизма или dependency.

Review обнаружило regression: синтаксически корректный JSON с null family definition, variable, canonical name или family entry вызывает NullReferenceException внутри strict Load; новый optional exception filter пропускал его, хотя прежние wrappers использовали fallback. Четыре real-JSON regression cases воспроизвели сбой до fix. Optional boundary теперь обрабатывает и этот failure; строгий Load продолжает бросать прежнее исключение.

Уточнение кеша: каталоги и maps объявленных families кешируются. Variable-only family сохраняет legacy GetRegisterEntries с default base 2 и read-only результатом, но его map проецируется по запросу. Произвольные unknown family strings не добавляются в unbounded cache; это не вызывает повторного disk I/O или пересборки catalog.

Второй review fix: Class — CatalogProjection владеет lookup projection; Inv — принятые strict parser entries сохраняют legacy last-write-wins lookup, включая несколько пустых canonical names. N+1 — общий projector применяет ту же политику без дополнительного family-specific кода. Regression с двумя empty-name entries воспроизвёл ArgumentException нового ToFrozenDictionary selector overload; lookup теперь сначала заполняет локальный dictionary присваиванием, затем замораживается. Catalog order и entries не меняются.

Независимое review diff base..L07, registry contracts, Data adapters и tests завершено; других material findings не выявлено. После review fixes: Plugin.Tests **166/166**; Data `CatalogSnapshotIntegrationTests` **1/1** (с rebuild, те же no-restore/single-node flags); `git diff --check` clean. Windows runtime validation остаётся за Windows CI; provenance/ledger завершает владелец задачи.


## Публикация и итог проверки

Три GPT-6.1 Sol / medium агента последовательно выполнили анализ, реализацию
и независимое ревью. После двух review fixes: Plugin.Tests **166/166**,
целевой Data integration **1/1**, build без warnings, diff-check clean.
Windows CI и ручная операторская/аппаратная приёмка учитываются отдельно.

[PR №33](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/33) начинается
от main `690659368399436fcd91e5b82e94138d761367b1` после C3. Раздельные code commits:
L06 `71caa5d1ec0cfb77011890cfa406ccdc9bcb03a0`,
L07 `15b5a33860e01f2b6ae497492c2f279516256d25`,
review fixes `5921231b1004a44bced36c3eaef9d733605eb52a`.
Деревья опубликованных commits совпадают с локальными проверенными деревьями.

Exact-path ledger `rust/migration/legacy-divergences.json` содержит четыре
новые записи существующих Plugins.Icem source paths, реальные code commit SHAs
и PR №33. Исторический source-manifest остаётся закреплён на
`96e2206562c9389d1543095232115d81eae4b692` и не изменён.
`Verify-Baseline.ps1 -RequireComplete`: **513/513**, **31** exact approved
divergences; frozen proto/assets/SPA и REST inventory сохранены.
Windows build/tests/MSI результат для точного head — в
[проверках PR](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/33/checks).
