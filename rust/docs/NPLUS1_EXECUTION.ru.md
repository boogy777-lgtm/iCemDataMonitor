# Выполнение Rust N+1, план v1.7

Дата начала: 24 октября 2026. Рабочая ветка `rust/nplus1-plan-v1.7`,
исходная база `origin/docs/nplus1-acquisition-200ms-20261024`.
Это журнал реализации, а не обещание полной production parity.

| Этап | Фактический статус |
|---|---|
| 1. Типизированный MMF / identity / geometry | Реализован; проверки ниже |
| 2. Published snapshot и read-only handles | Реализован; Linux allocator profile добавлен в отчёте P4 |
| 3. Единственный acquisition 200 мс | Реализован; Windows/runtime acceptance отдельно |
| 4. Modbus/Web и измерения | Реализован; Linux Release/perf отчёт ниже |
| 5. Consumer lifecycle / harness | Реализован; scoped Linux checks ниже |
| 6. MQTT | Реализован; Linux checks и real Mosquitto interop ниже; Windows OPEN |
| 7. OPC UA / OPC DA sidecar | P7a UA implemented, Linux SDK interop; P7b DA pending; Windows OPEN |
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

## Этап 6: MQTT (до реализации)

Class: независимый MQTT 3.1.1 state publisher, Host `mqtt_consumer/`; command subscriptions, durable event journal и MMF writer — вне класса.
Inv: только read-only SnapshotReader; full typed state, исходные bits/COM/zone provenance, capture/completion age и явный status; один latest pending и один protocol in-flight. Loopback wire tests проверяют reconnect, outage, coalescing, deadlines, quality и сохранение clocks.
N+1: только новый module + validated typed Config + Host CLI/composition registration; reader/core/Modbus/Web не меняются. Lifecycle trait/registry — executable completeness guard, runtime TCP отказ изолирован от acquisition.

### Dependency review до добавления

Выбран `rumqttc = =0.25.1`, `default-features=false`, для реального MQTT 3.1.1 TCP codec, CONNACK/PUBLISH/PUBACK/PUBREC/PUBREL/PUBCOMP, keepalive и bounded async event loop. Apache-2.0; published manifest MSRV 1.64.0, ниже pinned workspace 1.99.1. Источники: https://docs.rs/crate/rumqttc/0.25.1/source/Cargo.toml и https://docs.rs/rumqttc/0.25.1/rumqttc/struct.EventLoop.html ; локально проверен опубликованный crate 0.25.1, его src не содержит unsafe. Transitive selected dependencies используют уже принятые workspace Tokio/bytes/flume/futures/thiserror/fixedbitset; platform/runtime unsafe остаётся внутри external dependencies, application forbid не ослабляется. После resolution lockfile/delta проверяются отдельно.

TLS/rustls/native-tls, WebSocket, proxy, URL features не включены: первый scope — plain TCP к explicit numeric socket address, без DNS, credentials и управляющих subscriptions. serde derive/serde_json — существующая workspace serialization family; Tokio теперь явная cross-platform зависимость Host (`rt,time,macros`), worker имеет свой current-thread runtime. Официальный EventLoop документирует pending на reconnect; здесь clean-session owner пересоздаётся при connection error, поэтому не накапливает старые QoS payloads. QoS относится к текущему соединению, не гарантирует журнал промежуточных состояний между reconnect.

### Результат P6

Новый `mqtt_consumer/{config,payload,mod,tests}.rs` + optional `--mqtt-config FILE` и регистрация `mqtt` в Host. Изменения shared contracts, acquisition, Modbus/Web отсутствуют. Подробные config/schema/delivery/lifecycle границы: [MQTT_P6](MQTT_P6.ru.md).

Полный typed payload лениво сериализуется из shared canonical snapshot, ID через serde collect_str без временного String на variable. COM raw int32, IEEE754 raw bits/NaN/−0 и per-zone writer metadata сохранены. Явные capture status, source error, capture/completion age и acquisition-only quality обновляются независимо от generation; D09 Fresh не применяется. Не назначается LAN/RAW measurement time.

