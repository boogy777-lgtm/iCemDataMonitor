# План миграции

План Б: разработка в `rust/` существующего `boogy777-lgtm/iCemDataMonitor`.
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

## E9: законченный fault/reconnect инкремент

Boot подписка наблюдается после READY; fresh replay заменяет стадии; boot/status fault
освобождает Shell transport session. Host boot sends отменяются по shutdown при
backpressure. Consuming NavigateUi больше не ретраится по ошибке/status reconnect;
show-during-flight сохраняется. [Политика, тесты и границы](HOST_E9.ru.md).
Это частичное закрытие M3/M6, не полный PASS. Следующий Host-инкремент: отдельный
read-only sniffer capture owner, pause/resume/counters и actual availability projection.
Далее production CLI/ensure-host/config recovery и console/window lifecycle; MSI после
измеренного payload и Windows install/upgrade/uninstall. Legacy CI не приоритет миграции.

## Windows development MSI: N → N+1

`tools/Build-WindowsRelease.ps1 -Version x.y.z` теперь формирует отдельный Rust
development MSI из проверенного payload, с собственным UpgradeCode и исходным ProgramData-путём. `installer/README.md` описывает ограничения и manual Actions → next patch →
draft prerelease. Политика legacy MSI не меняется. Windows workflow проверяет
install N → upgrade N+1 → запрет downgrade → uninstall; пользовательский файл
сохраняется при upgrade и uninstall; удаляются только MSI-owned ресурсы. N+1 probe использует те же EXE, поэтому проверяет механику MSI, а не
изменения функциональности. M7 целиком не закрыт: production cutover/загрузка AddIn в iCem, signing,
rollback/failure/offline приёмка на чистой VM и running-process policy остаются открыты.

### M7: полный состав и legacy оформление упаковщика

Обязательны Rust runtime + исходный net48 AddIn, provenance/hash/metadata guard и
исходное оформление Бурсервис. Rust builder переиспользует исходные assets/bitmap generator и harvester;
SDK/DLL нельзя пропускать. Windows installer-contracts проверяет реальные MSI
операции на синтетических файлах без выпуска fixture MSI; реальный релиз отдельно
требует SDK-built DLL, полные Rust gates и runtime smoke/browser проверки. Пути и
opt-in опции сохранены. После [аудита v3.1](INSTALLER_AUDIT.ru.md) Rust package
не копирует WMI/kill/wipe/hidden-ARP политику legacy: используются native MSI
ownership и отдельный component GUID namespace; unowned данные сохраняются. Автоматический cutover установленного .NET MSI заблокирован.
Полная M7/визуальная/аппаратная приёмка этим изменением не закрыта.


### M7 clarification: повторный запуск и разрешённое force-закрытие

Установка ориентирована на оператора без IT-навыков. MSI восстанавливает сохранённые
пути/опции и повторным запуском ремонтирует отсутствующие/испорченные owned files.
Native Restart Manager закрывает владельцев затрагиваемых файлов; force — явная
несохраняемая опция, Cancel не является командой kill. Windows contracts проверяют
same-name foreign process isolation, grace/force, plain repeat repair и opt-out.
Реальные iCem/службы/другие sessions, reboot и first-run Host/config остаются gates.
Legacy и Rust упаковываются в разные MSI. См. INSTALLER_AUDIT.ru.md.

## E10: read-only capture vertical

[HOST_E10](HOST_E10.ru.md) реализует capture → lifecycle → actual status →
existing SetLayer RPC → native availability → software fault tests.
Следующий Host-инкремент: production CLI/ensure-host, затем config recovery и
console/window lifecycle. Оставшийся sniffer backlog: decode/signals и аппаратная
приёмка; dormant REST/file capture не считается реализованным API.
Windows packaging остаётся в отдельном PR #4/rust/windows-release.

E11: [первый запуск/ensure, CLI, config discovery и MSI ACL](HOST_E11.ru.md).
Shell автоматически запускает отсутствующий Host один раз; reconnect не повторяет
spawn. Строгая повреждённая конфигурация сохраняется и блокирует запуск; отсутствующая
создаётся из схемы. CLI/partial software evidence не закрывают SDK/UI/hardware gates.

## E12: управляемые логи (актуальное дополнение)

[HOST_E12](HOST_E12.ru.md) и [ADR 0008](adr/0008-managed-file-logs.ru.md):
общий файловый owner, native ON/OFF/clear для Host/Shell и независимого emulator,
сохранённая настройка, bounded rotation и безопасная частичная очистка. Frozen
proto/web/emulator contracts неизменны; internal JSON HTTP/2 control v1 доступен
только на существующем Host pipe. Mapping: 176 legacy + 6 explicit extensions,
visual parity NOT_PROVEN. P27 остаётся PARTIAL до Windows/operator/hardware gates.
Console-close/window lifecycle — следующий самостоятельный инкремент.

## E13: частичный console/window lifecycle

