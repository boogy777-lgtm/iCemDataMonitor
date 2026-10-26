# Аудит границ Windows MSI по PLATFORM ARCHITECTURE RULES v3.1

Class: MSI владеет файлами из `windows-payload.json`, записью своего продукта и
opt-in ярлыком/Run value. Пользовательские файлы вне класса владения MSI. Процессы и службы входят в отдельный класс конфликтов, если удерживают затрагиваемые файлы; это не право владения ими.
Inv: каждый файл имеет один mount и стабильный Rust component GUID; upgrade и
uninstall сохраняют не принадлежащие MSI данные. Проверки: `Test-WindowsPackaging.ps1`,
`Test-WindowsInstaller.ps1`, `Test-WindowsMsiUpgrade.ps1`.
N+1: новый файл добавляется в существующий payload inventory; harvester получает
только один runtime stage и один AddIn stage. Полнота и уникальность проверяются
до сборки MSI и после фактической установки. Новый ресурс другого класса требует
своего обоснованного инварианта, а не расширения самописного installer manager.

Аудит не меняет исходный .NET-установщик. Его исторические комментарии не являются
новым разрешением применять все накопленные политики к Rust-кандидату.

| Место в legacy | Решение для Rust | Причина и исполняемая проверка |
|---|---|---|
| `installer/gen-payload.ps1` | Переиспользован без изменения, с одним runtime stage | Не используется keep-first слияние .NET TFM. Проверяются все исходники/число файлов/один AddIn mount; любое пропущенное или дублированное назначение отклоняется |
| GUID harvester из `component:<relative path>` | Ограниченный адаптер задаёт GUID в namespace Rust UpgradeCode | Исходный AddIn GUID совпал бы с legacy. Windows contract сравнивает с неизменённым harvester: множества GUID не пересекаются, GUID стабильны при переносе build directory |
| PNG, ICO и `gen-ui-bmps.ps1` | Переиспользованы оригиналы | Это оформление, а не управление установкой. Hash guard PNG, генерация BMP и WiX `-wx` обязательны; визуальная эквивалентность не доказана |
| Проекция всего `installer/Product.wxs` и переписывание имён процессов | Удалена; небольшой `rust/installer/Product.wxs` описывает только Rust package policy | Отсутствует связность с посторонними legacy CustomAction. Role-targets берутся из verified manifest; не создаётся второй список payload |
| `CheckConflicts.js`, WMI stop/terminate, `util:CloseApplication`, kill при Cancel | Имён и kill-on-Cancel нет; native Restart Manager активен | Требование пользователя уточнено: force — разрешённая явная опция. `MSIRMSHUTDOWN=0` запрашивает штатное закрытие, `1` разрешает native force после deadlines. Только владельцы файлов данного MSI; согласие не сохраняется |
| Elevated JScript `WipeMonitorRoot`, `Return=ignore` | Удалён | Рекурсивное удаление чужих данных не имеет MSI rollback/ownership. Тест оставляет пользовательский файл, проверяет его после upgrade и uninstall и отсутствие всех MSI-owned файлов |
| Native Shortcut/RegistryValue | Сохранены, по умолчанию OFF | Target Shell и Run Host `--autostart` проверяются фактической установкой. Autostart принадлежит устанавливающему пользователю; эмулятор независим |
| Удаление `StartupApproved` | Не переносится | Значение Task Manager не создавалось MSI; пользовательское отключение не сбрасывается установщиком |
| Hidden ARP + глобальный `REINSTALLMODE=amus` | ARP виден; полный repair ограничен повторным `/i` установленного продукта | Для `Installed AND NOT REMOVE AND NOT UPGRADINGPRODUCTCODE` задаются `REINSTALL=ALL` / `REINSTALLMODE=amus`. Восстанавливаются отсутствующие и испорченные MSI-owned файлы, не произвольные файлы каталога |
| Downgrade и same-version пересборки | Same-version replacement разрешён, downgrade запрещён | `AllowSameVersionUpgrades=yes`, новый ProductCode, отдельный стабильный UpgradeCode и транзакционный `afterInstallInitialize`. Builder допускает только три numeric поля: скрытого четвёртого поля версии нет |
| AppSearch поверх explicit ICEMDIR | Native сохранение и восстановление путей/опций | Собственный HKLM Installer state, отдельные SAVED properties и type-51 transfers: explicit CLI побеждает, UI choices не затираются execute sequence. Новые файлы не требуют изменения этого механизма |

