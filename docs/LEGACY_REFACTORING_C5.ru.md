# C5 / L03 — owned immutable BusSnapshot

## До изменений

Class: BusSnapshot — опубликованное point-in-time значение; BusReader копирует MMF под mutex, потребители декодируют вне mutex. Public ushort[] Words позволял менять уже опубликованное значение через constructor alias, свойство и record with.

Inv: snapshot владеет отдельной копией слов; constructor, object initializer и with replacement используют один defensive copier. Record copy/stale безопасно разделяет immutable owner. Никаких public span/memory над backing. MMF layout 5, AddIn sole producer/read-only Host, int32 CDAB time в 4–5, reserved header 49–56, независимые LAN/RAW, COM time, endian/raw float bits, mirror FC03/04/06/16, active overwrite каждый tick, inactive/stale hold, resize order и wire schema сохраняются.

N+1: очередной потребитель использует IReadOnlyList либо CopyWordsTo в собственный buffer; новые ownership-transfer factories, pooling, framework, assemblies и dependencies не нужны. Acquisition/scheduling/timing policy (C6) вне scope.

Baseline: main b9a2cd0699bec2b2c8d83a05f162805b1b3be4a3 после C4; 513/513 baseline, 31 approved divergence (передано владельцем). Исторический source-manifest и ledger не изменяются этим commit.

## Совместимость и стоимость

Words и positional parameter/deconstruct меняют public тип ushort[] → IReadOnlyList<ushort>: binary compatibility нарушена; source callers с arrays (включая named constructor arguments) сохраняются, mutation/Length/AsSpan требуют перехода на Count/index/copy. JSON constructor/property имеют одинаковый тип; Words остаётся JSON array. Equality/hash сохраняют reference identity owner, структурное сравнение не добавлено. Stale with сохраняет metadata/owner; replacement Words получает новую копию. Constructor добавляет массив (~20 KB для 10001 words) и read-only wrapper, включая дополнительную копию после BusReader capture. Bounded local copies нужны span-based consumers; MetricsSampler использует одну собственную копию для decode и previous.

Read-only owner — private sealed IReadOnlyList wrapper, не ReadOnlyCollection: последний над массивом раскрывает backing через safe cast ICollection.SyncRoot. Wrapper предоставляет только Count/index/enumeration; поле массива недоступно. Единственный Own клонирует indexed scalar copy, не передавая backing пользовательскому ICollection.CopyTo. GetRegister сохраняет прежний IndexOutOfRangeException при выходе за Words bounds.

## Проверки

Linux .NET SDK 10.0.302: Data.Tests focused `BusSnapshotOwnershipTests|BusAddrRingServiceTests` — **17/17**. Команда: `dotnet test iCemDataMonitor.Data.Tests --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~BusSnapshotOwnershipTests|FullyQualifiedName~BusAddrRingServiceTests'`. Покрыты array/List/read-only input alias, retained ICollection.CopyTo target, constructor/init/with/null, owner-sharing stale/copy, metadata/equality/hash/deconstruct, cast mutation и SyncRoot, copy bounds до writes, независимые returned arrays, raw qNaN/negative NaN/signalling/-0/subnormal/0014–00A7 bits, STJ round-trip всех metadata/words. Первая попытка ReadOnlyCollection выявила реальный failing regression SyncRoot на .NET 10; заменена узким owner.

Windows regression расширяет mutex-timeout test: retained old snapshot после file update и второго capture, затем stale нового snapshot. Modbus FC03/04 raw bit vectors и LAN/RAW independent updates при фиксированных words 4–5; gRPC GetRegisters проверяет независимость LAN/RAW и int32 time. Эти named-MMF tests и LiveModbusEmulatorTests только скомпилированы локально; выполнение ожидает Windows CI. Linux не объявляется их заменой. Full Data suite на Linux не запускался.

Affected compilation: `dotnet build` Host.Tests и Transport.Tests — success, 0 warnings/errors; UI.Tests — success, 1 существующий xUnit1031 warning в HostClientTests.cs:662 (blocking task operation), 0 errors. Все команды с `--no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`. LiveModbusEmulatorTests включён в Transport.Tests compilation. `git diff --check` clean. frozen proto/SPA/AddIn/Rust runtime, scheduling/acquisition policy, deps и historical manifest не менялись.
