# C4: L06 / L07 — registry snapshot и каталоги

## До существенных изменений

Class: DeviceCatalogRegistry и family catalogs — проекции метаданных. Они не являются владельцами wire/MMF форматов. UnifiedVariableRegistry уже хранит immutable записи и frozen dictionaries; его internals и строгий Load не требуют изменений.

Inv: один owned snapshot на lifetime DeviceCatalogRegistry; все четыре API согласованы с ним. Каталоги кешируются, опубликованные коллекции read-only; Lookup согласован с коллекцией. Отсутствие snapshot также фиксируется на lifetime. Проверки должны различать injected registry и deployment registry.

N+1: новая family задаётся локальным описанием/policy (offset, fallback, palette) и использует общий projector/один optional loader. Не нужен новый framework или assembly.

Baseline до edits (передан владельцем задачи): Plugin.Tests 147/147; общий portable baseline 513/513; 27 известных divergences. Независимое review и provenance pending.

## L06 — единый snapshot

DeviceCatalogRegistry при construction один раз получает immutable UnifiedVariableRegistry и создаёт три catalog instances и общий unknown fallback. GetCatalog/GetModbusBase/GetFamilies/GetRegisterEntries используют этот snapshot. Public optional/null constructors сохраняют default-load поведение; internal FromSnapshot передаёт уже установленное отсутствие без повторного I/O. OptionalRegistryLoader общий, строгий UnifiedVariableRegistry.Load не изменён. Read-only wrappers защищают cached catalog, family names и register map от cast/mutation; Lookup остаётся согласован.

Intentional delta: missing/unreadable/malformed/unsupported optional deployment registry теперь даёт согласованный degraded режим: static family catalogs, metadata families/register map empty, base 2. Раньше Modbus getters могли бросать при malformed, повторяли loading при missing, и injected registry не управлял Modbus APIs. Отсутствие или успешная загрузка фиксируется на lifetime: изменение/удаление файла не влияет; recovery требует нового instance. Никакого hot reload.

L06 validation: Linux .NET 10, Plugin.Tests **155/155** (`dotnet test iCemDataMonitor.Plugins.Icem.Tests --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`). Новые проверки: injected registry отличается от deployment (names, aliases, sparse indexes, bases, extra family), четыре API, mixed case, cache identity, mutation rejection и Lookup consistency, all healthy entries всех трёх families, empty successful family, file mutation/deletion, deterministic optional faults + one-attempt lifetime/recovery.

Scope: Data adapters пользуются IDeviceCatalogRegistry; production DI wiring здесь не добавляется. Dormant Host.Core MapRegistryEndpoints / MapModbusPreviewEndpoints вне C4; активный MapFrozenOpsSurface не меняется. Это не обещание snapshot поведения всего Host REST. MMF layout 5/time 4–5/reserve 49–56, frozen proto/SPA, AddIn, Rust, deps и CI не меняются. Windows COM/AddIn/MMF runtime проверки на Linux недоступны. Independent review / provenance pending до фактического review и PR.
