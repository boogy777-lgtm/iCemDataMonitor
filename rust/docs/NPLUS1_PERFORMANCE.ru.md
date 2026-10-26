# N+1 P4: локальные Release-измерения

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


## Исторический P4 Linux baseline

Class: повторяемые измерения synthetic-fixture Host и decoder/publication; production MMF и Windows clients вне этого Linux harness.
Inv: сравниваются одни и те же fixture, pinned toolchain/profile, запросы и warm-up; сохраняются исходные результаты, ограничения и проверка ответов.
N+1: новая нагрузка добавляет сценарий в tools/perf/nplus1_network.py и отдельный result; production decoder/consumers ради benchmark не меняются.

## Метод и границы

Baseline `7360b19` и P4 `bba4516` собраны из отдельных detached worktrees на одном Linux x86_64 / Intel Xeon Platinum 8573C. Toolchain точный Rust1.99.1; профиль workspace Release (`opt-level=s`, thinLTO, codegen-units=1), без `target-cpu=native`/AVX2 flags. По три отдельных запуска, по 100 warm-up и 1000 измеренных запросов на каждый из двух одновременных persistent clients: Modbus FC03 100 words и gRPC-Web GetRegisters(all). Проверяются MBAP/function/length и успешный gRPC trailer. Часы roundtrip — `perf_counter_ns`.

Host использует synthetic fixture, не Windows MMF. В P4 fixture разделяет один immutable Arc; реальный Windows capture создаёт owned bytes и decoded snapshot. Поэтому сетевой fixture benchmark не измеряет MMF lock hold, capture или producer-to-client latency. Контейнер разделяемый, без CPU isolation. Первый 650-register candidate run частично совпал с завершением профилировочного warm-up. Разброс не позволяет заявлять статистически значимое ускорение/регрессию.

| Registers | Client | Baseline p95, мкс (min–max трёх запусков) | P4 p95, мкс | Baseline p99, мкс | P4 p99, мкс |
|---|---|---|---|---|---|
| 650 | Modbus | 2151.64–2161.57 | 2140.18–2146.92 | 2308.44–2362.30 | 2225.41–2292.16 |
| 650 | gRPC-Web | 156.30–225.61 | 181.53–212.60 | 262.43–372.38 | 242.44–275.00 |
| 10001 | Modbus | 2123.65–2138.43 | 2123.22–2172.58 | 2163.84–2300.49 | 2155.66–2895.29 |
| 10001 | gRPC-Web | 180.15–201.10 | 158.28–221.22 | 219.09–280.29 | 265.33–398.69 |

Raw results: `docs/perf/nplus1-p4-linux.json`. Reproduce from repository root (Python3 standard library; binaries built separately with exact same profile):

```text
python rust/tools/perf/nplus1_network.py --binary PATH_TO_HOST --fixture rust/tests/fixtures/bus-v5-minimal.bin --output result.json
```

10001-register fixture derives from the checked minimal fixture: header bytes8..10 little-endian count=10001; append zero body words to exact64+2*10001 bytes. Its active live ranges remain identical. It is a size workload, not actual production station evidence.

## Decode/publication и heap

`cargo run -p icem-core --example publication_measure --release --locked --offline` performs10000 decode+Arc publications and80000 shared reads. The unchanged workload includes Weak/live accounting but excludes native copy/mutex/network. After one warm-up, ten unprofiled runs took4384–7264 microseconds per whole workload. Payload ownership peak was two Snapshot bodies/2600 bytes while one old frame was held. This is bounded for that workload, not a universal limit on arbitrary external Arc retention.

External Valgrind3.22.0 Memcheck on the P4 executable measured20020 total-process allocations,20018 frees and13887876 allocated bytes. Zero definitely/possibly lost bytes and zero detected errors;548 bytes remained reachable in runtime at exit. These totals include startup, harness, formatting and control blocks, not just body allocation. No zero-allocation claim is made. Valgrind slowdown/RSS are excluded from latency results. Exact matching glibc debuginfo was used; no application dependency or unsafe allocator hook was added. Raw profiler outputs are under `docs/perf/`.