Latest pending ограничен одним Arc, serialized queued/in-flight — одним request; channel1, inflight1, payload≤16MiB, inbound≤1024B. При disconnect новый clean-session owner коалесцирует latest state, не replay history. QoS0/1/2/retain реальные; shutdown cancellable по deadline, status отражает CONNACK/offline/cancel/worker completion. Нет event journal/command subscriptions/TLS/auth/DNS/Will.

Dependency delta lock: только rumqttc0.25.1 (checksum зафиксирован), остальные выбранные packages уже в workspace lock. [Manifest/license review](../migration/mqtt-dependencies.json); new application unsafe нет, pin1.99.1 неизменён.

Exact Rust1.99.1 Linux: Host19 tests PASS (12 прежних +7 новых), fmt/check/build/clippy all-targets с --locked --offline PASS. Wire tests покрывают QoS0/1/2, reconnect500 versions, failure без смены generation, fixed COM/zone clocks, absence/retained/expiry/raw bits/types, offline/slow ACK/unresponsive CONNACK/deadline,2000 pending replacements/max65535 layout и independent actual acquisition+Modbus.

Отдельный real Mosquitto2.0.18 process smoke PASS: QoS2 subscribe, broker restart, generation1→3, retained generation20 после Host exit0/retain flag1, source clocks/bits не меняются. [Evidence](perf/nplus1-p6-mosquitto-linux.json), reusable `tools/acceptance/nplus1_mqtt_broker.py`. Linux fixture не заменяет Windows/MMF/hardware/security acceptance; эти gates OPEN.

## Этап 7a: OPC UA — UA ready, DA pending

Class: read-only OPC UA consumer в `apps/host/src/opcua_consumer/`; OPC DA,
MMF acquisition, core publication и legacy protocols вне класса.
Inv: stable typed NodeIds, atomic full-frame model, honest acquisition-only quality
и неизвестное measurement time; actual SDK client/read/subscription tests + lifecycle checks.
N+1: новый adapter получает только SnapshotReader и одну composition registration;
strict optional config, existing publication/registry tests guard against source coupling.

Dependency decision: FreeOpcUa async-opcua =0.19.0, MPL-2.0, `server` only
(default features empty); `client` only for tests. Includes generated namespace;
XML/JSON/discovery-registration disabled. Edition2021; upstream declares no MSRV,
exact pinned Rust1.99.1 compile is the acceptance check. Published sources of
server/types/crypto contain no unsafe blocks (comment mentioning unsafe excluded);
transitive runtime/crypto libraries retain their own unsafe implementation boundary,
application forbid(unsafe_code) remains unchanged. SDK sources are unmodified.
Sources: https://github.com/FreeOpcUa/async-opcua/releases/tag/0.19.0 and
https://docs.rs/crate/async-opcua/0.19.0; Cargo.lock to pin full resolved closure.

Class (acceptance harness): independent asyncua client → real Host UA wire;
synthetic source fixture не является Windows MMF/hardware стендом.
Inv: external browse/types/bits/timestamps/read-only/subscription evidence; executable
`tools/acceptance/nplus1_opcua_client.py`, owned subprocesses/temporary files only.
N+1: новые server profiles/adapters требуют собственного client acceptance gate;
existing gate не разрешает silent remote security assumptions.

P7a implemented: optional strict `--opcua-config FILE`, real OPC UA SDK server,
read-only typed stable identities, atomic complete Bus model, monotonic acquisition
quality + explicit producer-active mapping, raw source time/metadata без придуманных
SourceTimestamp. [Полный контракт/limits/audit](OPCUA_P7A.ru.md).
Linux exact1.99.1: Host22/22, три новых UA tests (actual SDK TCP client) PASS;
fmt/check/build/test/clippy all-targets `-D warnings`, locked/offline PASS.
Independent Python asyncua2.1.0 client functional gate PASS (evidence linked above).
Это P7 partial: UA ready в описанном loopback test profile; OPC DA sidecar7b pending,
Windows clients/MMF/runtime/packaging/hardware и secure/remote profile OPEN.

