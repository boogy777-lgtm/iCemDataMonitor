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

## Подтверждённые lifecycle defects и MSI-client guard

Run `42794478800`, head `ea2caec54ed26fc9b40403c12f76d64ebd5f850f` дошёл до
настоящей матрицы (production MSI оставался заблокированным):
- Fresh install: 18 native files. Repair: 1603, `RegisterOpcCatMgr` вернул 183
  (`ERROR_ALREADY_EXISTS`). Uninstall в этом сценарии ещё не достигнут.
- Injected failure действительно после успешного InstallExecute; после rollback
  файлы удалены, но остались 441 registry rows (включая пересекающиеся snapshot
  roots) и OpcEnum service с LocalSystem и уже отсутствующим EXE. Это FAIL.
- Чужой MSI-клиент канонического x86 opcproxy: установка ошибочно вернула 0,
  отказа до транзакции не было. Registry-only guard недостаточен.
- Foreign registry отказ по-прежнему PASS, с усиленной log-проверкой.

`RegistryGuard.cs` теперь читает MSI-клиентов всех 18 canonical component GUID
через `MsiEnumClientsExW`, all-user/all-context, index 0: любой клиент или любая
ошибка, кроме `ERROR_NO_MORE_ITEMS`, означает отказ. API только читает inventory;
никаких repair/adoption вызовов. Фиксированный resource `opc-component-codes.txt`
обязательно совпадает с полными Component tables обоих проверенных MSM.
`tests/RegistryGuardTests.csproj`: Release, 0 warnings/errors; PASS 13 pure tests
через injected API results, без реального чтения/изменения machine registrations.
Compile-only shared MSI с новым guard также собран; это не установочная приёмка.

Следующий lifecycle fix — native MSI Registry/ServiceInstall/ServiceControl
ownership вместо self-registration/EXE registration actions, с проверкой repair
и rollback. Не заменять это условиями «не запускать при repair», подавлением
ошибок или ручной зачисткой shared state. Открытые client/operator/upgrade/signing
gates остаются обязательными; production block сохранён.

Run `42795522797` собрал новый guard, но выявил отдельный interop defect: Win32
вызов со всеми null outputs возвращает 87 (`ERROR_INVALID_PARAMETER`), несмотря
на optional annotations API docs. Поэтому fresh runner тоже получил отказ;
зелёные foreign-owner jobs этого run **не принимаются** как доказательство.
Вызов исправлен: выделяется 39-character ProductCode buffer и output context.
Отказ при любых API errors сохранён; к whitelist ошибок ничего не добавлено.

Добавлен отдельный real native read-only smoke (`tests/NativeClientQuery.cs`),
использующий тот же метод, что production guard; он проверяет все 18 queries в
CI с полным admin token до сборки MSI. Pure tests не подменяют этот gate.
Локально компиляция Release PASS. All-user smoke на рабочем ПК вернул 5
(`ACCESS_DENIED`), так как текущий token не повышен; повышение не выполнялось.
Отдельная **read-only** machine-context диагностика исправленного interop
подтвердила 259 для незарегистрированного GUID и 0/ProductCode OPC Core для
канонического opcproxy. Production all-user policy этим не ослабляется.

## Переход на native MSI ownership

Run `42797248221`, head `c0142fc536f0baee041b441526e1453d6c4981e3`: source build
и real native query smoke PASS; foreign registry и foreign component-owner PASS,
clean fresh install вернул 0. Repair снова 1603; настоящий rollback оставляет
регистрации/службу. Эти два lifecycle defects не закрыты зелёными foreign checks.

`New-OpcRuntimeModule.ps1` заменяет upstream self-registration/EXE actions native
Registry, ServiceInstall и ServiceControl таблицами; все 18 canonical GUID,
имена/расположение файлов и demand/LocalSystem/RPCSS service contract сохраняются.
Для семи SystemFolder x86 files используется явный физический Windows/SysWOW64
directory: прежний mixed-x64 MSI dual-folder workaround больше не требуется.
Никаких ForceDeleteOnUninstall, RemoveRegistry или автоматической зачистки чужих
keys. Native ownership каждой записи связано с существующим file-component.

`native-runtime-inventory.json`: 420 owned keys / 447 values (444 captured + 3
value-backed MSI bookkeeping markers). Данные получены из
зафиксированного чистого CI install snapshot (run/input/snapshot hashes в файле),
shared AppID/ProgID/TypeLib keys дедуплицированы, machine paths заменены MSI refs.
Это не слепой harvest: каждый файл/GUID/arch/location сверяется с hash-pinned
upstream authoring; каждый IID/name/method count и proxy factory — с реальными
MIDL vtables и прежним pinned GUID inventory. Оба AppID требуют прежних local-only
SY/BA Access/Launch descriptors и AuthenticationLevel=6; внешний config не читается.

`Test-OpcNativeInventory.ps1`: локально PASS 27 проверок, включая пропуски,
дубликаты, подмены методов/IID/factory/file references, widening DCOM и service
ownership. Обе native MSM скомпилировались с `-wx`, без установки. Первые
compile-only варианты были отвергнуты WiX из-за лишнего RegistryKey Id; исправлена
сама authoring schema, предупреждения/ошибки не подавлялись.