## Массив и SIMD

The canonical representation stays one contiguous `Box<[u16]>` inside shared `Arc<Snapshot>`, with borrowed typed Service/Region projections and lazy typed variable iteration. There are no generated var1..varN fields, persistent `Vec<Value>`, per-tick var-name strings or conversion of COM int32 time to float32.

Дополнительная проверка layout на Linux x86_64, exact Rust1.99.1: `size_of::<Value>()=8`, `size_of::<Variable>()=24`. Непосредственно скомпилирован текущий `icem-bus`; fixture расширен до10001 registers, `Snapshot::variable_count()` возвращает9703. Это измерение текущего compiler layout, не обещание стабильного Rust ABI.

| Представление для этого snapshot | Размер элементов, bytes | Назначение |
|---|---:|---|
| Канонический `Box<[u16]>`,10001 words | 20002 | Единственное исходное тело, общие Arc handles |
| Материализованный `Vec<Value>`,9703 values | 77624 | Дополнительный массив не требуется для ленивой проекции |
| Материализованный `Vec<Variable>`,9703 entries | 232872 | Дублирует value/index/identity, поэтому постоянно не хранится |

Таблица исключает container/Arc headers, allocator overhead, capacity slack и metadata. Значения не доказывают ускорение; они показывают стоимость лишней материализации. Slice исходных `u16` нельзя непосредственно считать slice `f32`: live values собираются из двух CDAB words, COM time имеет типi32, service/reserve —u16. Если реальная численная операция потребует SIMD, допустим отдельный ограниченный homogeneous `f32` work buffer только выбранного диапазона; это явная дополнительная работа и память, проверяемая полным benchmark, а не замена canonical storage.

Inspected generated assembly from `cargo rustc -p icem-bus --release --locked --offline -- --emit=asm` contains scalar u16 byte swapping (`rolw $8`) in the decoded-body loop. XMM copies elsewhere are not proof of vectorized byte conversion. A separate `-C opt-level=3` assembly experiment also retained scalar byte swapping in the inspected loop. This inspection is Linux library code, not Windows MSVC final-LTO proof. Source contiguity enables optimization but does not guarantee it.

No substantial numerical hot spot or end-to-end win from manual SIMD was established. The project retains the safe scalar/compiler path, generic CPU compatibility, current Release profile and unsafe policy. No SIMD dependency/intrinsics, extra array or CPU dispatcher is added. Safe loop reformulation can be evaluated later against a measured bottleneck with scalar/raw-bit/NaN/tail equivalence.

На момент этого Linux checkpoint Windows Release MMF copy/lock-hold, allocations
и latency distributions оставались OPEN. Более поздние software evidence и
окончательный PENDING status указаны в текущем статусе выше; Linux measurements
сами по себе Windows acceptance не закрывают. Production performance не объявляется доказанной.

## R4 Windows Release: воспроизводимый CI fixture

Class: benchmark-only Windows MMF copy/decode/publication и synthetic Host latency;
живой AddIn, producer contention и hardware workload не входят в этот fixture.
Inv: safe native boundary и unmodified WindowsBusReader, immutable shared payload,
полные raw CSV/network samples, pinned Release profile; проверяются bytes и Arc identity.
N+1: новый размер/нагрузка меняет только example `windows_release_measure` и
`tools/perf/windows_release.py`; production reader/consumer не получает timers/branches.

`rust-nplus1-measurements.yml` — отдельный обязательный Windows job, вызываемый из
основного Rust workflow. Он собирает настоящий Host и example в workspace Release,
не заменяя существующие debug/SDK/STA/counterpart/scaffold gates. Повтор локально из `rust/`:

```powershell
cargo fetch --locked
cargo build --release --locked --offline -p icem-host-app --bin icem-host-dev
cargo build --release --locked --offline -p icem-platform-windows --example windows_release_measure
python tools/perf/windows_release.py --output target/windows-release-evidence
```

