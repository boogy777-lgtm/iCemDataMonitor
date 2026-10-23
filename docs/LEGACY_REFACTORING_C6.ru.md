# C6 / L05: единственный захват legacy bus

## Class / Inv / N+1 (решение до изменения кода)

Class: acquisition — отдельный BackgroundService в Data; publication — неизменяемый coherent record + Volatile reference; consumers — только ILatestBusState. BusReader сохраняет файловый lifecycle, sticky ACL policy, memcpy/decode под mutex и C5 owned immutable arrays.

Inv: один последовательный Snapshot loop; requests, subscribers, Modbus, health и ring не открывают источник и не захватывают mutex. Каждая операция читает один Current. Новый успешный capture определяется заменой LastSnapshot reference, даже при идентичных байтах; fallback reader без Stale не является успехом. Ошибка публикует retained stale snapshot, coherent IsOpen/error и монотонные attempt/completion/last-success timestamps; sequence растёт только при новом copy+parse. Нет source timestamp freshness heuristic.

N+1: новый consumer получает тот же read-only ILatestBusState, не создаёт poller/reader. Никаких observer/channel frameworks, новых assemblies, dependencies или INI keys. Modbus каждую итерацию сохраняет resize → header → stale gate → independent active zones, без дедупликации publication.

## Явные изменения времени и lifecycle

Production acquisition period = min(modbus_tick_ms, MetricsSampler.MinIntervalMs), при допустимом INI 200..500 это 200 ms. Петля sample-then-delay: синхронный capture, затем cancellable wait этого периода; стоимость capture добавляется к расстоянию между попытками, catch-up burst нет. Первый захват происходит сразу в background; StartAsync не ждёт источника. До первой публикации register RPC / REST возвращают Unavailable / 503, metrics завершается Unavailable; status/control доступны.

Current истекает после period + существующий BusReader.MutexTimeoutMs (обычно 700 ms) от последнего завершения (или старта до первого завершения). Во время mutex wait предыдущая completed publication остаётся доступной до deadline. Истёкший view не делает IO: retained snapshot становится Stale, последний известный IsOpen сохраняется. Known-open stale GetRegisters остаётся доступен; actual-closed отвергается. Это freshness acquisition pipeline, не hardware freshness threshold.

CaseState теперь берётся из того же StationHealth probe, что остальные status поля: 1 Hz health cadence плюс acquisition lag. REST ранее уже использовал LastSnapshot ?? Snapshot, поэтому не каждый REST request делал физический capture.

Ring сохраняет 1 Hz и пропускает null/stale и повторный successfulCaptureSequence; одинаковые значения/COM timestamp нового capture не дедуплицируются. Metrics сохраняет независимые subscriber clocks, min interval, capacity-1 DropOldest и per-stream deltas.

Shutdown отменяет ожидание периода и join-ит выполняющийся capture перед disposal Reader. WaitOne(500 ms) не отменяется мгновенно. DI создаёт/владеет Reader factory; acquisition регистрируется до health/ring и останавливается после них. Stop публикует terminal closed/stale state.

StandaloneShellSession.ViewerOnly вызывает TryOpen + LastSnapshot, но никогда Snapshot: вероятный прежний empty-viewer defect вне C6; здесь не исправляется. CLI уже RPC-backed; dormant endpoints не активируются. Frozen wire/layout/scalar/time/CDAB/COM 4–5/LAN/RAW/reserve/FC gates не меняются.

## Проверка и измерения

Windows MMF execution и сравнительные числа требуют Windows CI; Linux build/pure tests не являются доказательством Windows lifecycle или hardware performance.

### Windows measurement harness

`AcquisitionMeasurementsTests.RealMmf_CallPatternComparison_EmitsReproducibleMetrics` использует один final binary: тестовый legacy adapter захватывает настоящий BusReader на RPC/status-like call, per-subscriber sample, Modbus tick и health/ring tick; новая ветка использует настоящий BusAcquisitionService. Это comparator старого **call pattern**, не запуск baseline commit 401366 и не hardware benchmark. REST не включён в call amplification: прежде он уже был cache-first. На каждый вариант есть idle и одинаковый concurrent request-like load (4 projection clients @10 ms, status-like @50 ms, 2 subscribers @200 ms, Modbus @200 ms, health/ring @1 Hz), по 1500 ms; actual RegisterDecoder, actual TickOnce и actual FC03 projection вызываются тестом через reflection, без TCP/RPC serialization.

