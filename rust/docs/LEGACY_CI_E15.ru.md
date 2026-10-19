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
