# План миграции

План Б: разработка в `rust/` существующего приватного `boogy777-lgtm/iCemDataMonitor`.
.NET-проекты и AddIn в корне сохраняются. M0 — каркас и контракты; M1–M8 — этапы приёмки.
Реализованы Modbus-инкремент M4 (codec/mirror/TCP/dev host) и часть M2:
Windows MMF → Modbus, строгий read-once INI parser. [Статус M2](M2_STATUS.ru.md).
Его [границы](IMPLEMENTATION_STATUS.ru.md) не означают прохождение полного gate M4.

| Этап | Результат и границы | Зависит от | Доказательство завершения |
|---|---|---|---|
| M0 | Workspace, baseline, спецификация API, API inventory, план, безопасные pure codecs | Доступ к source | Hash manifest исходного commit; cargo fmt/check/test/clippy; папка rust/ опубликована |
| M1 | Characterization harness, baseline MSI/EXE size, UI screenshots/traces, dependency/UI spike, решения D01–D12 | M0 | .NET golden traces на Windows; реальные данные только в private fixtures; budget и отклонения явно записаны |
| M2 | Windows MMF reader + config.ini read-once + XML defaults, typed errors и liveness | M1 | Старый AddIn → Rust snapshots; matrix mutex/ACL/absent/recreate/restart; байты идентичны под lock |
| M3 | Host lifecycle, native tray/console/events/autostart, CLI dispatch, shell launch | M2 | Меню и иконки сохранены; multi-instance, stop/restart, Explorer restart; host не трогает emulator |
| M4 | Modbus server + четыре REST + gRPC/pipe + gRPC-Web + история и SPA | M2/M3 | Старые CLI/shell/SPA работают с Rust; все FC/ошибки/ranges; golden responses и reconnect |
| M5 | Самостоятельный Rust emulator: engine, DFB, COM/FLECS, ACE raw LAN, registries/units/overrides, pipe/CLI | M1/M2 | Byte-for-byte кадры, COM lines, контрольные команды; динамика DFB при заданном dt; no auto-stream default |
| M6 | Rust shell и UI emulator, статус/графики/настройки/темы/вкладки, два независимых трея | M3/M4/M5 | Сценарии операторов и UI acceptance на Windows/DPI; никаких .NET10/WinAppSDK зависимостей |
| M7 | Rust MSI, upgrade/rollback, logs, portable diagnostics, measurement | M4/M5/M6 | Чистая Windows VM: install/upgrade/uninstall/offline; полный размер меньше .NET baseline |
| M8 | Итоговая функциональная и нагрузочная приёмка, release candidate | M1–M7 | Все строки parity matrix PASS; CI на exact commit; нет непроверенных safety deltas |

## Порядок реализации пользователя — 2026-10-16

1. Web server: сохранённая SPA, REST/history, gRPC-Web и тема; затем оставшиеся API/приёмка.
2. Независимый emulator: engine/COM/ACE/CLI и GPUI UI с сохранением всех контролов.
3. Tray: независимые host/emulator icons, меню, console/lifecycle/autostart.

Milestone IDs выше остаются идентификаторами критериев, не прежним порядком задач.
GPUI — обязательный desktop backend. Web — прежние HTML/CSS/JS поверх Rust Axum/tonic.
Apple-style custom controls, light/dark, геометрия, typography, состояния/hover/focus,
keyboard/DPI и поведение сохраняются; generic UI kit не подменяет существующий дизайн.
[Web-инкремент](WEB_STATUS.ru.md). [Текущий emulator E1](EMULATOR_STATUS.ru.md).
Полного UI PASS до визуальной приёмки нет.

## Работа каждой задачи LLM

Входы: milestone, source-файлы, contract IDs, разрешённые Rust-модули.
До кода — Class/Inv/N+1 (2–4 строки). Выходы: patch + tests/fixtures + отчёт с
фактическими командами и результатами, обновлённый статус только подтверждённых строк.
Не писать «готово», когда есть лишь trait, TODO, always-success fake или текстовый план.
Новое изменение не должно требовать переписывания unrelated модулей.

## Обязательные проверки M1/M2

1. Golden .NET outputs: каждое RPC, 4 REST маршрута, CLI/stdout/exit,
   emulator text/JSON protocol и основные UI действия. Зафиксировать clock и seeds.
2. Фиксированный AddIn .NET48: x86/x64 фактического iCem, обычный/elevated token,
   Global namespace, DACL, abandoned mutex, timeout 500 ms, data-file deletion/recreation.
3. Host читает уже созданный producer-файл; не создаёт отсутствующую bus, не меняет ACL,
   не делает скрытую миграцию, не «лечит» AddIn новым форматом памяти.
4. Проверить read-only mapping API без Rust references на внешне изменяемую память;
   synchronisation и safe wrapper должны быть доказаны отдельно от codec.
5. Использовать исходный PCAP из корня репозитория и не подменять его пустым файлом или сгенерированной записью.

## Приёмка сетей и emulator

Modbus: FC03/04 (1..125), FC06, FC16 (1..123), MBAP fragmentation/coalescing,
partial reads, malformed/truncated lengths, range overflow, disconnect,
dynamic regCount, fresh/stale/active=0/reserve write conflict, long client sessions.
Valid requests должны совпасть с baseline. Parser defects не копировать небезопасно;
каждый intentional delta имеет ID, regression fixture и документированное поведение.

ACE: golden pcap, точные offsets/endian/LLC/SV, equipment/firmware, burst order и pacing.
COM: 32 поля FLECS, delimiters/newline/decimal culture, 1 Hz, port open/reconnect,
manual overrides, frozen output, enable/disable, unit conversions.
DFB: записать значения original при фиксированном dt/input sequence и сравнить Rust
с явно утверждённой числовой точностью; wire-format сравнивается побайтово.

## Приёмка UI и lifecycle

Host tray: оригинальный .ico, Открыть панель, Открыть Web, Показать/Скрыть консоль,
Перезапуск, Закрыть. Tooltip и caption выводятся из фактического состояния.
Close — снять icon до остановки loops, закрыть shell; emulator остаётся жив.
Emulator tray: Открыть iCemEmulator, Выход; закрытие окна сохраняет engine в трее.
Shell: Состояние и Панель; host ensure/reconnect, splash boot replay, лампы,
Modbus/sniffer, console/autostart, theme. Emulator: COM/LAN/Units и все controls.

## Выпуск и откат

До полного parity gate Rust имеет отличающиеся *-skeleton/dev имена и отдельный sandbox
для установки. Не запускать .NET и Rust host одновременно на одних ports/mutex.
Первый cutover: остановить старый host → установить совместимый Rust payload →
оставить ту же AddIn DLL/Data.dat/config → запустить и проверить. Откат возвращает
старый payload без изменения формата данных. MSI upgrade identity/пути утверждаются M7;
Rust CI имеет отдельный workflow; существующий .NET CI сохраняется.
