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
Startup/fixture setup/warmup и teardown находятся вне окна. Нет модификации IFEO
или глобального kernel logger. Положительный и нулевой controls для capture и
FC03 codec записываются отдельно. Любой driver wait bounded; ошибка убивает
собственный process и останавливает собственный session.

`*-events.csv` — raw xperf dumper, `*-tracestats.txt` — loss evidence. Parser
принимает только явные HeapAlloc/HeapRealloc event headers с PID и Size/NewSize,
раздельно считает allocation calls/bytes и reallocation calls/NewSize bytes.
Они не являются net live bytes. Неизвестные schema/columns, отсутствие PID или
loss counters и ненулевой ETW loss сохраняют OPEN, не нулевые allocations.
Все четыре raw windows сохраняются даже при неизвестной parser schema, чтобы
actual Windows output позволял адаптацию с воспроизводимыми regressions.
При ненулевом zero-operation control window overhead остаётся OPEN; subtraction
не выполняется. Per-operation averages выводятся только при zero measured control.
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
