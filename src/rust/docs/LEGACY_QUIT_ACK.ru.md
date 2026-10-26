# Legacy quit ACK: исправление после E9

Пользователь отдельно разрешил исправление legacy CI и слияние PR #5.
В run 39068801769 exit=1 относится к emulator suite: один failure из 101,
ControlPipeServerTests.Dispose_DrainsInFlightReply_QuitAckReachesClient получил
пустую строку вместо JSON ACK. Следующий Host suite успешно прошёл 226/226;
это не означает успешность всего solution test run.

CI установил SDK 10.0.402 и runtime 10.0.13. global.json запрашивает 10.0.302
с latestFeature: новый feature band разрешён этой политикой. Лог не показывает
runtime crash/несовместимость версий. Версии не менялись для маскировки ошибки.

Class: draining command handlers в iCemEmule.Core/Control/ControlPipeServer.cs;
idle/watch cancellation — соседний lifecycle-путь, не отдельный ACK-протокол.
Inv: drain task опубликована до executor; shutdown-test сигналами задаёт порядок
и проверяет настоящий JSON ACK; idle shutdown ограничен deadline.
N+1: все команды используют общий registered-before-execute путь; проверка
ActiveHandlerCount внутри executor защищает следующий command case от того же дефекта.

В production устранён запуск Task.Run до регистрации _handlers: быстрая команда
могла вызвать quit, когда Dispose ещё не видел её task. Handler теперь сначала
ожидает registration gate с asynchronous continuations, регистрируется, затем
допускается к исполнению. Wire/JSON/quit semantics, drain budget 1.5 s и accept-loop
join budget 1 s сохранены; бесконечный WaitForPipeDrain не добавлен.

У старого теста была отдельная timing-зависимость: blocking Dispose на ThreadPool
и Task.Delay(200) не гарантируют, что release-continuation выполнится до drain deadline.
Новый тест запускает blocking Dispose на dedicated LongRunning task, ждёт реальный
cancellation signal и освобождает ACK без искусственной задержки. Текстовая/JSON
строки проверяются, ACK assertions не ослаблены. Finally всегда освобождает executor
и наблюдает завершение Dispose; idle-client test проверяет конечный drain.

Это две найденные гонки; один CI trace не позволяет установить, какая именно
interleaving произошла на том runner. Ошибка версий .NET не подтверждена.

Original source-manifest не меняется. Два новых exact-hash исключения в ledger
ссылаются на source-fix commit c3788065909d8e60c4b45cabc38a93bdfbe0e783 PR #5;
guard и frozen proto/assets/SPA не ослаблены.
Локально SDK 10.0.402 успешно скомпилировал tests; выполнение named pipes в Linux
заблокировано SocketException(13) при создании Unix socket. Это ограничение среды,
не Windows verdict. Финальный Windows .NET tests/MSI и Rust CI фиксируются в PR.