Физические attempts/successes считаются обёрткой настоящего Reader. Carrier этого measurement harness — валидный файл через настоящий unnamed fallback MMF Reader: producer-owned named RW section здесь не удерживается, поэтому результаты не доказывают performance named producer path или hardware. Named-section lifecycle отдельно проверяется в BusAcquisitionWindowsTests. При здоровом carrier успешный Snapshot соответствует одному copy+decode (числа copy/decode явно inferred 1:1, без изменения Reader). Изолированный synchronous capture allocation использует GetAllocatedBytesForCurrentThread без await; allocation всей async фазы — aggregate process estimate, включая background/tick/task overhead, не ложное per-thread RPC measurement. Tick duration/lateness, completed-publication age и update→first publication/Modbus visibility дают n/p50/p95/p99. Marker учитывается один раз на каждой поверхности; publication observer @2 ms даёт sampled upper bound, пропущенные captures source updates не выдумываются. Stop измеряется отдельно idle/held real mutex; функциональный bound counts зависит только от configured period и elapsed phase, performance thresholds не вводятся.

JSON-lines пишутся в `TestResults/c6-mmf-measurements.log` и ITestOutputHelper/TRX (существующий artifact glob `*.log`). Runtime/OS/CPU/geometry/cadence/методика записаны первой строкой. Linux guard честно сообщает NOT EXECUTED; Windows results пока ожидаются и должны быть reviewed до merge.

### Локальная проверка (Linux, SDK 10.0.302)

Build closure Data.Tests / Transport.Tests / Host.Tests / UI.Tests прошёл последовательно с `--no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`. Единственное предупреждение UI — прежний xUnit1031 в HostClientTests.cs:662; C6 не меняет этот тест.

Выбранные проверки:

- Data.Tests: `FullyQualifiedName~BusAcquisitionTests` — 6/6. Чистые interface-based tests ownership, start/stop/join, freshness/reference sequence, монотонного expiry, frozen ring, independent metrics и discovered-production call-map guard.
- Host.Tests: `FullyQualifiedName~LatestBusConsumerTests|FullyQualifiedName~UiThemeRpcTests|FullyQualifiedName~LayerCommandTests` — 13/13 reported pass. HKCU-dependent StatusAndControls имеет Windows guard и на Linux фактически не выполняется. DI disposal test выполняется: source factory-owned, capture блокируется, container.Dispose обязан join-ить capture до Source.Dispose даже без отдельного Host.Stop.
- Transport.Tests: `FullyQualifiedName~CachedPublication_EveryModbusTick` — 1/1: одна неизменная publication всё равно вытесняет FC06 active/header writes каждый tick; inactive RAW/reserve hold; resize/header выполняются до stale gate.
- UI.Tests: `FullyQualifiedName~LatestRegisterMirrorTests` — 1/1: только ILatestBusState в DI, один Current на request, first/closed 503 и known-open stale available; source IO невозможно без Reader в контейнере.

Новые Windows-only проверки: BusAcquisitionWindowsTests (absent→present, идентичные bytes/time дают новый sequence, mid-life name-removal→recreate, repeated non-Stale Reader fallback не является успехом, mutex timeout/terminal state); BusAccessDeniedTests.Acquisition_DenyReadAcl_PublishesStickyDiagnostic_WithoutThrowingHost; AcquisitionMeasurementsTests.RealMmf_CallPatternComparison_EmitsReproducibleMetrics; LatestBusConsumerTests.StatusAndControls_ReadHealthCaseState_WithoutReadingLatestOrSource. Мигрированные real-MMF классы MetricsSamplerTests, BusRingServiceTests, BusHealthMonitorTests, TelemetryServiceImplTests, ModbusContractTests, RegisterMirrorTests требуют полного Windows CI. Здесь результаты их выполнения и performance numbers не заявлены.

## Независимое review и provenance

Три последовательных агента GPT-6.1 Sol medium завершили analysis → implementation → independent review. Существенных production дефектов review не обнаружило; независимый rerun Data6/6, Host13/13 reported (HKCU guard на Linux не выполняет тело), Transport1/1. Reviewed local tree a6a789a4e99af2f68f94322157908b8500bd9f3d опубликован с тем же Git tree: code commit `86dcb27c376ab5ed03ad04d579bf3d6059f527c8`, docs clarification `1a3c68d47c81b06c5dfe8b754c50b4381f70c650`, [PR35](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/35). Historical source manifest остаётся закреплён на `96e2206562c9389d1543095232115d81eae4b692`; ledger обновляет существующие approvals без потери прежнего reason и использует точные Git blob hashes.

Официальный `Verify-Baseline.ps1 -RequireComplete`: **513/513**, **42 exact approved divergences**, frozen proto/assets, D16/D17 theme predicates и REST inventory OK. `git diff --check` clean. Full Windows CI и анализ measurement artifact ожидаются до merge. Следующий пакет после C6 — C7 (logs); в C6 он не реализован.
