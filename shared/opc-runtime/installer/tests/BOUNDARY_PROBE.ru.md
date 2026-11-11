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
и не меняет MSI properties. Process-local self-test вызывает Win32 token/ACL API
в обычном и self-impersonated потоке, не открывая MSI и registry.

Эти данные описывают **конкретный** процесс и токен теста. `AccessCheck` на его
DACL не доказывает невозможность injection другим токеном, с включёнными
привилегиями или через DLL/file replacement; пути сами по себе не доказывают
целостность. Нужны отдельные attacker-token, source ACL, UI/elevation и гонки
tests до выбора trusted design. Получение строки через MSI API не делает public
property доверенной, а сравнение заранее известного литерала не является tamper
test настоящих DCOM properties.

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
