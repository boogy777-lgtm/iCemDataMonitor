# CI-2b / D55: synthetic register mirror fixture и CI diagnostics

Scope: отдельный prerequisite CI-1 поверх CI-2a/D54. Production .NET, Rust
runtime, baseline manifest, frozen proto/SPA, toolchain и зависимости не меняются.
E23 и ensure-host не входят в изменение.

## Подтверждённые факты и границы вывода

На PR21 head `e8f99925620afa47fe752c217012d96773f672d0` Legacy run
`39436420724` прошёл 940 из 941 tests. Единственный FAIL:
`RegisterMirrorTests.GetRegs_ReturnsZones_And_DecodedFloats`, line87,
`active` LAN оказался false; длительность теста около 6 s. Rust run
`39436420939` и installer gates этого head завершились SUCCESS. Это результаты
исходного head, а не доказательство Windows PASS после CI-2b.

Старый fixture записывал `DateTime.UtcNow.Ticks` до запуска TestServer. Endpoint
проверяет `producerFlag && ageMs < 5000`; заголовок fixture больше не обновляется.
Задержка старта/исполнения более 5 s делает ожидаемый active=true недействительным.
Это воспроизводимый механизм зависимости fixture от wall clock. Исходный CI log
не содержит ageMs и фаз старта/запроса, поэтому точная временная последовательность
и причина именно того FAIL остаются inferred, а не подтверждённым scheduler trace.
Старый helper также синхронно блокировал StartAsync и не освобождал application/client.

Четыре ранее запрошенных CI-теста уже исправлены отдельными изменениями и здесь
не переписываются:

| Тест | Существующее доказательство/исправление |
|---|---|
| `MetricsSamplerTests.DropOldest_SlowConsumerLosesSamples_BacklogNeverAccumulates` | D48: initial full sample может быть вытеснен capacity-one DropOldest; bounded loss-free acquisition, исходные overflow/count/value assertions сохранены. |
| `UiNavigationServiceTests.Navigate_AliveUi_SignalsShowEvent` | D44: dedicated mutex-owning thread, acquisition acknowledgement и explicit release вместо Task.Run + fixed sleep. |
| `SerialTeeServiceTests.TeeLoop_VirtualPortMissing_RetriesConnection` | D46/PR17: second factory signal и cancellation на том же стеке; exact attempt=2, исходные PAIR_MISSING и бюджеты сохранены. Точная причина прежнего Windows timeout не доказана. |
| `BusHealthMonitorTests.StaleProbe_ZoneAgeCrosses5s_VerdictDecaysToInactive` | ProbeClock: virtual UTC двигает возраст под настоящим mutex; frozen last-good snapshot обязан стать inactive. |

D45 отдельно устранил аналогичное предположение о первом sample в freshness test.
Подробная история и ограничения остаются в [SOURCE_AUDIT](SOURCE_AUDIT.ru.md).

## Class / Inv / N+1

Class: disposable synthetic Bus/TestServer fixtures в
`iCemDataMonitor.UI.Tests/RegisterMirrorTests.cs`; production writer/clock и
health-monitor boundary tests — соседние, отдельные случаи.
Inv: decode/zones/history/scalars и stale assertions сохраняются; future-clock
fixture обязан вернуть ageMs=0, producerFlag=true, count=1; владелец останавливает
и освобождает application/client/reader до удаления временного каталога.
N+1: каждый новый endpoint-test использует `CreateReader`/`CreateClientAsync`;
все 9 существующих caller sites await helper и используют общий ownership path.
Существующий health-monitor matrix проверяет границу без новых production seams.

## Детерминированность и cleanup

Fresh-only LAN и соседний COM получают явно синтетический future-clock header
`DateTime.MaxValue.Ticks`. Production AgeMs clamps отрицательный возраст к 0.
Имена helper/comments и assertions явно фиксируют этот механизм: это не обычный
fresh producer и не доказательство freshness для возраста 0..4999 ms. Endpoint
не имеет clock seam; новый production seam ради теста не добавляется.

Все исходные decoded float/zones assertions остаются. Отрицательный endpoint
control сохраняет точный input `DateTime.UtcNow.AddMilliseconds(-6_000).Ticks`
и четыре LAN assertions: inactive, producerFlag=true, age>MaxZoneAgeMs, count42;
RAW остаётся inactive/raw flag false. Без age gate stale-LAN assertion падает.
Неизменённый `BusHealthMonitorTests.Probe_AllZones_UseSameAgeGate_OnFreshAndStaleSnapshots`
проверяет -1/0/4999/5000/5001 ms и flag=false на fresh и stale snapshots;
ровно 5000 ms остаётся inactive. Future-clock fixture не заменяет этот coverage.

