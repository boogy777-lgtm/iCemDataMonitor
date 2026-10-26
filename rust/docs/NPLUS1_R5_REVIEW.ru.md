# R5: независимая интеграционная сверка, 26 октября 2026

## Окончательный software status — 26 октября 2026

R4/R5 **закрыты для scoped N+1 v1.7 software checkpoint**: Windows
[run40250966619, attempt2](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/40250966619/attempts/2)
SUCCESS, общий CI gate121348687621 и Release job121345765540 SUCCESS.
Проверенный source `7d8af1d22be2d38573d7a06b96b54c0f5a654423`, CI merge
`0593bb01c60b5b9ad9204d7223a9fd70c2bad1eb`, tree
`7bfd3d4de60e463269e814269eede1356fb15791`. Авторитетные raw-derived числа и
hashes: [attempt2 evidence](evidence/nplus1-r4-run40250966619-attempt2.json).
Attempt1 failed lifetime trace с4943lost events сохранён отдельно; loss check
не ослаблялся, attempt2 на новом runner прошёл без изменения кода/gates.

Ниже исторические OPEN/PENDING относятся к названным checkpoints. Current status
имеет приоритет. Native window averages включают отдельно измеренный control
noise; Size/NewSize не складываются в net live bytes. Exact Rust allocator calls,
Web/network heap attribution и Host-wide Arc count не заявляются и не становятся
новыми обязательными gates. Hardware/AddIn/UI/distribution/cutover остаются
отдельными release gates.

Этот отчёт привязан к validated code checkpoint. После публикации итогового
documentation head обязательный CI должен пройти ещё раз; его успех здесь заранее
не заявляется. PR готов к завершению после этого final-head CI; merge не заявлен.


Проверен software scope N+1 v1.7 на локальном source
`767ab65b98dc834c5487188b9d8102c1bb4a9c8a`, tree
`7bfd3d4de60e463269e814269eede1356fb15791`: readiness follow-up,
ETW driver/schema/cleanup и representative workload. Прочитаны `rust/AGENTS.md`,
README, SOURCE_AUDIT, API_SPEC, NPLUS1_CONSUMERS_PLAN, tracker и R4 evidence.
Это независимое review исходников и доступные локальные проверки; финальный
Windows CI code tree подтверждён actual attempt2 SUCCESS; итоговый
documentation-head CI проверяется после публикации, как указано выше.

## Архитектура и инварианты

- `apps/host/src/acquisition.rs`: один physical source owner, период200ms,
  пропущенные слоты не догоняются; protocol consumers получают read-only capability.
  Producer COM time не управляет schedule и не становится LAN/RAW timestamp.
- `crates/icem-core/src/publication.rs`: единственный non-Clone publisher,
  `Arc<Snapshot>` и latest cell. Captured изменяет generation/capture time;
  Retained/error сохраняют последний успешный payload. Reader clone не копирует body;
  conversion/network/COM не выполняются под publication lock.
- `apps/host/src/main.rs`: MQTT/UA/DA получают `snapshot_reader()` при composition
  registration. Новый consumer требует свой module/config и регистрацию, без
  protocol branch в reader/publication/Modbus/Web. Writable Modbus bank остаётся
  отдельным projection owner; запросы не читают MMF самостоятельно.
- `apps/host/src/consumers.rs`: bounded registry, cancel всех entries до ожидания,
  один shared stop deadline. Tests сохраняют проверку slow consumer/source outage,
  изменяющихся LAN/RAW при fixed COM и удержания старого immutable frame.
- `Box<[u16]>` и typed source projections сохранены. Постоянный `Vec<Value>`/
  `Vec<Variable>`, новый unsafe/dependency или CPU-specific dispatcher не добавлены.
  Измеренного основания для manual SIMD нет; safe scalar/compiler path допустимо
  завершает этот пункт плана. Optional P8 dictionary не активирован.

В просмотренном scope нового нарушения этих границ не найдено. Review не является
доказательством отсутствия любых дефектов всех adapters или production parity.
Последние исправления затрагивают только измерительный Python driver/tests/docs;
.NET/AddIn, frozen protobuf, web assets и production Rust этим follow-up не менялись.

## Readiness follow-up

`Acquisition::start` подтверждает создание source до первого capture; initial
`main::refresh` может увидеть absence, а следующий mirror refresh назначен позже.
Поэтому TCP listener и fixed500ms sleep не доказывают готовность Modbus bank.
Это подтверждённая кодом fixture race. В run40194159508 actual offending COM bits
не записаны, поэтому нулевой initial mirror как причина именно того failure
остаётся inference, а не установленным runtime фактом.

`tools/perf/windows_live_load.py` теперь ждёт expected nonzero COM через FC03
**до** measurement под тем же общим10s startup deadline. Только initial zero
считается pending; wrong nonzero, late valid reply и timeout отклоняются. Connect,
send и каждый fragmented recv используют оставшийся budget. В measured12s window
zero/invalid COM по-прежнему fail; частоты/проверки LAN/RAW/IEEE bits не ослаблены.
Errors сохраняют actual/expected; Modbus wrapper также записывает rate и sample
index. Web `check_values` сохраняет actual/expected без rate/sample wrapper. Test Host duration30s
вмещает10s startup и16s external sampling; measured workload остаётся12s.

Regression checks: zero→valid, zero timeout, wrong nonzero, zero fixture rejection,
late-valid deadline, fragmented receive budget и measured-zero failure PASS.
Actual run40250966619 attempt1 подтвердил live PASS:15initial-zero reads,
16-я попытка expected COM,0.781s до readiness. Это подтверждает readiness window
этого run; причина старого unlogged значения остаётся inference. Attempt1 CI
остался failed: process-lifetime heap step потерял4943events. Attempt2 прошёл
этот gate и весь CI; failure сохранён в R4 report.

