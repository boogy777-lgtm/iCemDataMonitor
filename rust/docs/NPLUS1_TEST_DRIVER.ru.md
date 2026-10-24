# N+1: независимость lifecycle test driver от ThreadPool

Class: тесты, где синхронно блокирующий lifecycle одновременно удерживается
ManualResetEventSlim и запускается через Task.Run; обычный async I/O вне класса.
Inv: disposal действительно начинает работу, блокируется до release и завершается
до уничтожения source; все прежние assertions и пятисекундные deadlines сохранены.
N+1: blocking orchestration test использует выделенный LongRunning driver, а не
общий ThreadPool, на котором могут ждать другие tests. Проверка: существующий
ContainerDisposal test и остальные LatestBusConsumerTests.

В Windows runs 40079586191 (Rust P4) и 40079994202 (P5) неизменённый test
`LatestBusConsumerTests.ContainerDisposal_JoinsInflightAcquisitionBeforeFactoryOwnedSource_EvenWithoutHostStop`
дважды остановился на строке129: `disposing.Wait(5s)` не дождался запуска
Task.Run. Это наблюдаемый failure; исходный baseline отдельно не запускался,
поэтому не объявляется доказанной baseline failure.

ThreadPool starvation — вывод из синхронного ожидания запуска общего pool task
при удерживаемом blocking capture, а не доказательство дефекта production owner.
Driver переведён на `TaskCreationOptions.LongRunning` с TaskScheduler.Default.
Это устраняет зависимость запуска driver от свободного pool worker; проверяемый
порядок disposal и release не ослабляется. Production C#/.NET/AddIn, source
manifest, divergence approvals, timeout budgets и CI routing не меняются.

Поиск в LatestBusConsumerTests нашёл один такой Task.Run/MRES driver.
Этот test добавлен в C6 и отсутствует в pinned source-manifest/legacy ledger;
исторические файлы и hash approvals не переписываются.

Валидация: точный SDK из global.json10.0.302/runtime10.0.10, Linux;
`dotnet test iCemDataMonitor.Host.Tests/iCemDataMonitor.Host.Tests.csproj
--filter FullyQualifiedName~LatestBusConsumerTests -m:1 -nodeReuse:false
-p:UseSharedCompilation=false` — PASS4/4, failures/skips0.
Однопроцессный MSBuild выбран из-за запрета Unix-socket IPC этой среды, а не
для изменения тестовой конкурентности или production runtime.
Полная Windows-проверка выполняется CI после публикации.

В соседнем `BusAcquisitionTests` уже применяется LongRunning driver для
чтений при удерживаемом capture. Найденный `EmulatorAppTests.WaitForQuit`
использует Task.Run после подтверждённого quitACK; удерживаемого capture в
этом сценарии нет. Он отмечен как соседний кандидат, без изменения его кода
и без заявления, что его ThreadPool scheduling вообще не может задерживаться.
