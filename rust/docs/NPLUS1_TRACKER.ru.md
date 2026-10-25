# Трекер выполнения плана N+1 v1.7

Обновлено: 25 октября 2026. Публикация: [PR43](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/43).
Исполнение: последовательно GPT-6.1 Sol, effort medium. Это трекер данного
N+1-плана, а не всей миграции .NET → Rust.

## Восемь этапов исходного плана

| Этап | Фактическое состояние |
|---|---|
| P1. Типизированный массив MMF, var identity | Реализован. Canonical Box<[u16]>, borrowed/lazy typed projections; index не равен register address |
| P2. Общая публикация и read-only handles | Реализован. Arc sharing, coherent status, retained snapshots |
| P3. Единственный acquisition 200 мс | Реализован. Consumer requests не запускают MMF read |
| P4. Modbus/Web и измерения | Реализован; Linux Release baseline есть. Windows Release measurements остаются R4 ниже |
| P5. Lifecycle и общий harness | Реализован; bounded stop, собственные worker/process handles |
| P6. MQTT | Реализован; real Mosquitto interoperability на Linux. Windows acceptance — R3 |
| P7. OPC UA, затем OPC DA | Реализованы оба adapter; Linux UA client и Windows DA in-process tests подтверждены. RPC/Windows client gates — R2/R3 |
| P8. Необязательный словарь | Не активирован: практической потребности нет; базовая работа от словаря не зависит |

## Оставшиеся программные блоки: 3

| ID | Работа | Состояние и критерий закрытия |
|---|---|---|
| R1 | Ранний допуск DA AsyncRead/Refresh | Закрыт: финальные три COM-regressions и все SDK37/37 + sidecar11/11 прошли Windows run40170843310. Весь run cancelled при следующем checkpoint, это не full green |
| R2 | DA STA/MTA и callbacks между процессами | Закрыт: mandatory Windows SDK step SUCCESS на remote75a1934, run40171770730/job121104122819. Matching MIDL proxy, Rust MTA + independent C++ STA4096,40 reconnects и owned cleanup; full CI этого head ещё не заявлен |
| R3 | Windows MQTT/OPC UA interoperability | Запустить реальные клиенты/broker в целевой ОС; подтвердить типы, identity, quality/time, disconnect/reconnect |
| R4 | Windows Release measurements | Capture/decode/publication, MMF lock hold, allocations и Modbus/Web p95/p99 с описанной методикой; не выдавать CI fixture за production hardware |
| R5 | Итоговая интеграция | Независимая сверка плана, полный CI окончательного head, актуальный отчёт и завершение PR |

R1: SDK-owned preparation + retained/canceled requests используют общий лимит32
на группу до decode/service/output. RAII возвращает credits при error/all-failed/
unwind; late Advise не получает незарезервированный completion. Это не предел
COM/RPC unmarshalling до SDK entrypoint и не обещание whole-process RSS.

## Подтверждённые контрольные точки

- [Run40129798356](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/40129798356):
  полный CI SUCCESS на remote `62cea77db6d219c630fac156b43678363b55259f`
  (production R6 checkpoint local `ea91101`). Rust, routing, baseline, packaging,
  legacy и общий gate успешны. Этот результат не распространяется автоматически
  на последующие изменения.
- Независимое callback review исправило пять HIGH findings. Windows runtime
  pre-R6 head: SDK27/27 и sidecar11/11, включая все пять regressions, PASS.
- Финальный R6 local `6fa8dc9`: queue12/health3/codec5, Python44, original42
  provenance, fmt/diff-check и Windows all-target Clippy PASS. Actual Windows runtime всех трёх fixtures подтверждён: [run40170843310/job121101210357](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/40170843310/job/121101210357),
  SDK37/37 + sidecar11/11 PASS, 06:59:56UTC. Весь run cancelled после
  SDK gate при публикации R2; full CI этого head не заявляется.
- R2: [run40171770730/job121104122819](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/40171770730/job/121104122819),
  remote `75a19342905c270efd431e77519badbaeff7ee0c`, tree
  `cef5a5f18e1e2ee90f73ddee37ee0289ff7490bf` (local `692dee3`).
  Обязательный Windows SDK step COMPLETED SUCCESS, 07:14:26–07:15:19UTC.
  Точный cache/command contract требует MIDL/CL proxy build, ignored Rust RPC test,
  independent owner-STA client4096/40 reconnects и успешный finally cleanup.
  Это actual Windows runtime gate; full job/общий окончательный CI — R5.
- Массив/SIMD: [измерения и решение](NPLUS1_PERFORMANCE.ru.md).
  Постоянный Vec<Value>/Vec<Variable> не создаётся. Ручной SIMD не добавлен:
  значимый end-to-end выигрыш не доказан; unsafe policy сохранена.

Подробности: [журнал](NPLUS1_EXECUTION.ru.md),
[native review](P7_CALLBACK_REVIEW.ru.md), [DA profile](OPCDA_P7B.ru.md).

Аппаратная приёмка реального стенда/AddIn, production cutover и разрешение
distribution terms являются отдельными release gates. Их нельзя закрыть
синтетическим CI или объявить выполненными по факту компиляции.
