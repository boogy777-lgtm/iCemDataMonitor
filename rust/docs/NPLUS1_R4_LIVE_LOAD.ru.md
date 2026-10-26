# R4: исполнимый software live-load fixture

Class: независимый software writer и benchmark consumers реального Windows file-backed
MMF (`examples/windows_live_load.rs`, `tools/perf/windows_live_load.py`);
production AddIn и аппаратная producer contention вне класса.
Inv: один Host acquisition200ms; writer меняет LAN/RAW при fixed COM, clients
проверяют coherent raw words; slow/disconnected clients не останавливают delivery.
Проверки fixture и сохранённые raw samples выполняются отдельным обязательным CI шагом.
N+1: новая частота клиента добавляется в таблицу workload Python; проверка всех
entries и наличие изменившихся LAN/RAW значений обязательны; production не меняется.

Метод и границы измерений будут дополнены вместе с исполнимым harness.

Class: отдельное окно ETW capture/publication либо Modbus codec request после
инициализации (`windows_release_measure --heap-window`, `windows_heap_window.py`);
network I/O, Web и process lifetime totals вне этой атрибуции.
Inv: READY после warmup, START после attach, DONE до остановки trace, RELEASE
после stop; bounded driver timeout и cleanup. Zero-operation control сохраняется
отдельно. Raw HeapAlloc Size / HeapRealloc NewSize считаются только при распознанной
схеме, PID и отсутствии ETW loss; KB totals не пересчитываются в точные bytes.
N+1: следующий workload добавляет explicit example mode и driver table, проверяемую
одним handshake/parser; новый native allocator hook или production counters не нужны.

## Запуск и артефакты

Обязательный reusable workflow `rust-nplus1-measurements.yml` собирает оба examples
и Host в том же workspace Release profile, затем запускает независимо latency,
live-load, warmed-window и прежний process-lifetime heap profiles. Ошибка одного
из runtime шагов не скрывает доступные evidence остальных; любой failed step
по-прежнему делает job неуспешным. Общий upload выполняется always.

```powershell
cargo build --release --locked --offline -p icem-platform-windows --examples
python tools/perf/windows_live_load.py --output C:/temp/icem-live
python tools/perf/windows_heap_window.py --output C:/temp/icem-heap-window
```

`windows_live_load` создаёт уникальные file/mutex/map names. Writer каждые50ms
под safe `NamedMutex` переписывает один и тот же файл; реальный WindowsBusReader
использует существующий read-only file-backed MMF fallback. Это software producer,
не AddIn. COM words4–5, COM write time/count и reserved bytes сохраняются. LAN200
и RAW400 несут одинаковый изменяющийся raw float marker, RAW402/404 — −0 и
минимальный subnormal. LAN/RAW writer times/counts меняются вместе; источник COM
времени не используется для LAN/RAW.

Сначала отдельный12s probe использует WindowsBusReader + настоящий latest cell,
200ms schedule и consumers100/20/5/1Hz, каждый удерживает максимум один Arc;
ещё один старый frame удерживается до конца и проверяется побитно. `retention.csv`
содержит actual capture times, отдельное elapsed capture+publication время
(исключая weak bookkeeping/checks/CSV) и peak unique live Snapshot payloads по
Weak handles. Во время нового capture учитывается ещё удерживаемый previous
publication. Guard≤7 относится только к этой явной fixture topology; это не
оценка всех Host/Web history/adapter объектов или общего allocator usage.
Счётчик не выводится из числа network requests. Probe завершается до запуска Host.

Далее запускается настоящий Host `--bus-live`, чей acquisition200ms остаётся
единственным reader owner этой фазы. Одновременно12s работают persistent Modbus
clients2/50Hz и GetRegisters clients1/20Hz. Каждый Modbus sample содержит три FC03
read (COM, LAN, RAW), их total roundtrip явно подписан; разные reads могут попасть
на разные publications. Проверка coherent LAN/RAW выполняется в одном Web reply,
а для Modbus проверяются каждый marker, fixed COM и RAW IEEE bits. Каждый client
обязан увидеть минимум три разных LAN/RAW markers. Raw samples и p50/p95/p99
сохраняются в `live-summary.json`.