Зарегистрированный legacy MSI обнаруживается через `OnlyDetect` и блокирует
установку кандидата. Это не полноценная проверка всех способов ручной установки
legacy и не production cutover. Legacy и Rust — отдельные MSI с разными семействами
обновления. Исходный legacy упаковщик не изменён.

## Уточнение пользователя: установка для человека без IT-навыков

Удаление самой возможности force было неверным ограничением предыдущего аудита.
Нужно устранять блокирующие установку ресурсы с явным согласием и проверять результат.
Новая опция на странице параметров разрешает Restart Manager принудительно закрыть
не отвечающие процессы/службы после штатного запроса и native deadlines. Она не
сохраняется на будущее. Cancel до execute sequence не завершает процессы; rollback после закрытия
Restart Manager не восстанавливает состояние уже закрытых процессов.
Windows связывает конфликт с затрагиваемым файлом, а не с именем процесса.

Class: владельцы файлов, которые заменяет/удаляет MSI; процесс с тем же именем,
держащий чужой файл, вне класса. Inv: native RM grace/force только по текущему
согласию; MSI success проверяется по payload и завершению блокирующего процесса.
N+1: новые files идут через `windows-payload.json`; новый process-name rule не нужен.
Исполняемые сценарии находятся в `Test-WindowsMsiUpgrade.ps1` и `tests/LockedFileOwner.cs`.

Повторный запуск того же файла MSI восстанавливает все owned files. Стандартная
страница maintenance предлагает repair/remove, затем показывает параметры и force.
При remove controls ярлыка/autostart отключены; force доступен для текущего удаления.
Silent `/i` установленного MSI также выполняет полный repair без ручных флагов.
Пути и последние опции восстанавливаются из MSI-owned HKLM Installer state; явные
CLI свойства имеют приоритет. Secure marker не даёт AppSearch execute sequence
отменить изменения, сделанные в wizard. Transitive option components применяют
явное отключение ярлыка/autostart. Force остаётся свойством одной операции.

Fixture становится готовой только после показа настоящего GUI окна; hidden/minimized окно не является доказательством GUI shutdown path. Shutdown messages сохраняются в bounded test logs.

Тесты повреждают и удаляют owned files, повторяют обычный `/i` дважды без путей и
опций, проверяют оба mounts, Unicode-пути, настройки, один зарегистрированный
продукт и неизменность пользовательского файла. Upgrade также запускается без
повторного ввода путей. Отдельно проверяются явный opt-out и последующее сохранение
этого выбора. Два test EXE одного имени держат разные файлы: закрывается только
фактический blocker. Graceful и force различаются по записи кооперативного освобождения ресурса fixture при shutdown message. Ожидать только FormClosed недостаточно: WM_ENDSESSION может завершить процесс без этого события.
Это проверка реального Windows Installer/RM; живые iCem/Npcap/службы/другие sessions
и first-run Host/config требуют отдельной приёмки.

### Checkbox representation and wizard regression

Class: три native checkbox (`Product.wxs`) используют null для Off; строка `0`
является непустым значением и не подходит для UI. Числовой backend — другой случай.
Inv: native type-51 actions очищают сохранённый/CLI `0` до UI, а null сериализуется
как `0` только в execute sequence. `Test-WindowsPackaging.ps1` проверяет все три
свойства, порядок и обе фазы; initialization marker сохраняет снятые UI choices.
N+1: следующий checkbox требует такого же преобразования и guard. Windows fixture
`InstallerUiProbe.cs` проверяет настоящий мастер через UI Automation: saved On,
explicit CLI zero, force Off по умолчанию, toggle On→Off, Next→Back, Cancel,
repair с opt-out и повторный запуск с сохранённым Off. UI probe/owned msiexec имеют
конечные deadlines. Fixture не входит в пакет. Это interaction evidence, а не
визуальная приёмка DPI/branding/layout или проверка реального SDK.

