# Установка Windows candidate

Текущий пакет — Rust applications + оригинальный net48 AddIn.
Полный и поддерживаемый runbook: [rust/installer/README.md](../rust/installer/README.md).
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
Артефакт: `rust/dist/iCemDataMonitor-rust-dev-0.1.0-windows-x64.msi` + SHA256.
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
операции. Отмена не убивает процессы. Reboot-required не считается успешным
завершением release gate. Установленный legacy MSI обнаруживается и блокирует
Rust MSI: автоматического production cutover нет.

Отдельный legacy builder `installer/build-msi.ps1` и workflow `release.yml`
сохранены для reference compatibility; их GUID, runtime handoff и wipe policy
не являются политикой Rust MSI. [Релиз](release.md), [конфигурация](config.md).