## Этап 7b: OPC DA synchronous read sidecar

Class: Windows local COM read-only snapshot consumer, отдельный owned child process;
legacy LayerKind::OpcDa no-op, MMF acquisition, core и остальные adapters вне класса.
Inv: deterministic versioned IPC≤256KiB, one in-flight frame, own-child kill/reap
watchdog и deadline lifecycle; native raw types/IDs и writer FILETIME проверяются
codec/process/safeSDK-client tests. Никаких MMF handles и handwritten unsafe.
N+1: новый protocol добавляет только module/config/composition registration;
codec общий ровно для двух концов IPC, reader/publication остаются прежними.

Реализованы optional `--opc-da-config FILE`, lifecycle entry `opc-da`, отдельный
`icem-opc-da-sidecar` и `icem-opc-da-codec`. SafeSDK pinned git revision, полные
role/features/licenses/MSRV/unsafe/lock audit и scope: [OPCDA_P7B](OPCDA_P7B.ru.md),
[machine-readable audit](../migration/opc-da-dependencies.json).
Старый OPC toggle остаётся no-op. Runtime COM class factory реальная; explicit
register/unregister трогают только собственные per-user CLSID/ProgID mappings.
Read-only synchronous profile имеет groups/items/read; browse/callbacks/DA3,
proxy/stubs installation, DCOM и production packaging не объявлены реализованными.

Canonical storage — raw Box<[u16]>+Arc. Stable var{address}, U16/I32/F32 raw bits,
COM int32, acquisition status/ages и producer active сохранены. Sample FILETIME —
только writer metadata соответствующей зоны с .NET→1601 offset, неизвестный0;
COMvalue/now не подставляются. Capture age продолжает монотонно расти; final review ниже уточняет IPC transit.

ExactRust1.99.1 Linux: Host26/26 и codec3/3 PASS, scoped check/build/test/clippy
locked/offline, fmt и diff-check PASS. Windows x64 target codec+sidecar all-target
check/clippy PASS; real COM unit/client tests только compile-checked. Отдельный
opt-in cross-process CoCreateInstance gate подготовлен и требует installedmatching
OPC Foundation proxy/stubs. Фактическая Windows/MMF/hardware/client/registration
приёмка OPEN; P7 целиком не закрыт одним Linux/compile evidence.

## Windows CI follow-up после P7b

Run `40081559349`, Rust job `120836375639`: workspace build/check PASS,
Host 20/23 PASS. Три failures относятся к portability test fixtures:

- `acquisition::tests::silence_expiry_is_checked_monotonic_and_does_not_erase_last_success`
  конструировал backward Instant через `at - 1ns`. Windows QPC не обязан
  представлять такой шаг: округление оставляет `at`, и корректный facade Fresh
  расходится с ожидаемым LastGood. Теперь шаг 1ms, обе стороны strict expiry
  и backward ordering проверены assertions; exact 5s boundary сохранена.
- Два MQTT wire tests читали `read_exact` после accept от nonblocking listener.
  Windows accepted socket наследует nonblocking; ошибка 10035 WouldBlock
  возникала в fixture packet reader. Общий helper явно задаёт blocking mode
  до неизменённых 3s read/write timeouts. Другой nonblocking accept в Host
  tests только удерживает молчащее соединение без read/write и исправления
  не требует. Production acquisition/MQTT и silence limit не менялись.

Отдельный legacy job `120836375621`: падение
`TickLoop_ActiveZoneWrite_OverwrittenEveryCycle_NoDeltaDetection`, line920,
expected `0x1111`, actual `0xBEEF` после исходного 2s polling budget.
Call graph проверен: TestServer initial CaptureOnce, затем actual
BusAcquisitionService с period200ms и publisher200ms; Current помечает snapshot
stale после period+MutexTimeoutMs =700ms. TickOnce сохраняет внешнюю запись
при stale, а при fresh+active копирует всю зону. Последнее observed значение
не отличает отсутствие publisher ticks от stale capture publication или
ошибки capture. ThreadPool scheduling delay — гипотеза, не установленная
причина: failure output не содержит TickCount/TickExceptionCount,
acquisition sequence/completion age/error. Ранее этот тест прошёл в run
`40081000067`; это не опровергает текущий failure. .NET SDK/Windows execution
в этой проверке отсутствуют. Legacy test exact-hash approved в
`migration/legacy-divergences.json`; файл, production .NET и approval ledger
не менялись. Открытая отдельная граница требует Windows reproduction с
перечисленными diagnostics перед выбором исправления; timeout/assertions
не ослаблены и passing legacy CI здесь не объявлен.

