# Выполнение Rust N+1, план v1.7

Дата начала: 24 октября 2026. Рабочая ветка `rust/nplus1-plan-v1.7`,
исходная база `origin/docs/nplus1-acquisition-200ms-20261024`.
Это журнал реализации, а не обещание полной production parity.

| Этап | Фактический статус |
|---|---|
| 1. Типизированный MMF / identity / geometry | Реализован; проверки ниже |
| 2. Published snapshot и read-only handles | Реализован; Linux allocator profile добавлен в отчёте P4 |
| 3. Единственный acquisition 200 мс | Реализован; Windows/runtime acceptance отдельно |
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

## Этап 3: единственный acquisition (до реализации)

Class: `apps/host/src/acquisition.rs` владеет физическим source на отдельном потоке;
Web/health/mirror получают latest publication; protocol callbacks вне acquisition.
Inv: один read на due tick 200 ms, без catch-up storm, consumer reads не трогают
source; injected schedule/counting tests проверяют cancellation, slow reader и ошибки.
N+1: новый consumer получает read-only latest handle; D09 metadata остаётся Host
compatibility bridge. Shutdown имеет явный deadline; native read не прерывается
небезопасно, deadline failure запрещает restart/создание второго owner.

### Результат P3 и проверки

Host создаёт ровно один dedicated worker `bus-acquisition`. Через поток передаётся
Send factory с путями/именами; factory создаёт `Box<dyn SnapshotSource>` на самом
worker, без требования Send к source и без пересылки native handles/guard.
Единственная физическая `source.snapshot()` теперь в worker; mirror, health и
Web requests читают latest state. Нет consumer callbacks или очереди кадров.
`Schedule` начинает сразу, далее использует 200 ms; пропущенные слоты не догоняются.
Если read занял 500 ms, 5 Hz физически недостижимы: max latency 200 ms не обещается.

Core publication сохраняется без изменений. После snapshot() берётся monotonic
completion как консервативная локальная capture observation; Retained/Err не
обновляют время последней успешной копии. Host-only compatibility metadata
(freshness/is_open/error) и publication читаются согласованно под кратким lock;
I/O и обработка consumers проходят вне lock. D09 Fresh-retained сохранён только
для прежнего BusReply facade; общий capture status/time этим не освежается.
Временный bridge пока копирует payload в owned BusReply. Устранение этих копий,
а также измерения CPU/allocations/latency относятся к P4, не заявлены как готовые.

Condvar отменяет idle wait немедленно. `stop` ждёт явный deadline 2 s; ошибка
запрещает нормальный restart Host. In-flight native read безопасно не прерывается;
его source и handles остаются на worker до выхода. Drop даёт такой же bounded
wait только если явный stop ещё не предпринимался, не умножая истёкший deadline.
Factory failure передаётся при startup; источника до первой удачной копии нет.

На exact 1.99.1 Linux выполнены PASS:
`cargo fmt --all --check`,
`cargo check -p icem-host-app --all-targets --locked --offline`,
`cargo build -p icem-host-app --locked --offline`,
`cargo test -p icem-host-app --locked --offline` (8 tests: 4 прежних и 4 acquisition),
`cargo clippy -p icem-host-app --all-targets --locked --offline -- -D warnings`.
Тесты проверяют injected schedule с пропуском slow-read слотов; !Send source,
500 consumer reads без request-driven capture; удержание Arc медленным reader;
первую ошибку/первый Retained/absence; сохранение capture time/generation при
Retained и source error; cancel при idle/in-flight read и bounded deadline;
startup factory error. Linux process smoke с fixture, ephemeral Modbus port и
700 ms duration завершился exit 0. `git diff --check`: PASS. Windows-only integration tests на Linux
имеют 0 cases; MMF/native guard и Windows runtime данным этапом не доказаны.
Native reader остаётся прежним: owned copy под mutex, decode после release.
Legacy/proto/assets/UI/dependencies/lockfile не менялись; push не выполнялся.

## Этап 4: общие Modbus/Web handles (до реализации)

Class: внутренний SnapshotRead и Host/Web read-only projections; writable Modbus
mirror остаётся независимым массивом, wire/API и observation-time history вне изменений.
Inv: один Arc payload на физический capture, никакого deep clone при replies/store/
last-good; retained/error сохраняют last success, silence 5 s определяется checked
monotonic age последнего attempt, не COM time. Pointer/isolation/expiry tests обязательны.
N+1: новый consumer клонирует handle, а новый source обязан вернуть Arc-owned
snapshot; тип поля и compile всех constructors охраняют полноту. Source acquisition,
health 1 Hz и history не вызываются HTTP requests. SIMD/performance evidence отдельно.

