# Релизный процесс

Software implementation и [N+1 v1.7](../rust/docs/NPLUS1_TRACKER.ru.md) завершены.
Release acceptance — отдельная проверка: exact-head Windows CI, реальный SDK-built
AddIn, загрузка в iCem, hardware/operator/visual walkthrough и distribution terms.
Текущий Rust MSI остаётся development candidate; `*-dev.exe` не переименованы.

## Rust Windows release

[Полный runbook](../rust/installer/README.md), workflow
`.github/workflows/rust-windows-release.yml` (фактический файл workflow — источник
параметров). Сборка из корня:

```powershell
pwsh -NoProfile -File .\rust\tools\Build-WindowsRelease.ps1 -Version 0.1.0
```

Требуются Windows x64/PowerShell7.4+, Rust pin/MSVC/SDK, .NET SDK/WiX5.0.2 и
настоящий iCem SDK либо проверенный AddIn bundle. SDK workflow выпускает bundle;
hosted release связывает успешный run/source hashes с текущими AddIn inputs.
Synthetic installer fixtures подтверждают mechanics, а не release payload.

Manual release выбирает `next` либо возрастающий `X.Y.Z` в пространстве
`rust-vX.Y.Z`, отдельном от legacy `vX.Y.Z`. После install/repair/upgrade/uninstall
checks доступны MSI и SHA256; `publish: true` создаёт draft prerelease.
Не подменять предыдущие assets. Rust MSI имеет собственные стабильные UpgradeCode
и component namespace, новый ProductCode и блокировку downgrade. Пользовательские
config/data/logs сохраняются; legacy MSI blocks cutover и автоматически не удаляется.

## Evidence перед merge/release

Успешный предыдущий N+1 [run40253303600](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/40253303600)
относится к предыдущему head. Для слияния C11 необходимы публикация exact commit и успешные обязательные
Windows jobs; актуальный verdict — в [PR44](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/44). Нельзя объявлять новый head
проверенным по старому run. [C11](LEGACY_REFACTORING_C11.ru.md) фиксирует source
retirement; manifest/ledger сохраняют настоящую provenance.

## Legacy reference release

Корневые `installer/build-msi.ps1`, `VERSION`, `.github/workflows/release.yml`
и legacy `v*` сохранены как отдельный reference/build path. Версия старого builder
читается из `VERSION`, override — `-MsiVersion X.Y.Z`. Автоматический ProductCode,
legacy downgrade/wipe/runtime policy не переносятся на Rust MSI.
