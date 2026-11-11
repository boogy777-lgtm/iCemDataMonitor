# TEST ONLY: граница immediate DLL custom action

`BoundaryProbe.cpp` не является guard и не входит в production MSI. Его строит
только acceptance workflow; `Test-SharedOpcRuntime.ps1` после проверки
disposable GitHub-hosted/admin и после копирования заблокированного MSI добавляет
DLL и immediate action в эту непубликуемую копию перед `InstallInitialize`.
Сама production LaunchCondition остаётся неизменной. Реальный repair-fix этим
не реализован: `descriptor-repair` по-прежнему должен считаться FAIL.

Лог записывает process/thread SID, integrity/elevation/virtualization metadata,
путь process/DLL, owner/group/DACL процесса и результат `AccessCheck` для
нескольких прав изменения процесса применительно к дублированному effective
thread token. Он не содержит содержимого registry descriptors. Проверяются
только состояние, тип и размер четырёх фиксированных значений HKLM в обеих
логических views, а также сравнение тестовой quoted property, прочитанной через
`MsiGetPropertyW`, с заранее известным литералом. DLL ничего не пишет в registry
и не меняет MSI properties. Дополнительно в test-only потоке временно создаётся
ограниченный same-SID medium-integrity token без enabled Administrators; под ним
выполняются только `OpenProcess` и закрытие handles на шесть опасных прав, затем
исходный thread token восстанавливается. Это не повышение прав и не тест другого
пользователя. Process-local self-test проверяет token/ACL API и восстановление
token в обычном и self-impersonated потоке, не открывая MSI и registry.

`../RawRegistrySnapshot.h` — отдельный проверенный побайтовый comparator для
raw `#x` MSI properties и независимых `REG_BINARY` bytes. Его Release tests
отвергают отсутствующее/пустое значение, неправильный registry type, нечётный,
не-hex, quoted/injected и несовпадающий transport, включая изменение каждого
hex digit в полном наборе байтов. Он **не включён в production MSI**, не
проверяет структуру security descriptor, DACL, доверенность источника или гонки;
одного успешного сравнения недостаточно для разрешения repair.

`../BoundedSecurityDescriptor.h` — отдельная structural-проверка serialized
self-relative SD с фактической длиной. До pointer-only `IsValidAcl` и
`IsValidSecurityDescriptor` она ограничивает offsets, ACL/ACE lengths и SID;
вход остаётся неизменным. Проверяется структурное подмножество обычных
allow/deny, object и audit ACE с произвольными SID и masks, а не только
две baseline строки. Conditional/callback, resource-attribute, label, reserved и
неизвестные ACE отвергаются: их payload не проверяется этими Win32 APIs. Это
может отказать и корректному descriptor, поэтому расширение поддержки требует
отдельной проверяемой обработки, а не молчаливого принятия. Null/отсутствующая
DACL структурно допустима; адрес input обязан быть DWORD-aligned для Win32.
Validator не принимает решений о правах DCOM.

`BoundedSecurityDescriptorTests.cpp` запускается тем же Release build и
проверяет counterexample с `AclSize=0`, OS-generated allow/deny/object/audit,
другой порядок частей и неизменность bytes, повреждённые offsets/lengths и
исчерпывающие усечения у guard page, плюс детерминированные мутации. Это НЕ
проверка provenance, concurrency или MSI writer; в production MSI validator
пока не включён и сохранение permissions при repair всё ещё не реализовано.

`../OpcRepairSnapshot.cpp` объединяет оба примитива для четырёх фиксированных
owned AppID values: только `HKEY_LOCAL_MACHINE`, явные 32/64 views, nonempty
`REG_BINARY`, bounded SD и raw property через `MsiGetPropertyW`. Длина чтения
property выводится из ограниченного machine value, а не из недоверенного ввода.
Промежуточные результаты не выдаются при любой ошибке; выбранный writer input
содержит только hex digits без `#x`. На один value действует ресурсный предел
256 KiB: он больше двух максимальных ACL, двух максимальных SID и header, но
может отвергнуть валидные SD с очень большими gaps/padding; это **не** системный
лимит SD. Поддерживаемые ACE и необходимость DWORD alignment те же, что у
validator выше. Точный эффект `#x` ещё должен быть проверен на собранных MSI
tables и реальном native writer, а не только по коду reader.

Reader компилируется отдельно с настоящими Win32/MSI declarations, а его
process-local tests исполняют тот же исходник с fake API, без открытия MSI и
registry. Они проверяют обе views каждого binding, изменённый между снимками
value, ошибки чтения/типа/размера и malformed/tampered properties. Отдельный
test сравнивает manifest компилированного reader с native inventory и отвергает
пропуск, дубли и некорректные bindings. Это **не** проверка реальных MSI API,
registry DACL, package/session/host/module/source provenance или невозможности
изменения value после проверки. Reader не вызывается production MSI и сам не
выдаёт права на запись. Подмена API/process или активной базы и привилегированное конкурентное
изменение не исключаются этими тестами; связывать результат с writer до
доказательства trust boundary небезопасно.