Пятый client удерживает незавершённые Modbus/HTTP requests700ms и disconnect,
повторяя цикл параллельно. Это медленная отправка/занятые connection slots и
обрывы, не доказательство outbound socket backpressure или subscription backlog.
Ошибки/зависание остальных consumers делают шаг failed. Все child processes
останавливаются с timeout; их logs и writer.csv остаются в artifact.

Один внешний PowerShell sampler32 раза через500ms сохраняет cumulative Host
user+kernel CPU time, working set, lifetime peak working set, private bytes,
handles и threads в `process-metrics.jsonl`. Summary сохраняет CPU first/last/delta
и max наблюдённые process memory fields. Это process evidence с overhead
внешнего наблюдателя, не live-byte attribution по allocations и не per-capture CPU
cost. Sampling продолжается немного после12s request window; интервалы явно
сохранены UTC timestamps. Heap profiling выполняется отдельно от latency.

## Attribution: измеряемые границы и OPEN

Example делает100 warmup операций и предварительный WARM/WARMUP exchange для
инициализации stdin/stdout buffers. Driver дожидается READY, начинает только
свой уникальный heap session через `xperf -start NAME -heap -pids PID ... -f ETL`,
передаёт START, ждёт DONE, останавливает только NAME, затем передаёт RELEASE.
Startup/fixture setup/warmup и teardown находятся вне heap окна. До child launch
отдельный WPR `CPU.Light -instancename UNIQUE` записывает process/thread metadata;
тот же instance suffix обязателен для status/stop/cleanup. Он работает до child exit,
затем собственные heap и process ETLs сливаются через xperf для разрешения PID.
Нет модификации IFEO или глобального kernel logger. Положительный и нулевой controls для capture и
FC03 codec записываются отдельно. Любой driver wait bounded; ошибка убивает
собственный process и останавливает собственный session.

`*-events.csv` — raw xperf dumper, `*-tracestats.txt` — loss evidence. Parser
принимает только явные HeapAlloc/HeapRealloc event headers с PID и Size/NewSize,
раздельно считает allocation calls/bytes и reallocation calls/NewSize bytes.
Они не являются net live bytes. Неизвестные schema/columns, отсутствие PID или
loss counters и ненулевой ETW loss сохраняют OPEN, не нулевые allocations.
Loss fields читаются из actual dumper trace-header summary, отдельно для обоих
исходных ETLs и merged trace; provider-count table `tracestats` сам по себе их не содержит.
Все четыре raw windows сохраняются даже при неизвестной parser schema, чтобы
actual Windows output позволял адаптацию с воспроизводимыми regressions.
При ненулевом zero-operation control точная operation-only атрибуция остаётся OPEN;
subtraction не выполняется. Recorded-window average на operation включает overhead
и подписан явно; отдельное operation-only поле разрешено только при zero measured control.
Это контролируемое attribution window, не универсальная гарантия нулевого noise.

Capture workload включает настоящий reader + publish + latest handle; request
workload включает настоящий `RegisterBank::process` FC03, prebuilt100-word request,
response allocation/drop, без network I/O. Полный Web/network request heap
attribution и Host-wide live Snapshot cardinality остаются OPEN: этот fixture
не подменяет их codec measurements или отдельным Arc probe. Actual Windows
compilation/runtime, toolkit compatibility и numerical outputs нужны прежде
закрытия соответствующих R4 gates. Hardware/AddIn contention остаются отдельны.

