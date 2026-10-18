# E12: управление файловыми логами из GPUI

База main `13dea0c161e24cf0cad32f84c80c941d0a1b2eee`, PR #7 слит;
открытого продолжения при старте не было. Ветка `rust/desktop-file-logs`.
Решение и точные гарантии: [ADR 0008](adr/0008-managed-file-logs.ru.md).

В «Состояние» Shell добавлены две группы: «Логи Host» и «Логи оболочки».
В независимом emulator — «Логи эмулятора». В каждой: native pill «Записывать логи»,
«Очистить логи», точный каталог, подтверждённая настройка, состояние writer и ошибка.
Это отдельные владельцы; очистка одной группы не удаляет файлы остальных.
Emulator настраивается в собственном окне, без запуска/остановки из Host.

Настройка применяется сразу и сохраняется в owner-local `rust-logging-v1.json`;
OFF не удаляет файлы, clear не изменяет настройку. Default ON сохраняет legacy.
Ошибки доступа/занятые файлы/частичная очистка/disk full не дают фиктивного успеха.
После потери ответа read-only poll сверяет truth, мутация не повторяется.
Последнее состояние при disconnect помечается неактуальным и не разрешает действия.

Прочитаны legacy StationFileLog, StationLogPaths/StartupLog, DataBusLog,
LogsCleanup, CrashLog и consumers; XAML/theme и inventory. Сохранены пути/UTF-8/UTC,
25 MiB cap/newest-half policy; новые deltas: целые записи, строгий cap без overshoot,
atomic setting/rotation, narrow allowlist retention вместо чужих *.log и явные ошибки.
Legacy `LogsCleanup` для exe/logs не имел active caller; он не является разрешением
удалять ProgramData или файлы AddIn. Подробная таблица владельцев в ADR.

## Проверки

- Log owner: off→on→write→off, restart persistence; clear ON/OFF/repeat,
  concurrent append/clear + bounded rotation, partial locked-file clear с продолжением
  записи, competing owner, hardlink/junction boundary, unavailable directory,
  readonly log/locked settings, malformed preference, incomplete tail и retention.
- Injected ERROR_DISK_FULL после частичной записи проверяет rollback и отдельный
  отказ rollback. Это детерминированная I/O seam, не испытание заполненного тома.
- Model: no optimistic switch, pending/unknown outcome, last-known disconnect,
  fresh reconnect, sticky partial failure. Общий renderer использует прежние theme
  tokens/pill geometry, Tab/Enter/Space; проверка каждого состояния на Windows ещё нужна.
- Local extension: body/type/unknown-field refusal, отсутствие control в web router;
  Windows настоящий pipe + file owner, persisted OFF через stop/rebind и потерянный
  ответ **после** мутации без повторного set.
- Frozen controls: прежние 176, отдельно 6 native extensions; mapping guard проверяет
  обе inventory. Completeness не означает visual parity.

Результаты локальных команд и exact-head Windows CI фиксируются в PR.
SDK/AddIn load, COM/Npcap, DPI/focus, sessions/UAC, reboot/rollback/offline остаются
NOT_RUN. Реальный release workflow не запускается без подтверждённого SDK.
MSI/force/Repair/ensure-host/config ownership этим изменением не меняются.
Следующий отдельный приоритет: console-close guard и lifecycle окон.

## Диагностика CI базы

Rust post-merge 39135015892: failure, job 117697652870, steps отсутствуют;
job logs повторно вернули BlobNotFound. Check-runs metadata имеет одну annotation,
но её содержимое не доступно через имеющийся connector endpoint. Legacy
39135015907 также завершился до steps; он не изменялся и не перезапускался.
Причина отказа запуска **не установлена**, ошибка исходников не доказана.

## Фактическая проверка PR #8

Локально: Rust 1.99.1; Windows GNU `cargo check --workspace --all-targets` и
`cargo clippy --workspace --all-targets -- -D warnings` по locked/offline graph PASS.
41 portable тест selected `icem-logging`, `icem-desktop`, `icem-web` PASS; fmt PASS.
PowerShell Scaffold/Chrome/MSVC runtime здесь не выполнялись. Прямое сравнение
Git blob hashes baseline: 512/513, 12 exact approvals, без расхождений; original PCAP
не доступен локально, но сохранён в base tree GitHub. Mapping 176 + 6 подтверждён.

PR [#8](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/8), первый head
05ddc846ebdc21ddefa90a97f34e765bdb815a5c. Windows Rust CI
[39137774227](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/39137774227)
снова завершился failure до steps, как на main. Одна явная повторная попытка
(verify 117706712052) также завершилась без steps. Logs первой попытки BlobNotFound.
Причина annotation остаётся неизвестна: connector не отдаёт её текст; cloud browser
показывает private-repository 404 и Sign in, а не содержимое annotation.
Legacy run 39137774275 не исправлялся и не перезапускался.

Новые Windows filesystem/pipe tests **скомпилированы, но не исполнены**. Дополнительная
попытка запуска собранного GNU test EXE через Wine остановилась до тестов:
`wineserver: socket: Operation not permitted`. Wine не заменяет Windows acceptance.
На этом этапе PR оставался draft до реального MSVC CI. Нельзя трактовать failure до steps как
ошибку исходников или писать PASS для этих Windows scenarios.

PR #8 затем слит по отдельной команде пользователя; main cf6bc282429f8732d279b93d67e9e44bf9a8659c.
Windows runtime gates выше остаются открытыми; продолжение — [HOST_E13](HOST_E13.ru.md).
