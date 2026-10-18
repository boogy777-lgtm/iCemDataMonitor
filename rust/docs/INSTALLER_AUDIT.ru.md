# Аудит границ Windows MSI по PLATFORM ARCHITECTURE RULES v3.1

Class: MSI владеет файлами из `windows-payload.json`, записью своего продукта и
opt-in ярлыком/Run value. Пользовательские файлы, службы и процессы iCem вне класса.
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
| `CheckConflicts.js`, WMI stop/terminate, `util:CloseApplication`, kill при Cancel | Не включаются в Rust MSI | Принадлежность процесса определяется не именем. MSI обрабатывает занятые файлы; `MSIRESTARTMANAGERCONTROL=DisableShutdown` запрещает автоматическое закрытие через Restart Manager. Guard запрещает собственные CAs, service-control и util элементы |
| Elevated JScript `WipeMonitorRoot`, `Return=ignore` | Удалён | Рекурсивное удаление чужих данных не имеет MSI rollback/ownership. Тест оставляет пользовательский файл, проверяет его после upgrade и uninstall и отсутствие всех MSI-owned файлов |
| Native Shortcut/RegistryValue | Сохранены, по умолчанию OFF | Target Shell и Run Host `--autostart` проверяются фактической установкой. Autostart принадлежит устанавливающему пользователю; эмулятор независим |
| Удаление `StartupApproved` | Не переносится | Значение Task Manager не создавалось MSI; пользовательское отключение не сбрасывается установщиком |
| Hidden ARP + forced `REINSTALLMODE=amus` | Не переносится | Rust-кандидат виден в установленном ПО; работают стандартные uninstall/repair. Guard запрещает скрытие и принудительную политику перезаписи |
| Разрешённые downgrade и same-version пересборки | Не переносится | Новый ProductCode для N+1, стабильный отдельный UpgradeCode, транзакционный `afterInstallInitialize`, downgrade запрещён. Версии и файлы не перезаписываются |
| AppSearch поверх explicit ICEMDIR | Не переносится | Предлагается стандартный iCem путь, оператор может изменить AddIn destination. Тест использует Unicode/пробелы и отдельный ICEMDIR; directory validation нельзя обойти переходом в OptionsDlg |

Зарегистрированный legacy MSI обнаруживается через `OnlyDetect` и блокирует
установку кандидата. Это не полноценная проверка всех способов ручной установки
legacy и не production cutover. Для silent upgrade с нестандартными путями и
opt-in опциями передавайте те же свойства явно; автоматическое восстановление
выбранных параметров следующей версией пока не реализовано и не заявлено.

Контрактные MSI содержат тестовые байты: на Windows runtime заменён копией
системного `where.exe` (не запускается), AddIn имеет синтетические metadata.
Пакеты и эти DLL не публикуются. 128-byte PE parser fixture не является валидной
целью Shell shortcut; проверка настоящего .lnk остаётся обязательной.

Открыто: реальная SDK-сборка и загрузка AddIn, визуальная приёмка мастера, занятые
файлы/перезагрузка, rollback после искусственного сбоя, offline/repair и обновление
между двумя разными реальными runtime версиями. Штатный MSI механизм сам по себе
не доказывает эти сценарии.

Первичные источники: [WiX 5.0.2 InstallDir UI](https://github.com/wixtoolset/wix/blob/v5.0.2/src/ext/UI/wixlib/WixUI_InstallDir.wxs),
[Microsoft: MSI Restart Manager](https://learn.microsoft.com/en-us/windows/win32/msi/using-windows-installer-with-restart-manager),
[DisableShutdown](https://learn.microsoft.com/en-us/windows/win32/msi/msirestartmanagercontrol).