Core builder теперь всегда использует native module recipe; native inputs
включены в metadata и source payload. Shared builder отвергает SelfReg,
module CustomAction/RemoveRegistry, проверяет registry/service coverage и
принадлежность всех registrations conflict inventory. Требуется свежий CI
lifecycle run этой реализации. Production fresh-install block сохранён.

Локальный compile-only shared MSI с cached Release binaries собран, guard Release
0 warnings/errors; SHA-256 `3f2e73b22bf4c1ca34e8365a941cd2262738507d17e384002350185b3ac57ddb`.
Это явно помеченный fixture, не доказательство свежей source build. Setup refusal
для него PASS: блок сработал до компиляции/публикации Setup. Syntax 15 scripts,
27 native authoring и 13 acceptance-oracle checks PASS; установки на ПК не было.
Lifecycle fixture усилен: после fresh install удаляет только собственный DLL и
default NumMethods value в обеих registry views на disposable runner; `/famus`
обязан восстановить точный installed snapshot перед uninstall. Успешный repair
exit без восстановления ресурсов больше не считается достаточным.
Отдельно не закрыто сохранение consent-driven operator grants при repair:
нынешний native inventory задаёт только исходный SY/BA baseline. Перед поставкой
нужны отдельное владение/проверка operator policy и тест customized descriptors;
нельзя молча выдать повторную запись baseline за сохранение разрешений оператора.

### Native run и расширение клиентской проверки

Run `42803585178`, head `68e85086ee6f375ff2ff080770f771e00286bd45`: свежие native
x86/x64 MSM и shared MSI собраны; реальные rollback/foreign-registry/foreign-MSI
owner checks PASS. Rollback после выполненного InstallExecute вернул полный
исходный снимок: без оставшихся native файлов, COM-записей и OpcEnum service.

`/famus` вернул 0 и восстановил удалённый DLL/NumMethods values, но strict snapshot
отверг repair: у 17 не удалявшихся файлов исчез `SE_DACL_AUTO_INHERITED` (`AI`);
SID owner/group, ACE/masks, hashes, registry и service configuration не изменились.
Этот **forced-all reinstall результат не принят** и не нормализуется comparator.
Для штатной maintenance-проверки используется документированный default `/fomus`,
а не force-all `a`: https://learn.microsoft.com/en-us/windows/win32/msi/command-line-options.
Требование восстановления намеренно удалённых ресурсов и точное сравнение всего
snapshot, включая SDDL, сохраняются. Поддержка forced reinstall отдельно открыта.

Добавлен пятый disposable case `clients`: один независимый net48 COM client source
собирается в x86 и x64, активирует настоящий OPCEnum, проверяет blanket packet
privacy и RPC Enum/Next/Reset/Clone через IOPCServerList2/IOPCEnumGUID, затем удаляет
runtime и сравнивает чистое состояние. ABI/vtable order сверены с pinned
`Source/Common/ProxyStub/opccomn.idl`. Это не сторонний HMI/LAN acceptance и не
normal-operator test: fixture использует elevated runner token.
Обе клиентские сборки локально Release 0 warnings/errors; **не запускались** на
рабочем ПК, так как CoCreateInstance способен запустить установленную службу.
Выполнение разрешено только общим disposable/admin gate; timeout не повторяет RPC.

Run `42806848733`, head `41c73c2dbac65d4930fb228af1902c62c28f1c41`:
штатный `/fomus` после удаления DLL и двух NumMethods values восстановил точный
snapshot, включая SDDL. Uninstall вернул 0 и удалил все файлы/службу, но оставил
пустой Programmable key и его пустой CLSID parent. Key-only `+` authoring не
оказалось достаточным для полного удаления этого marker; FAIL сохранён.

Исправление без ForceDelete: три прежде пустых key получают служебный DWORD
`IcemNativeKey=1`, принадлежащий тому же file-component. При uninstall MSI удаляет
только это значение; пустой key удаляется стандартным RemoveRegistryValues,
а неизвестные чужие значения/children не стираются. Key presence/COM values
сохранены; эта intentional bookkeeping delta записана в JSON. Generic key-only
ветка удалена, inventory с пустыми values отвергается. Native checks теперь 28.

Первый x86 COM probe активировал сервис, установил/проверил packet-privacy blanket
и получил enumeration object, но Next не прошёл HRESULT/count oracle. Это не PASS;
добавлена диагностика фактического HRESULT/count. Теперь обе архитектуры выполняют
по одной независимой попытке, известная ошибка одной не скрывает результат другой;
timeout по-прежнему завершает fixture без retry. На рабочем ПК probe не запускался.

Шестой case `shared-owner`: совместимый synthetic MSI claims canonical OpcEnum и
opcproxy components с точно теми же установленными bytes/version. После удаления
только нашего Runtime ожидаются неизменённые общие файлы, COM values/DACL и служба,
остальные собственные ресурсы удалены; Windows Installer ComponentClients должен
показывать только независимый оставшийся продукт. Expected registry subset
выводится из native inventory, shared-view roots проверяются явно. Оба owner cases
используют один compile helper; чужой продукт не удаляется ради чистого теста.
