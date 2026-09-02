# Аудит UAC / MMF data.dat (2026-08-27)

> Назначение файла: нормативный аудит-артефакт для будущих обновлений.
> Источник: explore-аудит кода + решение владельца 2026-08-27 (producer-only doctrine, INV-39).
> Статус решения: реализовано (host read-only), см. `specs/dotnet-server/Э4_invariants.yaml` INV-39.

## Обнаруженные точки создания/открытия MMF (на момент аудита)

| # | Файл | Процесс | Путь data.dat | Имя маппинга | Доступ | ACL | IL |
|---|---|---|---|---|---|---|---|
| 1 | `Burservis.iCem.DataMonitor\BusWriter.cs` | iCem.exe (AddIn, net48) | `C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat` | `Global\iCemDataMonitorBus_v1` | ReadWrite | InstallUsersModifyDacl (BUILTIN\Users → Modify на файл) + наследуемый DACL на каталог | asInvoker |
| 2 | `server-dotnet\iCemDataMonitor.Data\Bus\BusMapping.cs` (старый OpenOrCreate) | Host.exe | тот же | тот же | ReadWrite + ReadWrite-проба | нет | asInvoker |
| 3 | `server-dotnet\iCemDataMonitor.Data\Bus\BusReader.cs` | Host.exe | тот же | тот же | Read (снапшот) | нет | asInvoker |
| 4 | `BusZoneWriter.cs` (удалён) | Host.exe | тот же | тот же | ReadWrite (создание файла) | InstallUsersModifyDacl | asInvoker |
| 5 | `_wireprobe\Program.cs` | инструмент | — | тот же | Read | нет | — |

- Все исполняемые файлы **asInvoker** (манифесты без `requireAdministrator`).
- Файл живёт в `%ProgramData%` (не в Program Files).
- Инсталлятор: WiX `Scope="perMachine"` (elevation только при установке), приложения НЕ помечены elevated.

## Производственный инцидент (INC-02)

AddIn, запущенный elevated, создал `Data.dat` с DACL `Users=ReadAndExecute` →
host-потребитель (Medium IL) не мог открыть **ReadWrite**-вью ⇒ `BusAccessDeniedException`.
Класс проблемы: **write-асимметрия** между elevated-продюсером и не-elevated потребителем.

## Анализ безопасности

- DACL именованной секции при `CreateFromFile` **не наследуется от файла** — берётся token-default DACL создателя.
- Права на **write**-вью дополнительно проверяются по DACL **файла** (поэтому INC-02 ловился именно на записи).
- Чтение (Read) против файла с `Users=ReadAndExecute` **разрешено** — read-up не блокируется MIC.
- Default-DACL elevated-токена не гарантирует доступ другому аккаунту даже на чтение (cross-user вне модели same-user/same-session).

## Вердикт по рискам (матрица)

| Создатель | Потребитель | Риск | Итог |
|---|---|---|---|
| Medium (AddIn) | Medium (Host) | нет | ✅ |
| HIGH (elevated AddIn) | Medium (Host, ReadWrite) | высокий — INC-02 | ❌ было |
| HIGH (elevated AddIn) | Medium (Host, **Read-only**) | снят (read разрешён) | ✅ после INV-39 |
| Medium | HIGH | средний (файловый ACL) | ⚠️ теоретический |
| разные пользователи | — | высокий (token-default DACL) | ❌ вне модели |

## Принятое решение (decree владельца 2026-08-27)

**Producer-owned bus**: DLL AddIn = единственный источник потока; все остальные
участники читают, не перенаправляют; эмулятор = независимый n+1 источник (Data.dat
не трогает). Реализовано:

- Host: `BusMapping.OpenReadOnly` (FileAccess.Read + Read-вью, sized READ-проба),
  `BusReader` без миграции; `BusZoneWriter`/`LanBusSink`/`ISnifferDataSink`
  УДАЛЕНЫ; миграция v2–v4→v5 принадлежит AddIn (`BusWriter.UpgradeToV5FullInPlace`).
- Спецификации: `specs/dotnet-server/Э4_invariants.yaml` INV-39 + правки
  INV-01/04/05/06/07/13/20/23/24/25/26; `specs/iCemAddIn/Э4` + `Э0`;
  `specs/dotnet-server/Э0`; `specs/TZ-DOCSYS-001`.

## Рекомендации (приоритезированные)

**P1 — сделано кодом:** DACL при создании файла ставит только продюсер (AddIn);
host-write-поверхность удалена полностью.

**P2 — желательно при следующих обновлениях:**

1. Явный `<requestedExecutionLevel level="asInvoker" uiAccess="false"/>` во ВСЕХ
   манифестах (Shell.WinUI и iCemEmule.WinUI сейчас без явного уровня — Windows
   по умолчанию asInvoker, но явность страхует от случайного elevation в будущем).
2. Создать каталог `%ProgramData%\Halliburton\iCem\DataMonitoring` через WiX
   (`CreateFolder` + `Permission` BUILTIN\Users → Modify, наследуемый) — «ACL по
   рождению», чтобы ЛЮБОЙ будущий создатель файла получал корректные права.
3. Вердикт D.6 остаётся DECLINED: явный `MemoryMappedFileSecurity` (BUILTIN\Users
   ReadWrite на секции) НЕ вводим — расширяет read-доступ на все локальные аккаунты
   (ослабление на общих ригах); при read-only host он не нужен. Re-trigger: появление
   сценария cross-user (другой аккаунт-потребитель) или сервисной (session-0) топологии.

**P3 — известная оговорка ordered-restart:** секция, ВПЕРВЫЕ созданная reader-ом,
рождается read-only ⇒ после полной остановки станции продюсер (iCem) должен
запускаться ПЕРВЫМ (runbook INV-26: stop consumers → start producer → start consumers).
Файл/секция при штатной работе создаются AddIn-ом — оговорка касается только
cold-boot с существующим файлом и остановленным iCem.