## Измерительная атрибуция и cleanup

`windows_release_measure --heap-window` выполняет100warmup операций и WARM/WARMUP
до READY. START/DONE ограничивают attach window; RELEASE/teardown идут после stop.
Capture workload использует настоящий `WindowsBusReader` + publication + latest;
request workload — production `RegisterBank::process` FC03/100words с response
allocation/drop. Это representative codec request, без network/Web heap claim.

`windows_heap_window.py` использует уникальные owned heap session и WPR process
metadata instance. Process metadata начинается до child, merge разрешает actual PID.
На ошибке выполняются bounded owned child/session cleanup; child launch/cleanup
fault tests подтверждают stop того же WPR instance и сохранение summary. Foreign
recordings не останавливаются; неизвестный inventory/schema/PID/loss fail closed.
Parser отдельно считает HeapAlloc Size и HeapRealloc NewSize bytes; original heap,
original process и merged ETL требуют Events Lost=Buffers Lost=0.

Ранее найденный R5 cleanup defect в `windows_heap.py` исправлен: отказ удаления
собственной временной копии EXE попадает в `cleanup_errors`, сохраняет OPEN summary
и ненулевой exit. Regression `test_locked_executable_preserves_summary_error_path`
PASS. Новый подтверждённый code defect в текущем review не найден.

Actual run40194159508 уже подтвердил PID-resolved/loss-free warmed windows и оба
heap steps. Zero-operation controls содержат10HeapAlloc events/7697Size bytes;
их не вычитают как точный noise. Capture10000ops:50010events/30127697Size bytes.
FC03 10000ops:70010events/5077697Size bytes и60000HeapRealloc/5040000NewSize bytes.
Это inclusive native event window averages, не доказанное число вызовов Rust
GlobalAlloc и не net live bytes. Числа/source/PID/hashes сохранены в
[evidence checkpoint](evidence/nplus1-r4-run40194159508.json).

Эти границы и отдельно записанный control noise достаточны для scoped software
allocation measurement плана; требование идеального нулевого control не вводится.
Web/network heap attribution, exact Rust allocator counts и универсальный
Host-wide Arc bound остаются явными ограничениями evidence. Они не превращаются
в новые обязательные условия закрытия текущего software scope.

## Матрица evidence и окончательное решение

| Gate | Проверенное evidence | Статус окончательного дерева |
|---|---|---|
| Архитектура N+1 / source ownership / timestamps | Сверка acquisition, publication, composition и bounded lifecycle; новых нарушений не найдено | Review PASS |
| Capture/decode/publication и mutex hold | run40250966619 attempt2: capture+publication p95=48.2µs,p99=92.5µs; hold upper p95=5.3µs,p99=6.4µs | Actual attempt2 PASS на validated tree7bfd; documentation-head CI далее |
| Capture/request native allocations и bytes | run40250966619 attempt2 оба heap steps PASS; actual PID, original/merged loss=0, separate controls и Size/NewSize | Actual attempt2 PASS на validated tree7bfd; documentation-head CI далее |
| 5Hz/multi-rate/slow clients, Modbus/Web latency, CPU/memory | run40250966619 attempt2 live PASS:60captures/peak4 в separate probe,17slow cycles,32CPU/memory samples; earlier live FAIL сохранён | Actual attempt2 live PASS, readiness подтверждён; documentation-head CI далее |
| Rust debug/SDK/interop/scaffold | run40250966619: SDK37, sidecar11, STA4096/40reconnects, MTA, MQTT/UA, scaffold PASS | Actual attempt2 SUCCESS (включая carried-forward jobs) |
| Legacy baseline/MSI packaging | run40250966619 PASS; исходники/gates не ослаблены | Actual attempt2 SUCCESS (включая carried-forward jobs) |
| Hardware/AddIn/UI/distribution/cutover | Software writer, file-backed MMF и CI VM не доказывают эти свойства | Отдельные release gates, вне закрытия R4/R5 software PR |

Live fixture проверяет fixed COM, изменяющиеся coherent LAN/RAW, RAW−0/subnormal,
частоты Modbus2/50Hz и Web1/20Hz, incomplete/disconnected clients. Она не доказывает
outbound socket backpressure, physical AddIn contention или production SLA.
Weak-handle probe измеряет максимум в своей явно ограниченной topology, а process
sampler — cumulative CPU/working-set/private bytes. Они не заявляют universal
Host-wide live Snapshot cardinality или allocator live-byte attribution.

Локально на reviewed source:28perf Python tests и46CI Python tests PASS.
`git diff --check` PASS. Cargo/rustfmt/PowerShell/.NET и Windows runtime в этой среде
недоступны; их новые успешные исполнения не заявляются. Обязательный Release job
не имеет continue-on-error; независимые runtime steps сохраняют доступные artifacts
после ошибки другого шага, общий job при этом остаётся failed.

Итог независимой сверки: code review PASS, confirmed unresolved software defect
не найден; **R4/R5 software closure PASS** на validated tree7bfd
по actual attempt2 SUCCESS всех обязательных jobs. Actual run/source/tree/artifact и live summary привязаны
к tracker/report; final documentation-head CI остаётся обязательным перед
завершением PR. Успех предшествующего checkpoint не равен
финальному CI. Production replacement, hardware/UI/SDK-distribution acceptance
или cutover не объявляются вследствие закрытия этого software PR.
