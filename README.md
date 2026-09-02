<!-- Бейджи-заглушки: после первого пуша в GitHub подставьте реальные владельца и имя репозитория в обе строки бейджей ниже. -->
[![Сборка MSI](https://github.com/OWNER/REPO/actions/workflows/release.yml/badge.svg)](https://github.com/OWNER/REPO/actions/workflows/release.yml)
[![Релизы](https://img.shields.io/github/v/release/OWNER/REPO?label=release)](https://github.com/OWNER/REPO/releases)

# iCem Data Monitor

Монитор данных станции iCem (Halliburton/Burservis): принимает поток данных от iCem-эмулятора (LAN ACE / COM), ведёт шину данных на memory-mapped file и публикует её в Modbus TCP, веб-дашборд и WinUI-оболочку.

## Поток данных

```
+-------------------+  COM (com0com)   +---------------------------+
|  iCem-эмулятор    | ---------------> | iCem.exe + AddIn (net48)  |
|  (iCemEmulator)   |  LAN (ACE raw    | в процессе iCem: байт-    |
+-------------------+  802.3, 1 Гц)    | точный транслятор         |
         |                             +-------------+-------------+
         |  LAN (ACE, сниффер)                       | регистры шины
         v                                           v
+--------------------------------------------------------------------+
|   Шина: MMF Global\iCemDataMonitorBus_v1 + Data.dat (10001 рег.)   |
+---------------------------------+----------------------------------+
                                  |
                                  v
+--------------------------------------------------------------------+
|  iCemDataMonitor.Host.exe (.NET 10 BackgroundService)              |
|   - Modbus TCP publisher :502 (FC03/FC04)                          |
|   - Kestrel web-дашборд :8090 (SPA + gRPC-Web)                     |
|   - LAN-sniffer (ACE-кадры прямо из провода)                       |
+---------------------------------+----------------------------------+
                                  |
                    +-------------+--------------+
                    v                            v
        +----------------------+    +---------------------------+
        | WinUI 3 shell        |    | браузер                   |
        | iCemDataMonitor.     |    | http://localhost:8090     |
        | WinUI.exe            |    | (gRPC-Web + REST)         |
        +----------------------+    +---------------------------+
```

## Возможности

- **Шина данных** — MMF `Global\iCemDataMonitorBus_v1` (10001 регистр) с файловой подложкой `Data.dat`; пишет AddIn внутри iCem.exe, читают host и CLI.
- **Modbus TCP :502** — публикация шины SCADA-мастеру (FC03/FC04), обновление каждые `modbus_tick_ms` (по умолчанию 500 мс).
- **Веб-дашборд :8090** — Kestrel SPA + gRPC-Web + история регистров (кольцевой сэмплер 1 Гц); по умолчанию только loopback (`web_remote_on=0`).
- **LAN-сниффер** — приём ACE-кадров напрямую из сети (SharpPcap), независимо от iCem.
- **WinUI 3 shell** — клиент host'а (gRPC over named pipe); вкладка «Дашборд» открывает веб-SPA через WebView2; без диалогов «Запустить хост?» — shell сам запускает host'а.
- **CLI внутри host** — `iCemDataMonitor.Host.exe snapshot|get|header [--start]`; stdout = чистый JSON.
- **Один MSI** — полностью offline/self-contained (.NET 10 встроен в payload, WinAppSDK self-contained), без перезагрузок; единственный внешний рантайм — WebView2 Evergreen.

## Быстрый старт

1. Скачайте `iCemDataMonitor-<version>-x64.msi` со страницы Releases (ссылка — бейдж «Релизы» вверху; после первого пуша она станет рабочей).
2. Установите MSI (двойной клик) — подробности в [docs/install.md](docs/install.md).
3. Что где появится:
   - payload + конфиги + `Data.dat` — `C:\ProgramData\Halliburton\iCem\DataMonitoring`;
   - AddIn dll — `C:\Program Files\Halliburton\iCem\Bin\Modules\`;
   - `config.ini` в MSI не входит — host создаёт его при первом запуске.
4. Запустите `iCemDataMonitor.WinUI.exe` (shell) — он сам запустит `iCemDataMonitor.Host.exe`; статус-область shell показывает живость host'а.
5. Проверка:
   - открыть `http://localhost:8090` — веб-дашборд;
   - выполнить `iCemDataMonitor.Host.exe snapshot` — в stdout придёт JSON-массив слов шины.

## Сборка из исходников

Пререквизиты: Windows x64, .NET 10 SDK (версия из `global.json`), WiX v5 (`dotnet tool install --global wix --version 5.0.2`).

```powershell
dotnet test iCemDataMonitor.sln
.\installer\build-msi.ps1 -MsiVersion X.Y.Z
```

Параметр `-MsiVersion` обязателен; артефакт — `dist\iCemDataMonitor-<version>-x64.msi`. В скрипте нет абсолютных путей — репозиторий можно клонировать куда угодно.

## Структура репозитория

| Путь | Содержимое |
|---|---|
| Корень репозитория | Все проекты .NET 10 лежат в корне: headless host, WinUI shell, gRPC-контракты, шина/транспорт/конфиг + тесты; solution `iCemDataMonitor.sln` охватывает и эмулятор, и AddIn |
| `iCemEmulator\` | Независимое WinUI-приложение эмулятора станции (COM FLECS + LAN ACE) |
| `iCemEmule.Core\` | Движок эмуляции: эмиттеры FLECS COM и ACE LAN, реестры переменных |
| `iCemEmule.Tests\` | Тесты движка эмуляции |
| `Burservis.iCem.DataMonitor\` | AddIn dll (net48) внутри iCem.exe — байт-точный транслятор, пишет шину MMF + `Data.dat` |
| `Burservis.iCem.DataMonitor.Tests\` | Тесты AddIn |
| `installer\` | WiX v5 MSI: `Product.wxs`, `build-msi.ps1`, генераторы payload и битмапов |
| `specs\` | Спеки-закон: `INDEX.yaml`, топология стенда, `Э5_config.csv` (канон конфига) и др. |
| `templates\` | JSON-шаблоны реестров: `variable_registry.json`, `flecs_4.53.json`, `plc_basic.json` |
| `docs\` | Документация (этот набор, на русском) |
| `scripts\` | Релизные скрипты (`make-release.ps1` — файл поддерживается релизным агентом, появляется вместе с релизной инфраструктурой) |

## Релизы

Пуш тега `v*` запускает `.github/workflows/release.yml`: CI собирает MSI и публикует GitHub Release. Главный закон релиза: **каждый bump версии — новый ProductCode GUID в `installer\Product.wxs`** (ручной шаг; проверяется `scripts\make-release.ps1`). Цикл и чек-лист приёмки — [docs/release.md](docs/release.md).

## Документация

- [docs/install.md](docs/install.md) — MSI: установка, апгрейд, деинсталляция, тихий режим.
- [docs/config.md](docs/config.md) — `config.ini` (закон read-once, дефолты, миграция `station.ini`), `emule_config.xml`, `Data.dat`.
- [docs/architecture.md](docs/architecture.md) — компонентная карта, поток данных, шина MMF/Data.dat, веб-дашборд.
- [docs/release.md](docs/release.md) — релизный цикл и приёмка MSI.
- [docs/development.md](docs/development.md) — сборка, тесты, отладка стенда, известные ограничения.
- [PROTOCOL.md](PROTOCOL.md) — wire-протокол ACE (канонический формат кадра).
- [specs/INDEX.yaml](specs/INDEX.yaml) — входная точка спецификаций (спеки — закон).

Лицензия: TBD