После fixture fixes exact Rust1.99.1 Linux: Host26/26 PASS,
fmt all/check, scoped Host all-target check, build, test и clippy `-D warnings`
с `--locked --offline`, `git diff --check` PASS. Эти результаты не заменяют
повторное actual Windows выполнение трёх исправленных tests и legacy suite.

## P7b final review follow-up

[OPCDA_P7B final review](OPCDA_P7B.ru.md) фиксирует исправление P1 stale→GOOD
при blocked IPC: receiver credit Instant до READY/предыдущегоACK даёт
консервативную верхнюю границу возраста с idle/transit/decode, без UTC clock.
Исправлены noncommitting ValidateItems при полном quota, DEVICE/item-active
и invalid writerDateTime; sidecar добавлен в Windows payload inventory.
Group-active/SetState и плановые callbacks остаются точными P2 safe SDK blockers;
P7 PARTIAL. Профиль sync-read не заменяет обязательные COM groups/callbacks
из плана§7. Никакого speculative native fork/unsafe exception не добавлено.

Local final-review evidence: exact1.99.1 codec5/5, Host26/26, routing/cache42/42
PASS; fmt/check, Linux codec clippy and Windows sidecar+codec all-target
check/clippy locked/offline PASS. Windows runtime/service/packaging tests remain
pending; no actual COM/MMF/hardware result is inferred from cross compilation.

## P7b upstream safe SDK feasibility, 2026-10-24

Read-first проверка [OPCDA_P7B](OPCDA_P7B.ru.md) повторно сверила live upstream
HEAD/master, все девять branch heads, tags/releases/open PR и реальные safe API.
Default ref по-прежнему `da44a99265f9706618bbe506c98f714f2aa46f0b` (наш pin),
tags нет, releases/open PR пусты. `refactor/safe-opc-bindings` и
`ci/opc-classic-integration` не добавляют ни coherent state hook, ни DA callbacks.
Safe `client::DaGroup::state()` существует, но service не получает эту capability,
public safe lookup client-created group отсутствует; polling не даёт atomic
ordering с SetState/Read и не реализует callbacks.

Старые server/connection-point branches проверены отдельно: ABI-shaped traits,
safe raw-pointer→arbitrary-lifetime helpers и отсутствие проверенного owned sink
marshalling/bounded lifecycle не позволяют принять их как safe SDK. Другие
найденные Rust проекты являются DA clients/gateways; experimental .NET SDK не
предоставляет готовой Rust границы. Таблица exact refs, первичные source links,
минимальные upstream extension requirements и шесть Windows acceptance cases
сохранены в OPCDA_P7B. Это ограниченный feasibility вывод, не доказательство
отсутствия всех возможных SDK.

Итог: без нового audited upstream safe API снять текущий P7 blocker нельзя.
Такое API может быть принято без ослабления application/shared forbid(unsafe),
но сегодня готовой capability не найдено. P7 PARTIAL и требования плана1.7
сохранены; native fork, новые unsafe exceptions, dependencies/lockfile и
production code не менялись. Проверка doc-only: `git diff --check` PASS;
Windows compilation/runtime и CI остаются отдельными evidence.

## Windows live Modbus startup fixture follow-up

