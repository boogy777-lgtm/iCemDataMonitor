# Релизный цикл

## Главный закон релиза

**`ProductCode="*"` (авто на каждую сборку), `UpgradeCode` (`{34A0FD7C-EBD5-4E28-BA96-AE6575896F32}`) стабилен навсегда.** (2026-09-01 PM, аутопсия 1731/10005: ручная ротация GUID по bump'у версии отменена — same-version пересборка со стабильным ProductCode давала новый PackageCode и взрывала repair/reconfigure ошибкой 1731 «source package out of sync», а блокировка даунгрейда била владельца ошибкой 10005 «A newer version is already installed».) Каждая сборка — новый продукт; `MajorUpgrade` с `AllowDowngrades="yes"` (поглощает same-version; WiX запрещает комбинацию — WIX0035) снимает установленное в ОБЕ стороны, поэтому любой кликнутый MSI устанавливается, а `config.ini`/`Data.dat` выживают (у старого продукта выставляется `UPGRADINGPRODUCTCODE`). Скрипт релиза валидирует, что маркер `ProductCode="*"` на месте.

## Цикл релиза

1. **Ничего руками в `Product.wxs` не крутить** — версия приходит define'ом `MsiVersion`, ProductCode автоматический.
2. **Закоммитить** рабочее дерево.
3. **Запустить релизный скрипт**: `.\scripts\make-release.ps1 -Version X.Y.Z -Push` (файл поддерживается релизным агентом; он валидирует закон ProductCode и пушит тег).
4. **Пуш тега `v*`** запускает `.github/workflows/release.yml`: CI собирает MSI и публикует GitHub Release с артефактом.
5. **Проверить артефакт** в GitHub Release: `iCemDataMonitor-<version>-x64.msi`.

Локальная альтернатива (без CI): `.\installer\build-msi.ps1` — без аргументов: версия берётся из `VERSION` в корне репо (bump = править файл, затем закоммитить); явная версия для конкретного релиза — `-MsiVersion X.Y.Z`. Полный цикл: publish host/shell/emulator (2026-09-02: framework-dependent — self-contained отменён декретом «передавать 3 файла»), сборка AddIn, harvest payload, `wix build` → `dist\iCemDataMonitor-<version>-x64.msi` + копирование двух рантайм-установщиков из `installer\` в `dist\` (handoff-бандл), затем stage и bin/obj вычищаются. Пререквизиты — Windows x64, .NET 10 SDK (см. `global.json`), WiX v5 (`dotnet tool install --global wix --version 5.0.2`; расширения Util/UI 5.0.2 доставляются скриптом).

## Чек-лист приёмки MSI

Кратко; полный гейт проходит через мастер в каждом сценарии (конфликт-гейт показывается всегда):

- **Чистая машина**: Windows 10 1809+ x64; сначала оба рантайм-установщика из `dist\` (aspnetcore-runtime + windowsdesktop-runtime — 2026-09-02: MSI больше не self-contained), затем MSI; после установки — `http://localhost:8090` отвечает, `iCemDataMonitor.Host.exe snapshot` возвращает JSON (exit 0).
- **Апгрейд с сохранением данных**: установка новой версии поверх старой — `config.ini` и `Data.dat` в `C:\ProgramData\Halliburton\iCem\DataMonitoring` выживают; payload перезаписан целиком (REINSTALLMODE=amus).
- **Деинсталляция с wipe**: `msiexec /x` удаляет всё, включая runtime-созданные `config.ini`, `Data.dat`, `Logs\`, `emule_config.xml`, кэш WebView2 (WipeMonitorRoot); перезагрузка не требуется никогда (RebootPrompt=no).

См. также: [install.md](install.md) (семантика установки/апгрейда/деинсталляции), [development.md](development.md) (пререквизиты сборки).