Первичные документы Microsoft:
[existing-process capture](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/enabling-heap-data-capture),
[dumper](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/dumper),
[HeapAlloc Size bytes](https://learn.microsoft.com/en-us/windows/win32/memory/etw-heap-event-alloc),
[HeapRealloc NewSize](https://learn.microsoft.com/en-us/windows/win32/memory/etw-heap-event-realloc).

Локально15 Python parser/validation tests и46 CI tests PASS; py_compile и
`git diff --check` PASS. Rust/cargo/rustfmt, PowerShell и Windows runtime отсутствуют
в этой среде; их исполнения/успеха эта запись не утверждает. R4/R5 остаются OPEN
до actual evidence окончательного дерева.

## Actual Windows checkpoint: run40191811070

Source `df116528459356123b84a1ef9627b64651c274c1`, CI merge
`b6d22770684e14d01006dfd7d595640223f8437c`, tree
`2054e988c5033606cb04470badc1283ad6474cc3`; job121164251356.
Windows Server2025, AMD EPYC9V74,4vCPU, Rust1.99.1 MSVC, workspace Release.
Build, unprofiled latency и live-load PASS. Artifact12598631775
`windows-release-measurements`, ZIP SHA256
`fb602a0634bbed918268f0bdcb2f732cdc68aeac9ea7fe2b97326a340ccd3f59`;
downloaded ZIP проверен по этому digest. Native installer и legacy jobs PASS;
debug job121164251302 FAIL на rustfmt двух examples до debug restore.
Его семь whitespace-only hunks применены буквально; новый Windows fmt verdict нужен.

| Измерение | Actual результат |
|---|---|
| Tight-loop physical reader+publication,1000 samples | p95=22.4µs,p99=53.5µs; correctness вне timed region |
| Uncontended mutex hold upper bound | p95=3.9µs,p99=5.7µs |
| Отдельный5Hz probe,12s |60captures; peak4 unique live Snapshot payloads; capture+publication p95=199.4µs,p99=207.1µs |
| Modbus2Hz,24samples,три FC03 |p95=7.7333ms,p99=7.7631ms |
| Modbus50Hz,600samples,три FC03 |p95=7.7207ms,p99=7.7908ms |
| Web1Hz,12samples,один GetRegisters |p95=p99=2.8743ms |
| Web20Hz,240samples,один GetRegisters |p95=1.2709ms,p99=1.8193ms |
| Slow/disconnected client |17cycles с700ms incomplete requests; остальные consumers завершили12s |
| Host process sampling |32samples; CPU cumulative delta0.5s; lifetime peak working set14385152bytes; max observed private3768320bytes |

Fixed COM, изменяющиеся LAN/RAW, coherent Web frame, RAW−0/subnormal и pinned old
Snapshot checks прошли. Эти результаты не являются hardware/AddIn acceptance,
outbound backpressure proof, Host-wide Arc peak или точными heap live bytes.
Низкочастотные24/12samples — маленькие распределения этой fixture, не production SLA.

Оба heap steps FAILED до получения ETL: attach отверг singular `-Pid` как unexpected;
process-lifetime guard ошибочно нашёл substring `WPR` в `DefenderApiLoggerLowPriv`.
Actual heap allocation counts/bytes и loss statistics этого run отсутствуют, OPEN.
Исправлены только command `-pids` по [xperf start](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/start)
и anchored complete logger-name parsing. Имена с префиксами WPR/HeapSession
по-прежнему блокируют запись, неизвестный inventory отклоняется; foreign session
не останавливается. Regression проверяет actual Defender name, profile names и
unknown inventory.18Python tests PASS локально; новый Windows toolkit/runtime
и окончательный полный CI всё ещё требуются. R4/R5 не закрываются этим checkpoint.

## Toolkit/schema checkpoint: run40192653339

Source `0065ed06e46a0d07cdea19671896069cdc7d1a38`, CI merge
`6112781af99c10d0895ea6474b9c55b89402c906`, tree
`0fdea396072a96d6ec267bb041ae86a24de96f8a`; Release job121166826994.
Build, latency и live load вновь PASS, оба heap steps FAILED на evidence parsing.
Artifact12598996146, ZIP SHA256
`9e5c3eb04d5e7bd4245dc141085302a23513dfe79f6b66608b05ccbb70457a3c`
проверен после download. Все четыре warmed windows записаны в raw ETL/CSV,
attach/stop/handshake и owned session cleanup успешны.

Actual `tracestats -detail` содержит provider counts, но не loss counters;
`dumper` trace header явно содержит `Events Lost: 0, Buffers lost: 0`.
Actual process-lifetime table использует `Alloc#`, `Alloc KB`; total и per-heap
rows остаются раздельными и не суммируются повторно. Оба формата теперь покрыты
точными excerpts этого artifact и положительными/отрицательными regressions.

В heap-only ETL отсутствует process/thread mapping: `HeapAlloc` rows показывают
`"Unknown" ( -1)`. Они отвергаются, даже когда trace attach нацелен на один PID;
allocation numbers этому PID не приписаны. Follow-up добавляет собственный WPR
instance `CPU.Light` до запуска child и merge собственных traces после child exit.
Это измерительный metadata collector; production/MMF/consumers не меняются.
Тест по actual unresolved-PID excerpt требует fail-closed error. Child-launch fault
проверяет stop только того же WPR instance; child cleanup fault сохраняет
WPR stop и summary с error.23 Python tests
PASS локально; новый Windows run должен подтвердить разрешённый PID, loss checks,
данные обоих controls/workloads и полный окончательный CI. R4/R5 пока OPEN.

Все p50/p95/p99 в raw summaries используют отсортированный массив и точный индекс
`min(n-1, floor(n*p))` при p=0.50/0.95/0.99. Это фиксированная convention harness,
не утверждение канонического nearest-rank algorithm; numeric baselines не менялись.

## Resolved ETW checkpoint и live readiness: run40194159508

Source `9c42e9799fb0949e4bc9f0b541216bb4d5131b9d`, CI merge
`498392bf0d60b28b25345c2b6ebf0a84b8f693d3`, tree
`e109c26c44a41b8e0a31a172624633b3148692dc`; Release job121171358225.
Artifact12599418106, ZIP SHA256
`c0cf2d6b38080ada94125c530caa7e2dc6aedbe86cb9962d9f57ec7f4f82990a`
проверен после download; hashes всех четырёх merged event CSV, обоих lifetime
ETL и latency CSV/JSON также совпали с raw summaries. Малый авторитетный
[численный checkpoint](evidence/nplus1-r4-run40194159508.json) сохраняет числа,
source/tree, PID, raw hashes и точные ограничения; bulk ETL остаются в CI artifact.

Оба heap steps PASS. Четыре windows имеют разрешённый target PID; для каждого
original heap ETL, original process ETL и merged ETL Events Lost=Buffers Lost=0;
owned collector cleanup успешен. Startup/control и10000operations записаны отдельно.

| Workload | HeapAlloc events | Sum Size bytes | HeapRealloc events | Sum NewSize bytes |
|---|---:|---:|---:|---:|
| Capture control,0ops |10|7697|0|0|
| Capture+publication+latest,10000ops |50010|30127697|0|0|
| FC03 codec control,0ops |10|7697|0|0|
| FC03 codec100words,10000ops |70010|5077697|60000|5040000|

Это native ETW HeapAlloc/HeapRealloc events, **не** доказанный счётчик вызовов
Rust GlobalAlloc; native reallocation может также порождать HeapAlloc event.
Size и NewSize totals не складываются в net live bytes. Inclusive recorded-window
averages: capture5.001 HeapAlloc events/3012.7697 Size bytes на operation;
FC03 7.001 HeapAlloc events/507.7697 Size bytes и6 HeapRealloc events/504 NewSize
bytes на request. В них включён control overhead; subtraction не выполнялась.
Control bytes составляют примерно0.026% capture window и0.152% FC03 window.
Точная operation-only attribution остаётся ограничением, а не нулевым overhead claim.
Process-lifetime totals отдельно: startup783 HeapAlloc events/222.7 native KB;
10000capture/publication process50663events/29322.8KB. Они включают startup/teardown,
не подменяют warmed-window figures и не пересчитываются в точные bytes.

Actual live step FAILED в Modbus consumer на `COM source time changed`;
actual bits/sample index в старом exception отсутствовали. Retention probe прошёл
60captures/peak4, Host/writer не сообщили раннего завершения. Причина именно этого
unlogged значения не доказана; нулевой initial mirror — обоснованная inference.
Source-established fixture race существует независимо: `Acquisition::start` отправляет
ready после создания source, **до** первой acquisition; `main::refresh` может увидеть
absence и оставить initial zero bank. Первое scheduled mirror refresh назначается
на500ms после завершения boot, тогда как Web listener доступен раньше. Driver's
fixed500ms после TCP-connect не гарантирует первую публикацию/refresh.

Class: initial Modbus mirror readiness в `windows_live_load.py`, не production
acquisition, mirror semantics или допустимые ответы measured workload.
Inv: только initial zero до measurement считается pending; ожидаемый nonzero COM
value подтверждён FC03 под тем же10s startup deadline. Wrong nonzero, timeout и
любой invalid/zero measured response fail;12s load, частоты и проверки прежние.
N+1: fixture требует COM value, отличимый от initial zero; readiness helper имеет
regressions zero→valid, pending timeout, wrong nonzero и measured-zero failure.
Exception теперь сохраняет actual/expected, Modbus kind/rate/sample index.
Connect/send и каждый fragmented recv ограничены остатком общего deadline;
даже ожидаемый ответ после deadline отклоняется. Regressions проверяют late-valid
response и исчерпание бюджета между fragments. Fixture Host duration увеличен
с25s до30s: разрешённые10s startup +16s sampler не помещались в25s.
Это только lifetime тестового процесса; measured workload остаётся12s.
28 Python perf tests,46 CI tests и diff-check PASS локально; actual Windows live verdict follow-up
ещё требуется. Production Rust/COM/AddIn и существующие CI gates не менялись.

| План §13.4 / R4 requirement | Доступное evidence | Остаток |
|---|---|---|
| Capture/decode/publication time, MMF hold | Current unprofiled1000samples: capture+publication p95=24.0µs,p99=40.4µs; hold upper p95=4.0µs,p99=4.4µs | Uncontended650words, не production mutex-contention distribution |
| Allocations/allocated bytes на capture/request | Current PID-resolved/loss-free native event windows выше; representative real capture и production FC03 codec | Window averages включают измеренный control; Web/network heap attribution не заявлена |
| Peak live snapshots и memory/CPU | Separate5Hz topology probe60captures/peak4; run40191811070 PASS actualHostCPU/memory и12s multi-client load | Probe peak не Host-wide Arc count; process memory не allocator live bytes; current live follow-up обязателен |
| Modbus/Web p95/p99 и разная частота/slow clients | run40191811070 и40192653339 live PASS; current live FAIL сохранён с минимальным readiness fix | Новый actual Windows live PASS без ослабления measured assertions |
| Final software CI | run40194159508: Rust verify/routing/baseline/packaging и legacy PASS; Release FAIL только live-load, оба heap steps PASS | Все обязательные jobs окончательного опубликованного дерева и R5 review |
| Physical deployment | Software writer/file-backed MMF/CI VM | AddIn, поддерживаемое оборудование, настоящая producer contention, operator/UI/SDK-distribution acceptance отдельно |

Первые строки являются scoped software measurement, а не обещанием абсолютного
отсутствия allocation noise, универсального memory bound или ускорения относительно
другого оборудования/рантайма. R4 остаётся OPEN до live follow-up и окончательного CI.
Software R4 acceptance относится к явно измеренным fixture boundaries; exact
operation-only cost при ненулевом control, Web/network heap attribution и Host-wide
Arc count не становятся доказанными после одного live PASS. Это отдельно названные
границы evidence, а не скрытое требование нулевого allocation noise для приёмки
представленных software measurements.
