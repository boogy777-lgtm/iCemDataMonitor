# C5 / L03 — owned immutable BusSnapshot

Исторический план/отчёт своего checkpoint; факты и прежние failures сохранены.
Текущее завершение software и retirement 17-file closure описаны в
[C11](LEGACY_REFACTORING_C11.ru.md). Старые dormant route/serial references не
означают доступную текущую API или remaining migration.

## До изменений

Class: BusSnapshot — опубликованное point-in-time значение; BusReader копирует MMF под mutex, потребители декодируют вне mutex. Public ushort[] Words позволял менять уже опубликованное значение через constructor alias, свойство и record with.

Inv: snapshot владеет отдельной копией слов; constructor, object initializer и with replacement используют один defensive copier. Record copy/stale безопасно разделяет immutable owner. Никаких public span/memory над backing. MMF layout 5, AddIn sole producer/read-only Host, int32 CDAB time в 4–5, reserved header 49–56, независимые LAN/RAW, COM time, endian/raw float bits, mirror FC03/04/06/16, active overwrite каждый tick, inactive/stale hold, resize order и wire schema сохраняются.

N+1: очередной потребитель использует IReadOnlyList либо CopyWordsTo в собственный buffer; новые ownership-transfer factories, pooling, framework, assemblies и dependencies не нужны. Acquisition/scheduling/timing policy (C6) вне scope.

Baseline: main b9a2cd0699bec2b2c8d83a05f162805b1b3be4a3 после C4; 513/513 baseline, 31 approved divergence (передано владельцем). Исторический source-manifest сохраняется; exact provenance ledger оформлен отдельным metadata commit после ревью (см. ниже).

## Совместимость и стоимость

Words и positional parameter/deconstruct меняют public тип ushort[] → IReadOnlyList<ushort>: binary compatibility нарушена; source callers с arrays (включая named constructor arguments) сохраняются, mutation/Length/AsSpan требуют перехода на Count/index/copy. JSON constructor/property имеют одинаковый тип; Words остаётся JSON array. Equality/hash сохраняют reference identity owner, структурное сравнение не добавлено. Stale with сохраняет metadata/owner; replacement Words получает новую копию. Constructor добавляет массив (~20 KB для 10001 words) и read-only wrapper, включая дополнительную копию после BusReader capture. Bounded local copies нужны span-based consumers; MetricsSampler использует одну собственную копию для decode и previous.

Read-only owner — sealed SDK OwnedReadOnlyList<T> с private readonly backing, предоставляющий только IReadOnlyList Count/index/enumeration. ReadOnlyCollection над массивом раскрывает backing через safe cast ICollection.SyncRoot. BusSnapshot Own делегирует общему constructor, который делает indexed scalar copy, не передавая backing пользовательскому ICollection.CopyTo. GetRegister сохраняет прежний IndexOutOfRangeException при выходе за Words bounds.

## Проверки

Linux .NET SDK 10.0.302: Data.Tests focused `BusSnapshotOwnershipTests|BusAddrRingServiceTests` — **17/17**. Команда: `dotnet test iCemDataMonitor.Data.Tests --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~BusSnapshotOwnershipTests|FullyQualifiedName~BusAddrRingServiceTests'`. Покрыты array/List/read-only input alias, retained ICollection.CopyTo target, constructor/init/with/null, owner-sharing stale/copy, metadata/equality/hash/deconstruct, cast mutation и SyncRoot, copy bounds до writes, независимые returned arrays, raw qNaN/negative NaN/signalling/-0/subnormal/0014–00A7 bits, STJ round-trip всех metadata/words. Первая попытка ReadOnlyCollection выявила реальный failing regression SyncRoot на .NET 10; заменена узким owner.

Windows regression расширяет mutex-timeout test: retained old snapshot после file update и второго capture, затем stale нового snapshot. Modbus FC03/04 raw bit vectors и LAN/RAW independent updates при фиксированных words 4–5; gRPC GetRegisters проверяет независимость LAN/RAW и int32 time. Эти named-MMF tests и LiveModbusEmulatorTests только скомпилированы локально; выполнение ожидает Windows CI. Linux не объявляется их заменой. Full Data suite на Linux не запускался.