Класс реализует xUnit `IAsyncLifetime`; общий async helper await-ит StartAsync.
Fixture владеет каждым reader сразу после создания, затем application/client;
responses освобождаются в методах. При startup failure application проходит
StopAsync и DisposeAsync; при обычном/failed test cleanup client освобождается,
application останавливается/освобождается, затем reader и каталог. Ошибка удаления
не скрывается. Каждый существующий xUnit instance создаёт один application;
универсальный resource framework не вводится.

Исходный HttpClient request timeout остаётся default 100 s. Startup/StopAsync
получают cancellation budget 100 s той же величины, без расширения request budget.
StopAsync cancellation является cooperative; DisposeAsync не имеет cancellable API
и не заявляется как hard-bounded OS cleanup. TestServer здесь не регистрирует
background hosted workers; workflow сохраняет blame-hang 10m. Sleeps, polling,
retries, отключение параллелизма и новые пакеты не добавляются.

xUnit output содержит monotonic Stopwatch elapsedMs для startup/request/stop и
полный небольшой объект liveness сразу после JSON decode, до содержательных
assertions. Полное register body и чужие файлы не логируются.

## CI artifacts и fail codes

Изменён существующий `.github/workflows/ci.yml`, не CI-1 coordinator. Полные
`dotnet build CI.slnf -c Release` и `dotnet test CI.slnf -c Release --no-build`
с `Category!=Live` и `--blame-hang-timeout 10m`, MSI smoke и его 30m limit сохранены.
Добавлен `--logger trx` без фиксированного имени: каждый project пишет в собственный
TestResults; общий `--diag` path, перетирающийся между проектами, не вводится.

Build/test console сохраняется через Tee-Object; `$LASTEXITCODE` немедленно
копируется после native pipeline и возвращается через exit. Нет continue-on-error.
В runner.temp сохраняется только allowlist provenance: SHA, run ID/attempt,
runner OS/arch/image и dotnet --info. Environment dump, credentials и bus files
не собираются. `if: always()` upload сохраняет только TRX, Sequence.xml, TestResults
logs и указанные console/provenance files. Имя включает run ID + attempt, retention
7 days. Dumps не входят в upload patterns. При cancellation runner upload не гарантирован.

## Проверка и оставшиеся gates

Локальные проверки PASS: actionlint, YAML parse, PowerShell parser всех 6 run
scripts, `git diff --check`. Source check подтвердил сохранение всех 46 исходных
assertions, 9/9 awaited/owned caller sites, точный stale input и неизменённый health
matrix. Root отдельно проверил реальные native exit 0/exit 7 через Tee-Object и
немедленный LASTEXITCODE в pwsh 7.4.20: оба кода сохранены.

Focused compile SDK 10.0.402 PASS: `dotnet build
 iCemDataMonitor.UI.Tests/iCemDataMonitor.UI.Tests.csproj -c Release --no-restore
 --nologo -p:EnableWindowsTargeting=true -m:1 -nodeReuse:false`, локальный
`DOTNET_PROCESSOR_COUNT=2`. 0 errors, 1 существующий xUnit1031 warning в неизменённом
`HostClientTests.cs:662`. Это только local flags; CI parallelism не меняется.
Первая parallel restore phase вернула FAILED с 0 errors без раскрытой причины;
sequential restore `--disable-parallel -p:BuildInParallel=false` завершился exit 0,
несмотря на промежуточные NuGet proxy 403/incomplete-response сообщения.
Project/package sources не менялись. Compile log `/tmp/icem-ci2b-dotnet-compile.log`;
предварительные build/diag/restore logs с тем же prefix вне repository.

Локальный Linux-compatible `GetHistory` subset: 10 passed, 0 failed/skipped,
`dotnet test ... --no-build --no-restore --filter
"FullyQualifiedName~RegisterMirrorTests.GetHistory" --logger trx`.
Эти tests используют history без seeded Windows MMF и проверяют общий async
TestServer/ownership path; seeded register/MMF tests на Linux не запускались.
Log `/tmp/icem-ci2b-dotnet-history.log`. Это не Windows runtime acceptance.
Linux не доказывает Windows MMF,
TestServer runtime, MSI или окончательный exact-head CI. Обязательны отдельный review
и полный Windows run на окончательном head, включая неизменённые четыре теста выше.

Frozen source manifest не изменяется. Root review должен добавить точный hash
RegisterMirrorTests как approval15 с настоящими PR21/commit provenance и обновить
существующий approval `.github/workflows/ci.yml`. До этого Verify-Baseline ожидаемо
FAIL на изменённом legacy path; обход guard не допускается.
