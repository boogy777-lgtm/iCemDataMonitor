# Shared OPC Runtime — установочная приёмка

Пользователь выбрал сначала завершить OPC Runtime, затем пересобрать Setup.
Установка, COM/DCOM, службы и firewall рабочего ПК **не разрешены**.

Работа изолирована в ветке `feat/opc-runtime-acceptance-20261110` от исходного
незакоммиченного UX/turnkey дерева. Для Windows install/repair/rollback/uninstall
используется одноразовый GitHub-hosted `windows-2022`, не self-hosted workstation.
Пакет runtime по-прежнему имеет production `LaunchCondition=Installed`.

`Test-SharedOpcRuntime.ps1` создаёт отдельную **TEST ONLY** копию MSI только после
проверки disposable runner/admin token, входного checksum, UpgradeCode и единственного
production блока. В копии снимается только временный unconditional блок; все
настоящие registry conflict checks и native registration actions сохраняются.
Тестовый MSI не публикуется. Добавленная Type 19 ошибка после InstallExecute
проверяет настоящий rollback, а не только отказ до начала транзакции.

Матрица `acceptance-cases.json`: fresh/repair/uninstall; injected rollback;
отказ при чужой registry registration; отказ при другом MSI-клиенте канонического
OPC component GUID. Снимки сравнивают типы/значения/DACL registry в обеих views,
восемнадцать native files с hash/DACL и конфигурацию OpcEnum service.
Чужое состояние не очищается для удобства теста; неоднозначный исход не повторяется.

Исходная security-подготовка уже показывает lifecycle risk: upstream MSM вызывает
`/Service` и `/RegServer` при компонентном repair; текущие security-конструкторы
запрещают существующий service/AppID. Саморегистрация также не доказывает обратный
эффект при MSI rollback. Результаты одноразового CI нужны до изменения установочного
контракта; initial characterization не является обещанием turnkey.

Открыто: normal-operator grants только собственного runtime, x86/x64 независимые
COM clients, upgrade и shared-owner removal, signing/distribution terms и полная
Setup acceptance. До закрытия нужных gates production block не снимать.
