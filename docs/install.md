# Установка: iCem Data Monitor MSI

Артефакты в `dist\`: `iCemDataMonitor-<version>-x64.msi` (WiX v5) + два рантайм-установщика. (2026-09-02, owner decree «убераем self contained — дешевле передавать core\runtime, важнее меньший размер файлов»: handoff = **3 файла**, отменяет схему 2026-08-31 «.NET встроен в payload».) MSI НЕ несёт .NET: host/shell/emulator — framework-dependent; Windows App SDK по-прежнему self-contained. Перед установкой MSI на чистую машину ставятся оба рантайма из `dist\`: `aspnetcore-runtime-*-win-x64.exe` (покрывает host: AspNetCore.App + NETCore.App) и `windowsdesktop-runtime-*-win-x64.exe` (покрывает WinUI-приложения). Оставшийся внешний рантайм — WebView2 Evergreen Runtime (встроен в Windows 11 и почти на всех Windows 10).

## Требования

- Windows 10 1809+ x64 (минимальная версия платформы 10.0.17763).
- .NET-рантаймы 10.0.x x64 — двумя установщиками из `dist\` (см. выше); ставить ДО запуска приложений монитора (порядок относительно MSI не важен).
- WebView2 Evergreen Runtime — см. выше; отдельная установка обычно не требуется.
- Права администратора (Scope=perMachine).

## Пути установки

| Что | Куда |
|---|---|
| Payload (host, shell, emulator — плоско, exes — соседи на одном уровне), `Data.dat`, runtime-созданный `config.ini`, логи | `C:\ProgramData\Halliburton\iCem\DataMonitoring` (MONITORROOT) |
| AddIn dll `Burservis.iCem.DataMonitor.dll` | `[ICEMDIR]\Bin\Modules\`; ICEMDIR по умолчанию `C:\Program Files\Halliburton\iCem`, существующая установка iCem детектируется автоматически; если iCem не найден — мастер спросит путь |

Папка iCem (ICEMDIR) и папка монитора (MONITORROOT) — независимые пути. `config.ini` в MSI не входит — host создаёт его при первом запуске (см. [config.md](config.md)).

Опционально, через чекбоксы в мастере (по умолчанию выключены):

- ярлык на рабочем столе (все пользователи) на `iCemDataMonitor.WinUI.exe`;
- автозагрузка: HKCU Run-значение `iCemDataMonitorHost` = `"[MONITORROOT]iCemDataMonitor.Host.exe" --autostart` (тот же ключ, которым управляет и сам host — одно значение, одна истина).

## Что делает установка при живом iCem / мониторе

Перед каждым сценарием (установка, апгрейд, ремонт, деинсталляция) мастер показывает диалог проверки конфликтов. Конфликт (закон 2026-09-01 PM, после bug report «проверка конфликтов не обновляется» на 1.4.3) — это:

- **процессы с точным именем образа**: `iCem.exe` и стек монитора (`iCemDataMonitor.Host.exe`, `iCemDataMonitor.WinUI.exe`, `iCemEmulator.exe`, legacy `iCemDataMonitor.Cli.exe`). Правило «имя содержит icem» отменено: оно ложно цепляло сервисные процессы `Halliburton.iCem.*` (LogUpload/SyncService/Web), которыми владеет SCM — гейт не мог «зеленеть» никогда;
- **службы**, у которых `Name` или `DisplayName` начинается с `iCem`, — но только в работающем состоянии (Running / Start Pending / Continue Pending). Остановленная служба конфликтом не является.

Два выхода:

- **«Закрыть всё»** — установщик сначала останавливает конфликтующие службы (StopService; Start Pending отменяется SCM), ждёт 3 с, затем завершает процессы из точного списка (включая iCem.exe — только по явному выбору оператора) и продолжает, когда всё чисто;
- **«Обновить»** — оператор закрывает процессы/службы вручную, проверка повторяется честно (свойство пересчитывается на каждый клик); «Далее» активна только на чистом состоянии.

Отдельно: при подтверждённой отмене мастера (CancelDlg → «Да») применяется та же логика закрытия — ничто, что мастер трогал, не переживает его; при «Нет» проверка обновляется.

Дополнительно (утилита CloseApplication, до копирования файлов) закрываются процессы монитора: сначала WM_CLOSE, при неудаче — принудительное завершение:

- `iCemDataMonitor.Host.exe`
- `iCemDataMonitor.WinUI.exe`
- `icemdatamonitor-cli.exe` (legacy — запись оставлена, чтобы апгрейды с версий ≤ 1.3.x закрывали старый cli до удаления его файлов)
- `iCemEmulator.exe`

`RebootPrompt=no`: перезагрузки нет никогда — MSI завершает процессы принудительно и не требует рестарта.

## Тихая установка

```bat
msiexec /i iCemDataMonitor-<version>-x64.msi /qn /l*v install.log
```

Публичные свойства для тихого режима: `CREATEDESKTOPSHORTCUT=1`, `ADDAUTOSTART=1` (чекбоксы опциональны, по умолчанию выключены), `ICEMDIR="C:\Program Files\Halliburton\iCem"` (путь для AddIn, если iCem не установлен в дефолтное место). Лог — `install.log` рядом с MSI.

## Апгрейд

- MajorUpgrade (Schedule=afterInstallValidate, AllowDowngrades) снимает установленную версию перед раскладкой файлов — в ОБЕ стороны: апгрейд, переустановка той же версии и даунгрейд устанавливаются одинаково чисто (2026-09-01 PM: даунгрейд разрешён, ошибка 10005 отменена).
- `REINSTALLMODE=amus` — каждая установка поверх перезаписывает весь payload целиком (семантика «думмого экстрактора»: файлы всегда когерентны).
- **Конфиги и `Data.dat` СОХРАНЯЮТСЯ** при апгрейде: очистка папки MONITORROOT выполняется только на настоящую деинсталляцию (условие `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE` — апгрейд не проходит по нему).

## Деинсталляция (полная)

Деинсталляция **полная**: deferred custom action `WipeMonitorRoot` стирает всю папку `C:\ProgramData\Halliburton\iCem\DataMonitoring` — включая runtime-созданные `config.ini`, `Data.dat`, `Logs\`, `emule_config.xml`, кэш WebView2. MSI удаляет и свои нативные следы (ярлык, HKCU Run-значение автозагрузки, флаг «отключено» в StartupApproved). Ошибки очистки не откатывают деинсталляцию (Return=ignore).

Свойство `ARPSYSTEMCOMPONENT=1` скрывает продукт из «Программ и компонентов»; деинсталляция выполняется msiexec:

```bat
msiexec /x iCemDataMonitor-<version>-x64.msi /qn /l*v uninstall.log
```

(либо по коду продукта — но ProductCode автогенерируется на каждую сборку, см. [release.md](release.md), поэтому практичнее форма с файлом MSI).

Примечание: папка AddIn `C:\Program Files\Halliburton\iCem\Bin\Modules\` и сама установка iCem деинсталляцией монитора не затрагиваются; сам AddIn dll удаляется как файл, установленный MSI.
