# Выполнение Rust N+1, план v1.7

Дата начала: 24 октября 2026. Рабочая ветка `rust/nplus1-plan-v1.7`,
исходная база `origin/docs/nplus1-acquisition-200ms-20261024`.
Это журнал реализации, а не обещание полной production parity.

| Этап | Фактический статус |
|---|---|
| 1. Типизированный MMF / identity / geometry | Реализован; проверки ниже |
| 2. Published snapshot и read-only handles | Реализован; allocator profile открыт, см. ниже |
| 3. Единственный acquisition 200 мс | Не начат |
| 4. Modbus/Web и измерения | Не начат |
| 5. Consumer lifecycle / harness | Не начат |
| 6. MQTT | Не начат |
| 7. OPC UA / OPC DA sidecar | Не начат |
| 8. Необязательный словарь | Не начат; подключается при практической потребности |

## Этап 1: контракт проекций

Class: read-only проекции уже валидированного Bus v5 в
`crates/icem-bus/src/views.rs`; acquisition lifecycle и mutable Modbus mirror вне класса.
Inv: типы, адреса, биты и исходные writer metadata неизменны;
`crates/icem-bus/tests/projections.rs` проверяет полное покрытие тела и time/raw-bit границы.
N+1: новый adapter читает `Snapshot::variables/region/service`, не меняя decoder;
geometry regression исчерпывающе покрывает каждый регистр минимального,
нечётного, default и максимального допустимого размера.

`Snapshot` сохраняет существующий owned `Box<[u16]>`; проекции не создают
копий регионов, словаря и строк на каждый tick. `RegionView` заимствует тот же
срез; `ServiceView` представляет body 0..7, а не MMF header или новую зону.
Физические producer-зоны остаются COM 4..199, LAN 200..399, RAW 400..599.
Header reserve 49..56 и остальные байты reserved header не получают назначения.

В текущем Rust/SDK/legacy поиск не обнаружил существующего правила canonical
`var…`: старые фасады опираются на register addresses и необязательные имена
registry. Для подтверждённого нового класса адаптеров принято явное правило
`var{register_address}`: десятичный номер первого 16-bit регистра без padding,
начиная с `var0`. Это новое adapter identity, а не изменение старых API names.
Variable index — позиция проекции: index 5 соответствует `var6`, index 102 —
`var200`, index 302 — `var600`. Inactive slots не удаляются и не перенумеровываются.

| Диапазон адресов | Тип проекции | Геометрия |
|---|---|---|
| 0..3 | u16 `Word` | По одному значению на регистр |
| 4..5 | i32 `ComUnixSeconds` | Одна CDAB пара, `var4` |
| 6..599 | IEEE float32 | CDAB пары с чётного адреса |
| 600..N-1 | u16 `Word` | Каждый reserved регистр, включая нечётный хвост |

Reserve не объявляется float32 без source-контракта. Значения IEEE non-finite,
NaN payload, subnormal и signed zero сохраняются через `Value::raw_bits`.
`PartialEq` у float values — обычное IEEE сравнение; для определения изменения
источника использовать тип/identity и raw bits, не float equality.

COM time — отдельный i32 из 4..5; ноль является Unix epoch. `ServiceView`
не вычисляет availability или общее measurement time. `ZoneMetadata` продолжает
читать writer UTC ticks/active/count без локальных часов и подмены COM временем.
COM/LAN metadata отражают изменившиеся пары; RAW metadata может обновляться при
одинаковой строке. Counters wrapping u32, не общий producer frame sequence.
Перечитывание одинаковых bytes не создаёт metadata events; inactive values сохранены.

Source evidence: `Burservis.iCem.DataMonitor/BusLayout.cs` (layout/types),
`PollPairPreparation.cs` и `DataMonitorAddressMap.cs` (COM Time & Date),
`BusWriter.cs` (pair-change policy / RAW line / zone writes).
Immutable source-manifest и разрешённый legacy ledger не меняются.
Regression fixtures имитируют writer events; они не запускают настоящий AddIn.

### Проверки и границы

Точный toolchain 1.99.1 (rustc `58cf8f9c3`, 2026-10-13), rustfmt и Clippy
установлены; pin/workspace rust-version не менялись. Выполнены:

- `cargo fetch --locked`: PASS для полного неизменённого lockfile.
- `cargo fmt --all --check`: PASS.
- `cargo check -p icem-bus --all-targets --locked --offline`: PASS.
- `cargo build -p icem-bus --locked --offline`: PASS.
- `cargo test --locked -p icem-bus`: PASS, 5 существующих + 5 новых tests, 0 failures.
- `cargo clippy -p icem-bus --all-targets --locked --offline -- -D warnings`: PASS.
- `git diff --check`: PASS; изменения ограничены `rust/crates/icem-bus` и этим журналом.

Toolchain находится вне git repository. Для следующих этапов в этой среде:
`PATH=/workspace/scratch/a4cb7fc84f23/toolchain/cargo/bin:$PATH`,
`RUSTUP_HOME=/workspace/scratch/a4cb7fc84f23/toolchain/rustup`,
`CARGO_HOME=/workspace/scratch/a4cb7fc84f23/toolchain/cargo`.

