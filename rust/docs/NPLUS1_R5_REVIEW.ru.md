# R5: независимая интеграционная сверка, 25 октября 2026

Проверено локальное дерево после `acabb06` (R4 heap follow-up), план N+1 v1.7,
`AGENTS.md`, README, SOURCE_AUDIT и API_SPEC. Это review исходников и доступных
Python checks; Windows execution и полный CI окончательного дерева не подменяются
этой записью. R5 остаётся OPEN до окончательных R4 evidence и CI.

## Архитектурная сверка

- `apps/host/src/acquisition.rs`: единственный source owner, период 200ms,
  пропущенные слоты не догоняются. Requests получают read-only capability;
  producer COM time не управляет расписанием и не становится LAN/RAW timestamp.
- `crates/icem-core/src/publication.rs`: sole non-Clone publisher,
  `Arc<Snapshot>` и latest cell. Retained/error не заменяют последний успешный
  payload и не освежают capture generation/time. Reader clone не копирует body.
- `apps/host/src/main.rs`: MQTT/UA/DA получают `snapshot_reader()` только при
  composition registration. Их добавление не вводит protocol branch в reader,
  publication, Modbus или Web. Modbus writable mirror остаётся отдельным bank.
- `apps/host/src/consumers.rs`: bounded registry и общий stop deadline; cancel
  вызывается для всех entries перед ожиданием. Существующие tests проверяют slow
  consumer при изменениях LAN/RAW и fixed COM, source outage и held old frame.
- Постоянный `Vec<Value>`/`Vec<Variable>`, ручной SIMD, новая dependency или unsafe
  exception не добавлены. P8 dictionary по принятому решению не активирован.
  CPU-specific fallback proof требуется только при будущем accelerated path.

Нового нарушения этих границ в просмотренном scope не найдено. Это целевая
сверка, не доказательство отсутствия любых дефектов всех adapters.

## Исправление review

Class: удаление собственного временного benchmark EXE в
`tools/perf/windows_heap.py`; чужие registry/session objects вне класса.
Inv: отказ cleanup сохраняет OPEN summary и ненулевой exit; проверяется
`test_locked_executable_preserves_summary_error_path`.
N+1: следующий owned cleanup добавляет ошибку в тот же accumulator, без изменений
production source/consumers.

Ранее `exe.unlink()` мог выбросить исключение из `finally` до записи
`heap-summary.json`, скрыв доступные evidence и заявленный OPEN status.
Теперь OSError сохраняется в `cleanup_errors`, и report записывается обычным
путём. Эта коррекция не делает недоступный output volume записываемым.

## R4: что ещё требуется

| Gate | Текущий scope | Остаток |
|---|---|---|
| Actual Windows Release evidence | Первый latency job121159818060 SUCCESS на предшествующем tree5fabda19; heap там отсутствовал | Успешные actual artifacts, parser/toolkit compatibility и raw data окончательного дерева |
| Capture/decode/publication, lock hold | Static650-word physical file-backed MMF, uncontended tight loop; upper-bound hold | Дополнить общей software нагрузкой 5Hz, а не объявлять microbenchmark полным end-to-end |
| Modbus/Web p95/p99 | Три раунда, два persistent клиента, synthetic fixture Host | Live-reader software fixture, разные частоты клиентов, slow/disconnected client, LAN update при fixed COM; проверить ответы и clock/raw bits |
| Allocations и allocated bytes на capture/request | WPR/xperf PID process-lifetime totals, KB precision; startup control без точного вычитания | Изолированная атрибуция capture/request либо явный OPEN; aggregate process delta не выдавать за точный результат |
| CPU/time и память | Wall-clock raw samples; CPU identity | CPU-time и peak live snapshots/memory при описанной нагрузке; latest-cell bound не равен whole-process RSS bound |
| Production hardware | CI VM, safe fixture | Live AddIn/стенд, реальная producer contention и поддерживаемое оборудование — отдельная внешняя приёмка |

Первые пять строк имеют доступную software часть и не должны целиком переноситься
в «hardware only». Для расширения fixture уже имеются `--bus-live`, `--bus-map`,
`--bus-mutex`; safe writer может менять file под тем же `NamedMutex` без production
instrumentation или новой unsafe boundary. Ручной SIMD не нужен для закрытия этих
измерений: измерения могут обосновать сохранение простого scalar пути.

## CI и итоговые gates

`rust-migration.yml` вызывает reusable Release measurements обязательным job без
`continue-on-error`; cold GPUI build имеет отдельный budget50min. Существующие
scaffold, SDK37/37, sidecar11/11, Rust MTA, independent C++ STA4096/40 reconnects,
Mosquitto/asyncua и packaging gates не удалены и не ослаблены.

Локально: heap validation7/7, CI Python46/46 и `git diff --check` PASS. Rust/cargo, PowerShell
и .NET в этой среде недоступны; новая Windows compilation/runtime не заявляется.
R1–R3 evidence остаётся в tracker с exact source/run. Полный CI нужно привязать к
окончательному опубликованному tree, а не только к предшествующим checkpoints.

Legacy Modbus test ранее дважды падал на expected1111/actualBEEF; последующий run
40190328583 прошёл те же legacy tests без изменения их исходников. Вариативность
подтверждена, причина не установлена. Frozen .NET/AddIn/proto/assets не исправлялись,
gate не отключался; окончательный статус всего run проверяется отдельно.

Первый Release checkpoint (сообщён оркестратором): run40190328583,
job121159818060 SUCCESS, source9dd0b2cfdf3e392f88871c4a09a6c317c29ce64f,
CI merge b2cd8a98db7c2545a79bcc287a417b543985ba99,
tree5fabda19fbefdb64ac27d9b8cda346e6b3e9f202. Artifact12596859538
`windows-release-measurements`, digest
3b5c0549eb6ee0c961a157e36a460f89084c822cc0a3658a4220e423720dd5f7.
Это предшествует исправлению timed-region checks и heap follow-up;
`reader_publication_total` старого harness включает correctness checks.
Heap/per-capture allocations и окончательный R4/R5 им не закрываются.

PR остаётся draft до R4/R5. Рекомендуемый title:
`Rust N+1 v1.7: shared acquisition and MQTT/OPC consumers; Windows validation`.
Body должен описывать один acquisition200ms, typed shared snapshots, adapter
lifecycle, R1–R3 actual Windows fixture evidence и R4 measured/OPEN scope с точным
финальным CI head. Не объявлять production replacement, hardware/distribution
acceptance или точные per-request allocations по process heap totals.
