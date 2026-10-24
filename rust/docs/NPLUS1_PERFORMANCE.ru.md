# N+1 P4: локальные Release-измерения

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

Windows Release MMF copy/lock-hold, allocations and latency distributions on supported hardware remain OPEN. Linux measurements do not close those acceptance items. Reproducible local P4 measurements are complete; production performance is not declared proven.