MMF workload: static650-register fixture, уникальный Local mutex, неназванная file-backed
mapping через существующий native API; WindowsBusReader использует свой настоящий
unnamed fallback.100 warm-up +1000 recorded tight-loop iterations. CSV отдельно
хранит wait, upper-bound hold (включая release syscall/Instant), decode+Arc,
publication, boundary total, unmodified reader capture и reader+publication total.
Correctness и CSV formatting вынесены за timed region. Исходные bytes и все decoded
words сравниваются; shared handle проверяется через Arc::ptr_eq, старый frame удерживается.
Это uncontended kernel/boundary microbenchmark, не200ms production acquisition schedule.

Network: три запуска;100 warm-up +1000 measured requests на каждый из двух одновременных
persistent clients (Modbus FC03/100words, gRPC-Web GetRegisters/all). Все latency samples
хранятся в JSON; p95/p99 используют sorted samples с индексом `min(n-1, floor(n*p))`.
Host synthetic fixture и MMF microbenchmark — отдельные нагрузки; их нельзя складывать
в producer-to-client latency. Summary сохраняет git source/tree, CPU, OS, compiler,
profile и SHA256 raw files. Таймеры — Instant/perf_counter_ns; CI VM не изолирована.

Отдельный `tools/perf/windows_heap.py` запускается **после** unprofiled latency,
используя установленный Windows Performance Toolkit (`wpr.exe`/`xperf.exe`).
[Microsoft WPR method](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-for-heap-analysis):
уникальная копия benchmark EXE → owned IFEO key → `-HeapTracingConfig enable` →
`-start Heap -filemode` → workload → `-stop trace.etl` → finally disable/delete empty owned key.
Чужие IFEO key и известные WPR/HeapSession recordings не изменяются; неизвестный
`wpr -status` отвергается. Никаких downloads/dependencies/unsafe allocator hooks.

Два отдельных процесса/trace: `--heap 0` (startup/fixture/setup/checks/teardown control)
и `--heap 10000` (те же границы плюс physical WindowsBusReader capture/publication,
latest read и correctness check). Нет timing CSV/vector в heap workload.
Actual PID берётся из Popen; `xperf -i trace.etl -o totals.txt -a heap -pid PID -totals`
сохраняет native allocation/reallocation counts и allocated size в KB.
[Формат xperf](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/heap)
не обещает точности отдельного byte в печатном KB report: raw ETL сохранён для
повторного анализа. Summary включает исходную totals table, source/tree/compiler,
SHA256 EXE/ETL, commands, exits и PID; parser принимает только явные allocation
count/number + KB size columns и требует положительный capture count. Неизвестные
headers дают OPEN, числа из них не угадываются.

Это **aggregate process totals**, не изолированные allocations на capture. Startup
control не вычитается как точное измерение: разные процессы имеют runtime noise.
В эту нагрузку не входят network requests. Их heap attribution остаётся OPEN.
`xperf -a tracestats -detail` сохраняет raw provider statistics; actual loss
counters читаются из dumper trace header. Parser требует реальные нулевые
event/buffer loss counters; provider-count table их не подменяет. Неизвестный формат, потеря событий, занятый
recorder, отсутствие toolkit/privileges или ошибка cleanup дают `OPEN` + actual error
и ненулевой exit, сохраняя доступные raw files. `RECORDED_RAW_HEAP_TOTALS` означает
собранные native tables, не production performance acceptance.

Воспроизведение отдельного heap profile из elevated terminal после Release build:

```powershell
python tools/perf/windows_heap.py --output target/windows-release-evidence/heap
```

Этот первоначальный harness не измерял5Hz live acquisition, LAN updates при
frozen COM и slow/disconnected clients. Они добавлены отдельным software fixture
в R4 live-load report; текущий follow-up verdict остаётся PENDING. Live AddIn,
настоящая producer mutex contention и supported hardware остаются отдельной
production performance acceptance. Software R4/R5 не закрыты до финального CI.
