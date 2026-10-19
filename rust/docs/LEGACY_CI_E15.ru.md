# Legacy CI: согласованный snapshot в retry tests

Разрешение владельца: 2026-10-19, дополнительно исправить Legacy CI и продолжать merge.
Run 39189512965: Transport SerialTeeServiceTests.TeeLoop_VirtualPortMissing_RetriesConnection
получил STARTING вместо PAIR_MISSING. Production catch выставляет PAIR_MISSING,
ожидает 5 s, finally возвращает STARTING перед следующим watchdog cycle. Тест
успешно дождался PAIR_MISSING, но после await взял новый snapshot. Планировщик
может возобновить continuation уже в другой фазе. Зафиксирована гонка наблюдения;
production COM tee не активируется и retry delays/semantics не меняются.

Class: bounded polling tests (`SerialTeeServiceTests.cs`), не production lifecycle.
Inv: assertion использует именно snapshot, который удовлетворил predicate; regression
сразу переводит источник PAIR_MISSING → STARTING перед assertion.
N+1: generic WaitUntilAsync(observe,predicate) используется transient PAIR_MISSING и
обоими PORT_BUSY checks; bool polls используют тот же deadline/poll implementation.

10 s deadline, 20 ms poll, все исходные assertions и finally StopAsync сохраняются.
Не увеличен timeout и не принимается STARTING как эквивалент ошибки. Текущий
snapshot по-прежнему проверяет Com0comPairOk/PhysicalPortBusy совместно с State.
Baseline source-manifest неизменён; существующее точное исключение test-файла
обновляется новым hash и source-fix commit provenance. Guards не ослаблены.

Exact-head Windows .NET tests/MSI и Rust baseline guard обязательны. Старый successful
main run не заменяет новый результат. Полный Rust/Legacy outcome фиксируется в PR #11.

## Дополнительный cold-run failure 39191869780

SerialTee 84/84 PASS; обнаружены ещё два независимых timing assumptions:

- MetricsSampler Deltas ожидал full как первое доставленное сообщение, хотя канал
  capacity=1/DropOldest может потерять его до запуска consumer. Delta assertions
  теперь выполняются только при DroppedCount=0; при зарегистрированной потере
  новая подписка начинается в общем 15 s deadline. Промежуточные idle samples
  допускаются до heartbeat mutation; ожидаемый changed sample по-прежнему обязан
  содержать ровно heartbeat и float pair. Оба слова float и heartbeat меняются
  одной реальной MMF transaction. Full=302 и все typed/value/unchanged assertions
  сохранены. Producer всегда отменяется и join имеет deadline. Отдельный overflow
  test продолжает проверять DropOldest и retained capacity=1. Production не меняется.
- HealthCheck MongoDown отменял read через 4 s, включая первый timer tick, cold
  ThreadPool и OS process/service enumeration. Реальный HealthEvent теперь ожидается
  в стандартном для async tests бюджете 15 s; timeout остаётся FAIL, исключение
  cancellation не подавляется. DiscoverAsync обязан быть вызван, DbOk=false остаётся
  exact assertion. Stop/join перенесён в finally с 10 s deadline, service disposed.

Это ограниченное изменение test observation/lifecycle, не production scheduling,
не отключение parallel tests и не автоматический retry CI. Baseline ledger получает
точные новые hashes для MetricsSamplerTests и Core ContractTests; frozen surfaces
и baseline manifest не меняются. Следующий Windows run обязан проверить оба suite.
