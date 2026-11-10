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

## MIDL build gate

Первый run `42790763520` собрал x86/x64 Release, но остановился до создания MSI:
старые хеши `_i.c` учитывали комментарии с версией/окружением MIDL. Установочные
jobs не запускались. Все девять локальных baseline inputs повторно совпали со
старыми pins; новые pins получены из тех же тел после нормализации только трёх
metadata-комментариев и CRLF/LF. Активный код, GUID declarations и остальные байты
остаются hash-pinned; missing/extra files теперь также отвергаются.
`Test-OpcGuidInventory.ps1`: локально PASS 18 проверок; inventory содержит 70
IID/LIBID/CLSID declarations. Проверяются metadata portability и мутации идентичностей,
макроса и инъекции между/через комментарии. CI сохраняет generated inputs даже
при ошибке; изменение тела не разрешается через автоматическое обновление pins.

Compile-only локальная пересборка shared MSI из cached MSM прошла, Release guard:
0 warnings/errors. В отдельном file fixture отсутствующие в старом cache license,
RGS и IDL восстановлены из закреплённого `source-upstream.tar`; исходный cache
не менялся. Первые попытки с неполным cache не прошли, а не были подавлены.
Новый blocked MSI `rust/target/runtime-midl-portable-20261110.msi`, SHA-256
`fb140ebe2009b1d625eddfb1c8b282654194d63c39f5e41dc418067869889418` — не поставка.
Все 143 guard paths побайтово совпали с прежним secure-v3 inventory.
Никакой установки на этом ПК не было; fresh CI build/installation ещё обязателен.

Обычный CI `42790763716` также не прошёл: Scaffold/cache command contract drift,
legacy tests и Windows heap profiling. Baseline и packaging contracts прошли.
Эти результаты не подменяются отдельной OPC-матрицей и не означают release acceptance.

## Oracle rollback: запрет ложного PASS

Read-only проверка собранной `InstallExecuteSequence` обнаружила отсутствие
`InstallExecute`: Type 19 на 6550 перед `InstallFinalize` сам по себе не доказывает,
что deferred operations вообще применялись. Результат старого rollback fixture,
даже если он зелёный, **не является принятой rollback-проверкой**.

В TEST ONLY CI-копии добавляется стандартный `InstallExecute` на 6500, только
для `ICEM_ACCEPTANCE_ROLLBACK=1`. Oracle требует в логе выполненный FileCopy,
успешный InstallExecute, затем именно injected Type 19 failure, в этом порядке.
Foreign-owner oracle отдельно требует реального отказа `OpcCheckForeign` до
InstallInitialize. `Test-SharedOpcAcceptance.ps1`: локально PASS 12 synthetic-log
проверок; ранний сбой/только имя scheduled action/неверный порядок не дают PASS.
Основание: Microsoft `InstallExecute Action` выполняет накопленный install script,
не завершая транзакцию: https://learn.microsoft.com/en-us/windows/win32/msi/installexecute-action.

Run `42793336639`, head `2aeefc9ad881390b52b35dc13f5910f227eb0b42`:
- Source-pinned x86/x64 Release + MSI build PASS. Сохранённые MIDL inputs подтвердили
  timestamp-only drift (`03:14:07` у runner, `08:14:07` у локального baseline),
  помимо пути. Тела проходят те же pins; не требуется расширять допустимый код.
- Foreign registry refusal PASS; snapshots unchanged.
- Fresh install вернул 0 и сохранил installed snapshot, но lifecycle fixture
  остановился до repair: `ConvertFrom-Json` без dictionary mode отверг допустимое
  пустое имя default registry value. Общий `Read-SharedOpcSnapshot` сохраняет его.
- Foreign MSI-owner fixture не компилировался: WiX File требует `Source`, не
  `SourceFile`. Authoring вынесен в `opc-core/tests/ForeignOwner.wxs` для отдельной
  compile-only проверки; до проверки preflight этот job не дошёл.
- Старый rollback job показал PASS до реального script execution; этот результат
  не принимается. Более строгий oracle добавлен до последующего acceptance run.

`Test-SharedOpcAcceptance.ps1` теперь проверяет 13 сценариев, включая default
registry value. На скачанных CI evidence новый decoder прочитал 18 файлов/441
registry rows; усиленный preflight oracle принял реальный foreign-registry log,
а rollback oracle отверг прежний ложный PASS. Generated CI MIDL inputs прошли
все 18 inventory tests; standalone ForeignOwner MSI с `-wx` скомпилировался
локально без установки. Установочная приёмка runtime ещё не завершена.