[HOST_E13](HOST_E13.ru.md): единый owner видимости, headless attach,
actual acknowledgement и graceful Ctrl+C. Console X → hide требует отдельной
архитектуры окна: legacy callback не отменяет CTRL_CLOSE. Этот gate и оставшиеся
window lifecycle/operator сценарии открыты; весь P10/P15 не объявляется завершённым.

## E14: emulator window lifecycle

[HOST_E14](HOST_E14.ru.md): X/Open сохраняет HWND; UI close не ждёт backend
в callback, in-flight завершается владельцем после UI loop. Windows operator gate
остаётся открытым; Console Host X → hide требует ADR 0009. P10/P15 остаются PARTIAL.


E15: [Host console X → Hide](HOST_E15.ru.md), [ADR 0011](adr/0011-host-diagnostic-window.ru.md).
Собственное диагностическое окно; frozen API и E14 сохранены. Ctrl+C относится к
окну Host, после detach родительский терминал не адресует Host. Windows/operator
результаты фиксируются по exact head; полная parity не объявляется.

Актуальный порядок оставшегося кода и scope decisions: [HOST_E15, следующая работа](HOST_E15.ru.md#следующие-работы-код-и-отдельные-gates). В legacy status selfTest тоже null; пробел — app --selftest/--landiag, а не сам JSON null. Capture-to-file production wiring не найден; dormant endpoints не активируются.


E16: [Host passive decode/catalog](HOST_E16.ru.md). Capture owner выполняет
Host-specific decode и bounded cache/tracking; periodic witness идёт в общий logger.
Dormant REST/file capture не активированы; hardware/visual parity не объявляется.


E17: [типизированный application API emulator и общий watch client](EMULATOR_E17.ru.md),
[ADR 0014](adr/0014-emulator-application-api.ru.md). GPUI передаёт Command напрямую;
pipe v1 остаётся compatibility adapter, включая coercions и исторический save ACK.
Внутренний результат отделяет apply/save failure; CLI text/JSON watch получает поток,
а не только ACK. Software CI не закрывает operator/SDK/hardware gates.


E18: [типизированный Shell → Host](HOST_E18.ru.md). Проверенные параметры/status,
generated replies, общий bounded worker. Logging/dashboard JSON — следующие
границы; visual/SDK/hardware gates остаются открытыми.


E19: [типизированное управление логами](HOST_E19.ru.md): Command/Status/Reply,
Incomplete effects и Unknown без retry, строгий неизменённый internal v1 adapter.
Локальные clear counts не выводятся из v1 текста; GPUI получает typed facts.
176+6 mapping прежний; Windows exact-head и operator/SDK/hardware gates отдельно.


E20: [типизированные ответы emulator](EMULATOR_E20.ru.md),
[ADR0017](adr/0017-typed-emulator-payloads.ru.md): 21 concrete backend payload families,
private ControlResult, typed Execution → derived GPUI View/Signal identity; JSON только
на v1 adapter/display formatting, без response roundtrip. Полные 43 E19 wire frames,
parser C# domain-shaped fixtures и mismatch/nonfinite/partial/save-failure checks.
D49: dashboard status отображает уже typed Model.host, сохраняя last-known при fault.
Dashboard REST/selftest/landiag и visual/operator/SDK/hardware gates остаются открытыми.


E21: [проверенные dashboard snapshots](DASHBOARD_E21.ru.md),
[ADR0018](adr/0018-typed-dashboard.ru.md): owned Registers/History через Shell и GPUI,
полная geometry/value/UTC/liveness validation, independent history failure и
current-selection filtering. REST/SPA/proto, appearance и 176+6 mapping прежние.
Software/exact-head Windows evidence фиксируется отдельно; physical/SDK/hardware
acceptance остаётся OPEN. Далее selftest/landiag и stdin CLI отдельными increments.


E22: [встроенный emulator --selftest](EMULATOR_E22.ru.md): полный mapping T1–T26, first-argument dispatch до startup, read-only EXE-relative emulator catalogs, isolated native TestCtl и полный i32 failCount. Windows process/release smoke tests добавлены; local Linux не доказывает Windows runtime. D22/operator/SDK/hardware gates открыты; E23 — explicit --landiag и stdin CLI.


CI-2a / D54: [canonical INI read/create race](CI2_CONFIG_RACE.ru.md), отдельный
prerequisite CI-1. Только исходный canonical NotFound разрешает defaults; strict
validation/no-clobber и legacy migration сохранены. Локальный red/green не заменяет
обязательные exact-head Windows gates; E23 и ensure-host вне этого изменения.


CI-2b / D55: [synthetic register mirror fixture и CI diagnostics](CI2_TEST_INFRA.ru.md).
Future-clock test fixture отделён от настоящего fresh producer; stale endpoint control
и exact 5000 ms health matrix сохранены. Production .NET неизменён; окончательные
Windows gates и отдельный review обязательны. E23/ensure-host вне изменения.