### Результат P4: ownership и policy

`SnapshotRead.snapshot` теперь `Arc<Snapshot>`: Windows reader после decode создаёт
один Arc и сохраняет clone handle в last_good; publication принимает этот же Arc.
Retained, Host reply, Web Store::last и request projections клонируют handles, а
не Box<[u16]>. Modbus по-прежнему копирует разрешённые диапазоны в отдельное
writable зеркало; FC06/16 никогда не меняют publication или Windows cache.
Fixture source хранит один immutable decoded Arc; это synthetic fixture, не
измерение физического copy/decode. `publication_measure` продолжает decode + Arc
на каждой итерации и подходит для отдельного allocator/capture kernel profile.

Reader возвращает последний успешный shared payload также при очередном source
Err, с LastGood; до первого success payload отсутствует. D09 Fresh-retained
сохранён только в accepted Modbus/Web facade: core CaptureStatus остаётся Retained,
generation/captured_at не обновляются. История по-прежнему берёт local observation
UTC на независимом probe 1 Hz, а не COM int32 или time отдельного HTTP request.
Source health/header cadence и bounded history 600 samples прежние. Requests
вызывают observe (handle only), не probe и не physical source.

Добавлена явная acquisition-silence policy: age последнего completed attempt
через `Instant::checked_duration_since`, строго меньше 5 s — текущий attempt.
На границе 5 s, при отсутствии completion или backwards clock facade LastGood;
данные остаются доступны. Число 5 s выбрано равным существующему окну zone
liveness (`icem-bus::ZoneMetadata::is_live`), но это отдельный локальный clock и
оно не вычисляет producer liveness. Регулярный Retained/Err не обновляет время
успешного capture; D09 нельзя использовать как универсальный freshness сигнал.
Общий reader и дальше предоставляет capture_age/status для следующих adapters.

Следствие принятого плана v1.7: frozen protobuf comment о read-at-call больше
не описывает физический I/O Host; ответ берёт latest acquisition. При исправном
5 Hz acquisition обычный age ограничен периодом ~200 ms плюс source/scheduling
work; строгий максимум 200 ms не обещается. Wire shapes и observation timestamps
сохранены; cache возраст имеет отдельную silence границу выше. Ни monotonic
completion, ни local UTC не подменяют COM Time & Date или zone writer metadata.

### Проверки P4

На exact Rust 1.99.1 Linux PASS:

- `cargo fmt --all --check`.
- `cargo check --workspace --all-targets --locked --offline`.
- `cargo build -p icem-host-app -p icem-core -p icem-web -p icem-protocols --locked --offline`.
- `cargo test -p icem-core -p icem-host-app -p icem-web -p icem-protocols --locked --offline`:
  core 6 integration + 2 compile-fail, Host 9, protocols 13, Web 34; 0 failures.
- `cargo clippy -p icem-core -p icem-host-app -p icem-web -p icem-protocols -p icem-platform-windows --all-targets --locked --offline -- -D warnings`.
- `git diff --check`.

Meaningful regressions: 500 replies share pointer without driving source;
error/retained retain last success, checked 5 s boundary/backwards age; independent
LAN/RAW metadata/bits progress with fixed COM time; FC03/04 preserve NaN payload,
infinity, negative zero and subnormal bits; FC06/16 mutate mirror only, stale hold
and fresh overwrite intact. Existing exhaustive zone gate tests remain PASS.
1000 Web observations preserve the exact Arc and do not advance health time,
header stagnation counter or history. Slow held publication remains immutable.

Initial test link encountered stale incremental `Arc<Snapshot>::deref` symbol;
cleaning only affected packages fixed it, then all final gates above passed.
Windows-only tests have 0 cases on Linux: actual MMF mutex/copy time, last-good
runtime, hardware and Windows integration remain unproven. No dependencies,
lockfile, unsafe policy, legacy, frozen proto, web assets or UI were changed.
Release baseline/network/allocator/codegen measurements are recorded separately;
this ownership change does not itself establish a numeric performance win.

## Этап 5: lifecycle потребителей (до реализации)

Class: Host-owned independently started Modbus/Web servers and future typed adapters;
legacy LayerKind plans/sniffer and physical source ownership are near nonmembers.
Inv: ConsumerId is unique and bounded, cancellation precedes deadline wait, actual
socket ownership is controlled by registered handles; tests bind/stop real listeners,
failed startup, duplicate registration and independent publication during slow/faulted stop.
N+1: add adapter module + typed config/factory + composition registration only;
reader/publication/other adapters remain unchanged. Trait implementation and registry
capacity/duplicate rejection provide executable completeness/resource guards.

