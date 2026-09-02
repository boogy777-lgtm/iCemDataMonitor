# Разработка

## Пререквизиты

- Windows x64 (проекты WinUI собираются только под Windows).
- .NET 10 SDK — версия pinned в `global.json` (10.0.302, `rollForward: latestFeature`).
- WiX v5 — только для сборки MSI: `dotnet tool install --global wix --version 5.0.2` (нужные расширения Util/UI скрипт MSI-сборки доставляет сам).
- com0com — только для полного стенда с COM-эмуляцией (пара COM-портов между `iCemEmulator` и iCem.exe).

## Сборка и тесты

**Первый запуск с нуля** (2026-09-01): установить .NET 10 SDK → открыть `iCemDataMonitor.sln` в IDE (или `dotnet build iCemDataMonitor.sln` в консоли) → Build. Отдельный `dotnet restore` НЕ нужен — первая сборка восстановит NuGet-пакеты сама (фиды по умолчанию, `nuget.config` в репо отсутствует специально: всё приходит с nuget.org).

```powershell
dotnet build iCemDataMonitor.sln
dotnet test iCemDataMonitor.sln
```

WinUI-проекты (`Shell.WinUI`, `Emulator.WinUI`, `iCemEmulator`) собираются с `-p:Platform=x64`. Полный MSI: `.\installer\build-msi.ps1` — без аргументов, версия берётся из файла `VERSION` в корне репо (bump = править этот файл); явная версия: `-MsiVersion X.Y.Z` (2026-09-02, owner decree «скрипт должен быть самодостаточным» — интерактивных запросов нет). Весь прогон пишется в `dist\iCemDataMonitor-<ver>-x64.build.log` — все warning'и MSBuild/WiX остаются читаемыми после закрытия окна; в конце (и при падении сборки) окно ждёт нажатия клавиши, `-NoPause` отключает ожидание. Артефакт в `dist\`, после сборки stage и bin/obj вычищаются автоматически. В скриптах сборки нет абсолютных путей: репозиторий можно клонировать/переименовывать куда угодно.

## Сборка из IDE (2026-09-01, owner request «человек собирает через средства разработки»)

Репозиторий полностью IDE-совместим — решение открывается как есть, без скриптов:

- **Visual Studio 2022 17.12+ / VS 2026.** Нужны компоненты: «.NET desktop development» + **Windows App SDK** (C# WinUI workload) + .NET 10 SDK (см. `global.json`). Открыть `iCemDataMonitor.sln` → Build Solution работает в любой конфигурации: WinUI-проекты и net48-AddIn в sln замаплены так, что `Any CPU` решения собирает их как `x64` автоматически. Тесты — через Test Explorer (xunit). MSI из VS не собирается — это скриптовый шаг (`installer\build-msi.ps1`, WiX v5).
- **VS Code.** Рекомендуемые расширения уже лежат в `.vscode/extensions.json` (C# Dev Kit). Готовые задачи в `.vscode/tasks.json`: `build: solution` (Ctrl+Shift+B), `test: solution`, `msi: build` (с вводом версии), harness конфликт-гейта установщика. Отладка host'а: `dotnet run --project iCemDataMonitor.Host` (консольное приложение); WinUI-приложения из VS Code не отлаживаются — для них Visual Studio.
- **Rider.** Открыть `iCemDataMonitor.sln` — то же, что VS (нужен .NET 10 SDK; WinUI-отладка под Rider на Windows поддерживается).

Стиль кода закреплён для IDE: `.editorconfig` (UTF-8, LF, 4 пробела C#) и `.gitattributes` (нормализация EOL, бинарники защищены) — человеческая IDE не будет перекрашивать файлы при первом сохранении.

## Отладка стенда

- **Пути данных** (MONITORROOT): `C:\ProgramData\Halliburton\iCem\DataMonitoring` — тут живут `config.ini`, `Data.dat` (файловая подложка MMF `Global\iCemDataMonitorBus_v1`), `emule_config.xml` (создаётся эмулятором) и `Logs\`.
- **Куда смотреть логи host'а**:
  - `Logs\iCemDataMonitor.Host\startup.log` — загрузка host'а, строки-свидетельства (в т.ч. миграция конфига, READY-маркер);
  - `Logs\iCemDataMonitor.Shell\shell-crash.log` — крэши WinUI shell'а;
  - `Logs\<AppName>\data-bus.log` — журнал шины (Data);
  - `Logs\iCemDataMonitor.Cli\cli.log` — диагностика CLI-режима (stdout всегда чистый JSON, stderr = логи).
- **Прогон локально**: `iCemDataMonitor.Host.exe [--port N] [--ini PATH] [--autostart]`; CLI поверх живого host'а: `iCemDataMonitor.Host.exe snapshot|get|header [--start]` (exit-коды: 0 ok, 1 RPC-ошибка, 2 usage/host не отвечает).
- **Правка конфига**: `config.ini` читается host'ом один раз при старте — после правки нужен рестарт host'а (см. [config.md](config.md)).

## Спеки — закон (amendment-first)

`specs\` — каноническое описание текущего состояния; входная точка — [../specs/INDEX.yaml](../specs/INDEX.yaml). Изменение поведения начинается с правки спека (amendment), код следует за ним. Ключевые файлы: [../specs/stand_topology.mmd](../specs/stand_topology.mmd) (топология стенда), [../specs/dotnet-server/Э5_config.csv](../specs/dotnet-server/Э5_config.csv) (каноническая таблица конфига), [../PROTOCOL.md](../PROTOCOL.md) (wire-формат ACE).

## Известные ограничения

- **LiveGateGui исключён из репо** (net8 ↔ net10 NU1201, мёртвый прототип); LiveGate-инструменты остались вне репо — полный гейт воспроизводится скриптово/вручную.
- **Trimming/self-contained host'а — ОТМЕНЕНЫ** (2026-09-02, owner decree «убераем self contained — дешевле передавать core\runtime»; отменяет «Блок 2 п.4» 2026-09-01): host публикуется framework-dependent win-x64; рантаймы ставятся на целевую машину двумя установщиками из `dist\` (см. [install.md](install.md)).
- Ретироуд-ключи `config.ini` (`com_port`, зонные ключи, `emule_on`) принимаются и игнорируются — сознательно, для совместимости живых файлов стенда.