Если файлы не удалось заменить и Windows требует reboot, код 3010 не равен
полностью готовой установке. Release acceptance ожидает 0 и проверяет hashes всех
установленных файлов. Стандартный MSI сообщает о необходимой перезагрузке; нельзя
объявлять такой результат PASS до завершения reboot/проверки. Deadlines RM — 30 s
для unresponsive приложения и 20 s для службы; test operation ограничена 180 s.

Контрактные MSI содержат тестовые байты: на Windows runtime заменён копией
системного `where.exe` (не запускается), AddIn имеет синтетические metadata.
Пакеты и эти DLL не публикуются. 128-byte PE parser fixture не является валидной
целью Shell shortcut; проверка настоящего .lnk остаётся обязательной.
Run 39103390560 уже создал .lnk, но WScript.Shell вернул кириллицу как `?`.
На `49162690d09be80709fafad7489d9cd134c58bc2` run
[39103566447](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/39103566447)
PASS: Shell.Application / FolderItem.GetLink подтвердил Unicode target и наличие
цели. Сырые байты .lnk и адрес сохранены в logs. Default opt-out, оба mounts,
ярлык/автозапуск, N→N+1, downgrade rejection и uninstall ownership прошли;
это evidence механики MSI на fixtures, а не SDK/runtime/визуальной эквивалентности.

Открыто: реальная SDK-сборка и загрузка AddIn, визуальная приёмка мастера, занятые
файлы/перезагрузка, rollback после искусственного сбоя, offline/repair и обновление
между двумя разными реальными runtime версиями. Штатный MSI механизм сам по себе
не доказывает эти сценарии.

Первичные источники: [WiX 5.0.2 InstallDir UI](https://github.com/wixtoolset/wix/blob/v5.0.2/src/ext/UI/wixlib/WixUI_InstallDir.wxs),
[Microsoft: MSI Restart Manager](https://learn.microsoft.com/en-us/windows/win32/msi/using-windows-installer-with-restart-manager),
[MSIRMSHUTDOWN](https://learn.microsoft.com/en-us/windows/win32/msi/msirmshutdown),
[RM deadlines](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmshutdown).
Checkbox semantics: [Microsoft CheckBox control](https://learn.microsoft.com/en-us/windows/win32/msi/checkbox-control).

Дополнительный дефект ZIP builder: цикл AddIn затенял переменную имени архива,
поэтому checksum называл вложенный `addin-manifest.json` вместо ZIP. Переменная
цикла отделена; portable contract проверяет SHA256 **и точное имя** артефакта.

Release smoke передаёт owned temporary directory завершающему workflow step с
`always()`. Он проверяет имя и temp-parent и удаляет распаковку после browser и
evidence upload, в том числе при сбое; каталоги не накапливаются на SDK runner.
Реальное выполнение SDK-backed release, включая этот cleanup, остаётся открытым.

### Installer lifecycle regression (Windows execution pending)

Class: независимая пересборка того же `x.y.z` относится к replacement, меньшая
numeric версия — к запрещённому downgrade; Installed maintenance не является
новым legacy cutover. Inv: одна регистрация, восстановленные предыдущие hashes,
paths/options и operator config после сбоя. N+1: `Product.wxs`,
`Test-WindowsPackaging.ps1` и `Test-WindowsMsiUpgrade.ps1` проверяют policy и
реальный MSI; production deny guard для CustomAction/Binary сохранён.

Fixture отдельно компилирует same-version MSI и обновляет active ProductCode.
Только тестовая копия XML добавляет type-19 failure после `InstallExecute`,
до `InstallFinalize`: выполняются removal и новый install script, затем проверяются
rollback старых файлов/регистрации/options и сохранность operator file.
UI Automation добавляет fresh custom AddIn path/Back/Cancel/install, Cancel с живым
AddIn owner и включённым force (процесс должен остаться жив), успешный UI force
repair и maintenance Remove. Runtime/AddIn blockers и foreign same-name owner
проверяются независимо. Legacy gate тестируется маленьким registry-only MSI с legacy UpgradeCode:
FindRelatedProducts обнаруживает fixture, incoming Rust блокируется, уже
установленные repair/uninstall доступны. Это evidence native family detection,
а не установки production legacy payload рядом с Rust.
Эти новые Windows сценарии локально не запускались; Linux XML/Python checks
не доказывают MSI/RM/UI поведение. Production acceptance выше остаётся открытой.
