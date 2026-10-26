# Установка Windows candidate

Текущий пакет — Rust applications + оригинальный net48 AddIn.
Полный и поддерживаемый runbook: [src/rust/installer/README.md](../src/rust/installer/README.md).
Это development candidate с именами `*-dev.exe`; наличие MSI не закрывает
production cutover, реальные SDK/iCem/hardware/UI/operator gates.

Требуются Windows x64, vendor iCem с .NET Framework 4.8 для AddIn,
административные права для per-machine MSI. COM-стенд использует внешнюю пару
драйвера com0com; LAN capture/emission требует Npcap. Rust desktop использует GPUI.
Legacy .NET 10/WinAppSDK/WebView2 prerequisites относятся к отдельному старому MSI.

## Сборка полного пакета

Windows x64, PowerShell 7.4+, MSVC/Windows SDK, Rust pin, .NET SDK и WiX 5.0.2:

```powershell
pwsh -NoProfile -File .\rust\tools\Build-WindowsRelease.ps1 -Version 0.1.0
```

Без реального iCem SDK либо verified SDK-built AddIn bundle сборка завершается
ошибкой. Порядок подготовки bundle и проверки provenance описаны в runbook.
Артефакт: `dist/rust/iCemDataMonitor-rust-dev-0.1.0-windows-x64.msi` + SHA256.
ZIP — промежуточный build artifact. Synthetic CI DLL не является release AddIn.

## Пути и запуск

| Что | Куда |
|---|---|
| Host/Shell, данные/конфиг/логи | `%ProgramData%\Halliburton\iCem\DataMonitoring` (`MONITORROOT`) |
| Emulator catalogs | Отдельная role path `emulator/templates/` внутри runtime |
| AddIn | `[ICEMDIR]\Bin\Modules\Burservis.iCem.DataMonitor.dll` |
| AddIn provenance | runtime `addin/` |

MSI не создаёт `config.ini` или `Data.dat`. Shell запускает отсутствующий Host;
самостоятельный emulator запускается отдельно. Использовать launcher paths из
manifest/runbook. Modbus по умолчанию :502, Web loopback :8090.

```powershell
msiexec /i iCemDataMonitor-rust-dev-0.1.0-windows-x64.msi /qn /l*v install.log
```

Desktop shortcut и HKCU Host autostart опциональны, default OFF. Emulator не
автозапускается. Повторная установка ремонтирует missing/corrupt owned files.
Upgrade N→N+1 сохраняет paths/options; downgrade блокируется. Удаление убирает
MSI-owned resources и сохраняет пользовательские config/data/logs.
Restart Manager сначала просит graceful shutdown; force — явная опция конкретной
операции. В полном UI при занятых файлах стандартный диалог Files in Use
требует отдельно выбрать «Close the applications and attempt to restart them»
и нажать OK; checkbox force это подтверждение не пропускает. Отмена до execute sequence не останавливает владельцев файлов;
после закрытия Restart Manager rollback не восстанавливает состояние процессов. Reboot-required не считается успешным
завершением release gate. Установленный legacy MSI обнаруживается и блокирует
Rust MSI: автоматического production cutover нет.

Отдельный legacy builder `installer/build-msi.ps1` и workflow `release.yml`
сохранены для reference compatibility; их GUID и runtime handoff
не являются политикой Rust MSI. [Релиз](release.md), [конфигурация](config.md).


## Legacy MSI: исправленный цикл установки

`installer/build-msi.ps1` сохраняет прежний UpgradeCode, ProgramData layout,
скрытую ARP-запись и разрешённый downgrade. Замена версии теперь выполняется
внутри MSI transaction: ошибка нового пакета откатывает удаление старого.
Повторный запуск того же MSI ремонтирует owned files; новый build той же версии
заменяет предыдущую регистрацию. Удаление через `msiexec /x <ProductCode>`
сохраняет пользовательские config/data/logs. Рекурсивного wipe больше нет.

Restart Manager закрывает только владельцев затронутых файлов, сначала штатно.
Принудительное закрытие зависших владельцев требует checkbox либо
`MSIRMSHUTDOWN=1` для текущей операции. При занятых файлах полный UI также
показывает стандартное подтверждение Files in Use: выбрать закрытие приложений
и нажать OK. Checkbox force не заменяет это подтверждение. Отмена до execute sequence
не останавливает процессы/службы; rollback после закрытия Restart Manager
не восстанавливает их состояние.
Опции ярлыка и HKCU autostart по умолчанию выключены; последующие repair/upgrade
сохраняют выбранные paths/options. CLI overrides имеют приоритет; явный `0`
выключает опцию. Выбор относится к пользователю, выполняющему установку.
Новые сохранённые настройки доступны начиная с этого исправления; старый MSI
не записывал custom ICEMDIR/options, поэтому при первом переходе их следует
передать явно. Для уже установленного старого пакета его cached uninstall
остаётся прежним: новый MSI не может переписать код старого cached пакета.

Incoming legacy MSI блокируется при установленном Rust MSI; maintenance уже
установленного legacy продукта остаётся доступным. Автоматического cutover нет.
Production legacy build требует реальный vendor SDK/AddIn. Исключение
`-AllowMissingAddinForCi` разрешено только в GitHub Actions для непубликуемого
smoke-пакета; synthetic lifecycle fixtures не доказывают SDK/runtime parity.

`installer/tests/Test-LegacyInstaller.ps1` выполняет на disposable elevated
Windows VM установку, repeat/repair, same-version rebuild, upgrade/rollback,
разрешённый downgrade, сохранение paths/options/data, owner isolation,
UI Back/Cancel/Remove и проверку разделения семейств MSI. Локально вне CI
требуется явный `-AllowMachineChanges`. Тестовые MSI не входят в release.