### Результат P5

Host `consumers.rs` содержит ConsumerId, максимум 16 уникальных регистраций,
Lifecycle (actual status/cancel/stop) и небольшой ServerHandle для действующих
server owners. Это lifecycle-only registry: source, преобразование данных,
protocol dispatch, retry, очередей и дополнительных workers в нём нет.
`modbus_consumer.rs` и `web_consumer.rs` имеют собственные typed Config/factory;
composition root регистрирует их handles. Modbus facade использует тот же shared
Option<ModbusServer>, поэтому SetLayer продолжает legacy toggle и actual socket
projection. Web toggle остаётся next-start планом SPA/history, OPC DA — no-op.

Modbus/Web servers предоставляют request_stop/is_running/stop_until. Shutdown
сначала отменяет все consumer owners, затем ждёт единый общий deadline 2 s.
Timeout сохраняет тот же entry/server для повторного ожидания, не создаёт новый
worker; fault не запрещает отмену остальных. После consumer drain acquisition
тоже получает stop даже при ошибке drain; ошибка запрещает нормальный restart.
Обычный Drop ограничен 2 s и не повторяет истёкшее ожидание. Это cooperative
thread cancellation: по deadline безопасно отсоединяется оставшийся поток с
его собственными ресурсами, а не насильно убивается. Deadline не real-time SLA.
Web readiness ограничен 2 s; Modbus bind/spawn синхронно сообщает start failure.
Existing Modbus connection/accept bounds и Web bounded streams остаются прежними;
новый harness не добавляет I/O buffers и capture payload copies.

`Acquisition::snapshot_reader` выдаёт только core SnapshotReader. Состояние там —
последний completed attempt, не watchdog: следующий adapter должен явно проверять
monotonic capture/completion age, например выбранный budget 5 s; D09 facade
Fresh не используется как универсальная health. После смерти worker прежний
CaptureStatus сам не меняется. COM time, writer metadata и local time не смешаны.
P6 требует новый module + собственный typed Config/factory + регистрацию в Host;
reader, публикация, Modbus/Web modules и core protocols менять не требуется.

Exact Rust 1.99.1 Linux: fmt, scoped all-target check, build, tests и clippy -D warnings
(--locked --offline) PASS для icem-host-app/icem-transport/icem-web.
Host 12 tests (9 previous + 3 lifecycle), transport 14, Web 34; 0 failures.
Real harness запускает оба действующих TCP servers, проверяет occupied-port start
failures, actual running state, остановку Modbus при работающем Web, cancel/drain,
повторный stop и освобождение обоих портов. Registry проверяет capacity/duplicates,
cancel-all перед slow wait, один общий deadline и сохранённые entries после timeout.
Slow/failing harness не останавливает acquisition; core reader видит изменение
raw bits LAN/RAW с фиксированными COM bits, а source outage сохраняет последний
Arc и позволяет последующим failed attempts продвигать completion time.

Linux не доказывает Windows/MMF/native runtime и hardware acceptance. Новых
dependencies/unsafe, frozen API/proto, legacy, web assets и UI изменений нет.
Linux process smoke: fixture + ephemeral Modbus/Web listeners + duration 700 ms,
actual registry diagnostics Running/Running, clean exit 0. Shared server slots are
Host-owner control capabilities only; network/acquisition workers never lock them.

## Дополнение оркестратора: P4 measurements и представление массива

[Отчёт NPLUS1_PERFORMANCE.ru.md](NPLUS1_PERFORMANCE.ru.md) и raw results
`docs/perf/nplus1-p4-linux.json` фиксируют независимые frozen-worktree Release
сравнения до/после на 650 и10001 registers, decode/publication workload,
реальный внешний Valgrind heap profile и generated-code inspection.
Установленный отдельно profiler измерил20020 total-process allocations /
13887876 allocated bytes,0 detected memory errors/definitely lost bytes;
это уточняет прежнее P2 NOT_MEASURED для Linux, не закрывает Windows profile.

Уточнение пользователя про массив/SIMD: данные хранятся одним непрерывным
`Box<[u16]>` внутри общего `Arc<Snapshot>`; Service/Region и typed variable
iterator — проекции, не набор var1..varN fields. Canonical storage не становится
`Vec<f32>`/`Vec<Value>`, тип COM int32 не теряется. Ручной SIMD не добавлен: объём
пользы на полном пути не доказан; исследованный byte-swap loop скалярный.

Benchmark harness проверен реальными loopback Modbus/gRPC-Web ответами baseline
`7360b19` и P4`bba4516`; это synthetic fixture/Linux, не Windows MMF/hardware.