Подготовлен отдельный `TestOnlyRepairSnapshot.cpp`: после проверок disposable
GitHub-hosted/admin harness встраивает его с настоящим reader **только в копию**
MSI и временно связывает native `Registry` rows с private hex properties.
Тот же harness проверяет исходные compiled rows, view компонентов и новую форму
`#x[private]`; он планирует `/fomus`, `/famus`, basic UI, независимые клиенты,
rollback и pre-`InstallInitialize` отказ на property tamper, missing/empty/type,
malformed SD и HKCU decoy. Это эксперимент для проверки MSI-механики, не
реализованная package policy и не доверенный guard: active database, host,
module, source и unsupported transforms он не проверяет. До фактического
завершения нового hosted run все перечисленные integration results — **не
проверены**, даже если локальные fake-API fixtures проходят. Production MSI
по-прежнему author-ит исходные baseline values и не содержит эту DLL.

Runtime workflow теперь требует `repository.private=true` и совпадение имени
репозитория в GitHub event до сборки/передачи каких-либо Runtime binaries;
mutating harness повторяет ту же проверку после hosted/admin gate. При public
или недоступном metadata job явно FAIL, а jobs с бинарниками не запускаются.
Текущий репозиторий `boogy777-lgtm/iCemDataMonitor` публичен, поэтому новый
private Runtime run здесь невозможен без решения владельца. Эта проверка —
изоляция CI, а не доказательство целостности MSI или защиты от администратора.

Эти данные описывают **конкретный** процесс и токен теста. `AccessCheck` на его
DACL не доказывает невозможность injection другим токеном, с включёнными
привилегиями или через DLL/file replacement; пути сами по себе не доказывают
целостность. Нужны отдельные attacker-token, source ACL, UI/elevation и гонки
tests до выбора trusted design. Получение строки через MSI API не делает public
property доверенной, а сравнение заранее известного литерала не является tamper
test настоящих DCOM properties.

На completed [42954354395](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/42954354395)
из `9878b21` тестовый repair шёл в x64 `msiexec.exe` с DLL `MSI*.tmp`:
primary process token имел high integrity и не был LocalSystem; отдельного
thread impersonation token не наблюдалось. Effective token получил все шесть
проверенных прав над собственным процессом. Это свидетельство именно elevated
GitHub runner, не проверка medium-integrity attacker или защиты другого SID.
Обе registry views вернули binary values, тестовый quoted литерал совпал;
descriptor-repair при этом **FAIL**, остальные шесть cases — PASS. Ни trust,
ни неизменность исходных bytes при repair из этого не следуют.

На [42956539042](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/42956539042),
source `44a914e`, ограниченный same-SID medium token получил `ERROR_ACCESS_DENIED`
на все шесть проверенных process rights и при install, и при repair. Matrix
по-прежнему 6/7: `descriptor-repair` FAIL. Модель не включает другого SID,
over-the-shoulder elevation, full UI, file replacement или изменение registry
между preflight и записью. Наблюдаемые пути — `Windows\System32\msiexec.exe`
и `Windows\Installer\MSI*.tmp`; это не проверка file/parent DACL или integrity
файла. Никаких общих security-выводов из модели не делается.

Документированные границы, подтверждающие необходимость проверки:

* [Microsoft: AppSearch](https://learn.microsoft.com/en-us/windows/win32/msi/appsearch-table)
  оставляет исходную property при отсутствующей registry-записи.
* [Microsoft: custom-action remoting](https://devblogs.microsoft.com/setup/debugging-custom-actions-leaked-handles/)
  указывает на выделенные процессы для in-process actions; одна лишь метка
  execute sequence не доказывает защиту памяти и загруженного модуля.
* [Microsoft: registry virtualization](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-virtualization)
  исключает `HKLM\Software\Classes`, impersonation и 64-bit processes;
  явные `HKLM` и `KEY_WOW64_32KEY/64KEY` всё равно обязательны.
* [Microsoft: MSI DLL session handle](https://learn.microsoft.com/en-us/windows/win32/msi/accessing-the-current-installer-session-from-inside-a-custom-action)
  позволяет получить свойства напрямую, без EXE command-line quoting.

Открытые варианты для исследования без изменения контракта: проверяемый DLL
guard с защищённым процессом/источником и независимым execute-side сравнением;
либо новый transactional механизм, исключающий запись существующих values,
если он сохранит 18 canonical компонентов, shared-owner/rollback и обязательный
отказ при повреждённом состоянии. Ни один из вариантов здесь не принят и не
утверждается невозможным; особенно нельзя подменять их безусловным overwrite.