Class: asynchronous Windows `.NET Producer → MMF → Host → Modbus` fixture
`apps/host/tests/live_modbus.rs`; source-factory/listener readiness не является
data readiness и не входит в этот класс assertions.
Inv: initial `0x1234` и subsequent `0xABCD` подтверждаются полным точным FC03/04
wire response в отдельных bounded waits; FC06 echo, inactive LAN hold и exit0
остаются обязательными. Requests не приобретают physical-source capability.
N+1: общий fixture helper обслуживает оба producer states; malformed response
shape/function/byte count отклоняются немедленно, deadline сохраняет exact bits
в assertion diagnostics. Следующий state использует тот же helper.

Run `40091548303`, Windows Rust job `120865341020`: workspace check/build и
Host unit tests PASS; live Modbus initial assertion line65 получил
`[1,3,2,0,0]` вместо `[1,3,2,18,52]`, subsequent refresh ещё не выполнялся.
Проверен production call graph: `Acquisition::start` подтверждает factory до
первого `acquire`; `main::refresh` читает только publication и передаёт
`MirrorInput::Unavailable`, если snapshot отсутствует. `RegisterBank::new`
создаёт нулевое зеркало, `Unavailable` сохраняет data words; периодический
`main` refresh затем применяет доступный snapshot. Никакой пропущенной
subscription/attachment здесь нет. План1.7 §4/§8 допускает initial absence;
API_SPEC §2 явно задаёт mirror behavior без snapshot. Existing
`initial_absence_and_cancellation_interrupt_wait_not_native_call` executable
test проверяет отсутствие snapshot во время первого blocked capture.

Listener message теперь служит только обнаружению endpoint. Первый FC03 ждёт
реального `0x1234` тем же helper, что subsequent FC04 ждёт `0xABCD`.
Прежний 2s update budget не увеличен; initial stage получает такой же budget.
Fixture Host lifetime выведен как `2 × MIRROR_WAIT + 1s = 5s`, чтобы два
ожидания и финальные write/hold assertions не пересекли planned exit3s.
Production startup, acquisition200ms, mirror period и register semantics
не менялись. Проверены остальные Windows fixtures с `Producer::start`:
они напрямую вызывают synchronous `WindowsBusReader::snapshot`, без ошибочного
переноса listener readiness на asynchronous first publication; правок не требуют.

Local exact Rust1.99.1: Linux Host26/26 PASS; scoped Host all-target
check/clippy `-D warnings`, build, fmt-check и `git diff --check` PASS,
`--locked --offline`. Linux не запускает Windows-only live fixture.
Попытки Host Windows x64 all-target Cargo check и clippy блокированы до
application code: dependency `ring0.17.14` требует отсутствующий `lib.exe`
(MSVC native toolchain; available GNU compiler unsupported). Эти gates
не объявляются PASS. Отдельные `rustc --test --emit metadata` и
`clippy-driver --test --emit metadata -D warnings` для изменённого
`live_modbus.rs` вместе с его std-only Windows support на target
`x86_64-pc-windows-msvc` PASS. Это проверка типов/lints fixture, без linking,
запуска Host/.NET/MMF или замены полного Windows CI runtime gate.

## P7c1 — dynamic coherent DA group state, 2026-10-24

Class: SDK committed owned group-state → sidecar synchronous Read; callbacks,
core/acquisition и IPC clocks вне класса. Inv: один coherent tuple на Read,
CACHE учитывает group+item active, DEVICE сохраняет producer quality; Windows
SDK/client tests плюс compile-time mandatory trait argument. N+1: P7c2 callbacks
используют тот же committed state; безопасные service implementations не могут
молча пропустить новое состояние. Полный P7 остаётся PARTIAL.

Вместо повторного doc-only поиска реализован минимальный local reviewed SDK
extension на прежнем pin `da44a99265f9706618bbe506c98f714f2aa46f0b`:
`third-party/rust_opc`, пять dependency crates вне application workspace,
42 exact upstream inputs/415895 bytes, LICENSE/NOTICE/winmd сохранены.
MSRV upstream1.97 отделён от selected patch exact1.99.1; features/registry
versions unchanged. Cargo.lock меняет только пять git source identities на
local reviewed inputs. Exact SHA256 manifest и reversible patch verifier
проверяют полный closure без cache edits/CARGO_HOME substitution. Build
генерирует bindings/module wrappers в OUT_DIR; tracked files immutable.

