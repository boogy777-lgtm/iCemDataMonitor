# Трекер выполнения плана N+1 v1.7

Обновлено: 26 октября 2026. Публикация: [PR43](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/43).
Исполнение: последовательно GPT-6.1 Sol, effort medium. Это трекер данного
N+1-плана, а не всей миграции .NET → Rust.

## Восемь этапов исходного плана

| Этап | Фактическое состояние |
|---|---|
| P1. Типизированный массив MMF, var identity | Реализован. Canonical Box<[u16]>, borrowed/lazy typed projections; index не равен register address |
| P2. Общая публикация и read-only handles | Реализован. Arc sharing, coherent status, retained snapshots |
| P3. Единственный acquisition 200 мс | Реализован. Consumer requests не запускают MMF read |
| P4. Modbus/Web и измерения | Реализован; Linux Release baseline есть. Windows Release software measurements подтверждены R4 ниже |
| P5. Lifecycle и общий harness | Реализован; bounded stop, собственные worker/process handles |
| P6. MQTT | Реализован; real Mosquitto interoperability на Linux. Windows loopback acceptance R3 подтверждён ниже |
| P7. OPC UA, затем OPC DA | Реализованы оба adapter; Linux UA client и Windows DA in-process tests подтверждены. RPC/Windows client gates R2/R3 подтверждены ниже |
| P8. Необязательный словарь | Не активирован: практической потребности нет; базовая работа от словаря не зависит |

## Оставшиеся программные блоки: 0

| ID | Работа | Состояние и критерий закрытия |
|---|---|---|
| R1 | Ранний допуск DA AsyncRead/Refresh | Закрыт: финальные три COM-regressions и все SDK37/37 + sidecar11/11 прошли Windows run40170843310. Весь run cancelled при следующем checkpoint, это не full green |
| R2 | DA STA/MTA и callbacks между процессами | Закрыт: mandatory Windows SDK step SUCCESS на remote75a1934, run40171770730/job121104122819. Matching MIDL proxy, Rust MTA + independent C++ STA4096,40 reconnects и owned cleanup; full CI этого head ещё не заявлен |
| R3 | Windows MQTT/OPC UA interoperability | Закрыт для loopback fixture: run40172775150/job121107195362 SUCCESS; Mosquitto2.0.22 QoS2/restart/retained, asyncua2.1.0 read/subscription/types/quality/clocks. Live MMF/hardware отдельно |
| R4 | Windows Release measurements | Закрыт scoped software: run40250966619 attempt2 Release SUCCESS; real capture/hold, 5Hz live multi-rate/slow clients, CPU/memory, PID-resolved loss-free warmed capture/FC03 native event windows и lifetime controls. Exact operation-only/Web heap/Host-wide Arc — явные ограничения |
| R5 | Итоговая интеграция | Закрыт software: независимая сверка PASS, cleanup/readiness regressions PASS; все обязательные code-checkpoint jobs и общий gate121348687621 SUCCESS. Final documentation-head CI обязателен после публикации перед завершением PR |

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

R3 actual evidence: WindowsServer2025 10.0.26100, Python3.12.10; counterparts PASS
2026-10-25T07:46:55Z. Source442ae6b276d870306d3c94e26bcaba54533b807f,
CI merge51f0244c0a41cdc58e665cd25dd197193db09ad1 (identical tree confirmed).
Artifact12591616200 `windows-independent-counterparts`,
SHA256 e610af28e8592ab4cd9917ed18a2ec8a950a3f7735d42ba802d832111737157b.
UA browse368, UInt16/Int32/Float, NaN7fc01234/negative-zero80000000,
read-only and COM/writer times; both Host processes exit0. Same Rust job retained
SDK37/37 + sidecar11/11 and independent STA4096/40reconnects/RustMTA PASS.
Legacy .NET Modbus test failed separately; no full-workflow green claim.

R4 fixture/method/raw-artifact harness: see NPLUS1_PERFORMANCE.ru.md.
Actual Windows native counts/Size/NewSize и zero-loss evidence подтверждены attempt2. Production hardware acceptance остаётся отдельным gate.

[Независимая R5 сверка и точные оставшиеся software/hardware gates](NPLUS1_R5_REVIEW.ru.md).

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

Обязательные jobs attempt2: Rust verify121345766982, legacy121345766655,
packaging121345766192, routing121345797634, baseline121345788060 — SUCCESS;
исходно успешные jobs сохранены при rerun failed Release job. Artifact12621084111,
ZIP SHA256 `4d070c76289320e1476476190fc044b47f9479e9de12f8c97abcfb288b739f42`.
SDK37/sidecar11, STA4096/40reconnects/MTA, MQTT/UA, Scaffold и legacy/MSI gates
сохранены. Полная миграция .NET → Rust этим software PR не объявляется завершённой.