Affected compilation до SDK extraction: `dotnet build` Host.Tests и Transport.Tests — success, 0 warnings/errors; UI.Tests — success, 1 существующий xUnit1031 warning в HostClientTests.cs:662 (blocking task operation), 0 errors. Все команды с `--no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`. LiveModbusEmulatorTests включён в Transport.Tests compilation. `git diff --check` clean. frozen proto/SPA/AddIn/Rust runtime, scheduling/acquisition policy, deps и historical manifest не менялись.

## Подтверждённый same-class follow-up C4 — до изменений

Class: опубликованные catalog/families/register-map collections C4 — тот же boundary immutable sequence, что Words. Array.AsReadOnly защищает IList writes, но ICollection.SyncRoot раскрывает mutable backing; это нарушает согласованность cached list и Lookup. Найдено при проверке C5; исторические результаты C4 остаются описанием прежних проверок.

Inv: catalog, declared families и projected register map сохраняют owned storage, cached identity/order/healthy metadata/lookup и degraded snapshot lifetime. Никакой safe cast не должен позволить менять элементы. Сами элементы — value/immutable entries; требуется shallow storage ownership, не deep cloning arbitrary objects.

N+1: минимальный общий sealed OwnedReadOnlyList<T> в существующем SDK предоставляет Count/index/enumeration, private array и indexed defensive-copy constructor; без IList/ICollection/SyncRoot, span/memory, новых assemblies/deps/framework. BusSnapshot Own делегирует этому constructor вместо private duplicate. Каталоги применяют тот же механизм; extra startupcopy существующих projection arrays допустим. Domain projection и policies остаются в Plugins. Цветовая private copy не публикуется и не меняется.

Проверки до fix на .NET 10: три реальные попытки записи через SyncRoot изменили published catalog, families и register-map; regression **0 passed / 3 failed**. После extraction:

- Plugin.Tests полный suite — **169/169**, включая все прежние 166 healthy/fallback/lifetime/projection cases и три новых regression cases. `dotnet test iCemDataMonitor.Plugins.Icem.Tests --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`.
- Data.Tests portable — **18/18**: ownership+ring **17** и существующий CatalogSnapshotIntegrationTests **1**; та же команда Data выше с дополнительным `|FullyQualifiedName~CatalogSnapshotIntegrationTests` в filter. SDK/Plugins/Data и зависимые проекты rebuilt, warnings/errors отсутствуют.
- `git diff --check` clean. После extraction повторные Host/UI/Transport builds не требуются для этого private-storage изменения; полный Windows solution/CI и runtime checks остаются отдельной проверкой.

API C4 остаётся IReadOnlyList/IEnumerable; конкретные IList/ICollection casts теперь могут отсутствовать. Дополнительные startup copies catalogs/families/maps приняты. Generic owner гарантирует storage ownership, не глубокую неизменность произвольных mutable T; текущие consumers используют ushort, string и readonly value records.


## Независимое ревью и публикация

Три агента GPT-6.1 Sol / medium последовательно выполнили анализ,
реализацию и независимое ревью. Reviewer не нашёл материальных дефектов
в итоговых двух code commits. Самостоятельно повторены Data **18/18** и
Plugin **169/169**, без warnings/errors; worktree и diff-check чистые.

[PR №34](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/34) сохраняет
отдельные commits C5 `e126c5100b459b6e5e38d54a828d92c9a1643b9a`
и подтверждённого C4 follow-up `4fe2f7ebfb6535c19dbfc27a8b777427eae4d723`.
Опубликованные деревья совпадают с проверенными локальными деревьями.

Exact-path ledger `rust/migration/legacy-divergences.json` обновляет прежние
записи BusReader, MetricsSampler, TelemetryServiceImpl, ModbusContractTests
и DeviceCatalogRegistry без дубликатов и с сохранением описаний прошлых
изменений; остальные затронутые manifest paths добавлены отдельно. Все
записи содержат реальные PR/code commit SHAs и точные canonical blob hashes.
Исторический source-manifest остаётся на
`96e2206562c9389d1543095232115d81eae4b692`.

`Verify-Baseline.ps1 -RequireComplete`: **513/513**, **38** exact approved
divergences; frozen proto/assets/SPA и REST inventory сохранены.
[Windows/Rust/packaging CI](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/34/checks)
проверяет итоговый head. Ручная аппаратная/операторская приёмка здесь не выполнялась.
Следующий пакет плана — C6: единый acquisition owner, отдельные timing measurements
и согласованная latest-state семантика.