Обязательный owned GroupState передаётся в `DaGroupService::read` через новый
`forbid(unsafe_code)` helper с clone-under-lock/release-before-service.
Read linearizes при clone; existing SetState commit/error/output ordering
и ABI/unsafe operations не меняются. Service CACHE теперь учитывает active
group+item, DEVICE сохраняет source quality; rate/client handle coherent и
не управляют producer200ms. Новых unsafe операций/ABI/exception нет;
application forbid/native ADR0001 unchanged. Подробности: [P7c1](OPCDA_P7B.ru.md).

Новые safe SDK tests вызывают existing SetState implementation entrypoint,
затем настоящий safe COM client Read/GetState: active false→true→false,
revised rate/client handle, failure-before-commit и 2000 concurrent read/state
tuples. Это **не** внешние COM SetState calls; safe upstream wrapper отсутствует,
и новая unsafe операция ради теста не добавлялась. Sidecar COM test покрывает
AddGroup(false), CACHE bad, DEVICE good/retained producer quality; существующие
item capacity/activity/lease checks сохранены. NEXT P7c2: callbacks/owned sinks,
async/advise/unadvise/cancel/marshalling и Windows cross-process acceptance.

Exact Rust1.99.1: fetch locked; затем Linux Host26/26+codec5/5, sidecar build,
scoped Linux all-target clippy `-D warnings`, Windows x64 sidecar+codec+SDK
all-target check/clippy, fmt-all/check и diff-check PASS, locked/offline.
Windows runtime здесь не запускался; root CI должен явно запускать dependency
SDK tests вместе с sidecar tests (они не application workspace members).
Provenance verifier, exact pin git-object SHA256 сверка и metadata nonmember
guard PASS; SHA256 всех vendor files неизменны после forced bindings regeneration
и actual Windows compile. Legacy/.NET/AddIn/proto/assets и другие adapters
не изменены; cross-process/client/MMF/hardware/release gates остаются OPEN.

## P7 SDK provenance: Windows newline follow-up

Class: temporary upstream reconstruction (`third-party/rust_opc/verify.py`),
не SDK runtime. Inv: exact upstream hashes/patch и mutation rejection сохраняются
при Windows Git newline settings; `tools/ci/test_vendor.py` исполняет проверки.
N+1: новые SDK deltas проходят тот же verifier и existing CI unittest discovery.

Run `40098148886` падает до debug restore с SHA256 mismatch `Cargo.toml`.
Исходный verifier на Linux воспроизвёл тот же отказ при `core.autocrlf=true`:
внешняя temporary reconstruction теряет checkout `* -text`, reverse apply
вставляет CRLF. Исправление задаёт command-scoped autocrlf=false/eol=lf и
vendor attributes для обеих apply commands. Manifest/patch, SDK Rust sources,
lockfile и application unsafe/lint policy неизменны. Причина, fixture и границы:
[P7_SDK_CI](P7_SDK_CI.ru.md). Actual Windows CI/runtime остаются pending.

Linux: CI unittest discovery44/44, verifier default и autocrlf=true/eol=crlf,
cache command contract, exact1.99.1 fmt-all/check и diff-check PASS. Новых
dependencies/unsafe, изменений frozen .NET/AddIn/proto или SDK patch/hashes нет.


### P7c2 R6 early SDK admission — 2026-10-24

Class: heavy AsyncIO2 Read/Refresh2 SDK-owned preparation; periodic/readonly Write
outside this fix. Inv: preparation+retained/canceled <=32/group, fixed all-sink
credits before decode/service/output; RAII rollback and no service/COM under locks.
N+1: callback_queue Members/Preparation, both entrypoints and deterministic pure
regressions; reversible patch/42-original SHA256 remain mandatory completeness guard.
Checkpoint: queue11/11 and full provenance PASS; Windows compile/runtime pending.
This bounds SDK-owned preparation only, not pre-entrypoint COM/RPC unmarshalling
or whole-process RSS. Existing P7 Windows/proxy/operator gates remain OPEN.