Windows runtime/MMF, SDK, hardware, визуальная и production acceptance этим
этапом не доказываются. Полный workspace gate относится к интеграции следующих
этапов; здесь проверен изменённый dependency-free crate на Linux.

## Этап 2: shared publication (до реализации)

Class: immutable latest publication в `crates/icem-core/src/publication.rs`, один
non-Clone writer и read-only handles; MMF polling/worker и legacy D09 facade вне класса.
Inv: один Arc<Snapshot> на успешный capture, coherent status/version/time, initial
absence, удержание last success при failure/Retained; integration tests проверяют
concurrency, pointer identity, slow readers, monotonic expiry и освобождение старых frames.
N+1: adapter получает clone reader без source access; следующий acquisition owner
меняет только composition, не cell/consumers. Exhaustive origin match охраняет
Captured/Retained; пример измеряет payload bytes/constructions и Weak live frames,
не выдавая их за allocator-wide counts или zero allocations.


### Результат P2

Добавлен `icem-core::publication`: один non-Clone `SnapshotPublisher`, cloneable
`SnapshotReader` без source/writer capability и `Arc<Mutex<PublishedSnapshot>>`.
Cell содержит ровно одно latest state; `latest()` под кратким lock копирует только
state/Arc handle. Протоколы, source polling и 200 ms worker здесь не подключались.
Physical `SnapshotRead` оставлен совместимым; новых dependencies/unsafe нет.

До первой успешной физической копии `snapshot=None`, generation=0 и capture time
отсутствует. `Captured` переносит owned Snapshot в Arc; даже одинаковые bytes
увеличивают **локальную capture generation**, не producer update ID. `Retained`
(включая legacy Fresh) и `Err` сохраняют именно последний успешный Arc/generation/
capture time; retained input не заменяет эти данные. Latest attempt status/error и
completion time публикуются согласованно. D09 Fresh/LastGood не становится общей
health policy: legacy Freshness здесь не вычисляется и не экспортируется.

Injected `Instant` отличает successful capture от completion последнего attempt;
`capture_age` не обращается к wall clock/COM time и возвращает None для отсутствия
или backwards observation. Invalid ordering отвергается до мутации. Zone metadata,
COM int32 time и raw bits остаются внутри исходного Snapshot. Этот механизм не
выводит source freshness/health из факта очередного capture.

Пять integration tests покрывают absence/первую ошибку/первый retained, failure и
Fresh-retained без освежения successful capture, повтор идентичных bytes, backwards
clocks без частичной мутации, sharing pointer/body, неизменность старого кадра,
освобождение replaced frames без backlog, coherent status/version/bytes при четырёх
параллельных readers. Два compile-fail doctests охраняют non-Clone writer и отсутствие
publish у reader. Сам cell не ограничивает число ссылок, которые внешние consumers
решат хранить: их pending/history budgets — отдельный lifecycle этап, не скрытая
глобальная гарантия P2. Пример и тест удерживают максимум один старый кадр.

### Измерения P2 и проверки

Команда: `cargo run -p icem-core --example publication_measure --release --locked --offline`.
Linux x86_64, Intel Xeon Platinum 8573C, exact Rust 1.99.1, workspace Release
(opt-level=s, thin LTO, один codegen unit), без дополнительных target flags.
Fixture 650 registers, 10 000 decode/publications, 8 sequential reader handles,
80 000 consumer reads, один reader держит первый Arc до конца. Это tight-loop
ownership/accounting workload, без warm-up, MMF, worker cadence и сети; один run,
не latency/throughput acceptance или Windows baseline.

- Измеренный requested register payload: 13 000 000 bytes, 10 000 constructed bodies.
- Weak accounting: peak 2 одновременно живых Snapshot / 2600 bytes register bodies.
- Все 80 000 reader results имеют ту же Arc pointer identity, что latest publication.
- Single run elapsed: 8580 microseconds; process-wide Linux VmHWM: 1072 kB
  (включает code/runtime/harness, не только register bodies).
- Allocator-wide allocation counts и allocated bytes: **NOT_MEASURED**. В среде нет
  valgrind/heaptrack/ltrace/perf/bpftrace; unsafe allocator hooks и dependency ради
  счётчика не добавлялись. Payload construction/bytes не выдаются за allocator
  statistics: Arc control blocks, allocator metadata и harness сюда не включены.
  Пример пригоден для внешнего heap profiler на целевой машине. Zero allocations
  и полный allocation baseline не заявляются; allocator profile остаётся открыт.

На exact 1.99.1 выполнены PASS:
`cargo fmt --all --check`,
`cargo check -p icem-core --all-targets --locked --offline`,
`cargo build -p icem-core --locked --offline`,
`cargo test -p icem-core --locked --offline` (5 integration + 2 compile-fail),
`cargo clippy -p icem-core --all-targets --locked --offline -- -D warnings`,
Release example и `git diff --check`.

Изменения только в `rust/crates/icem-core` и этом журнале; legacy, proto, assets,
lockfile и toolchain неизменны. P3 (единственный acquisition 200 ms), consumer wiring,
Windows/MMF/hardware acceptance и полноценный allocator/latency profile остаются
отдельными открытыми этапами. Push/PR не выполнялись.
