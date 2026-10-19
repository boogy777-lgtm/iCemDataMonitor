# E13 — управляемая видимость консоли и Ctrl+C

База: PR #8 слит по команде пользователя, main
`cf6bc282429f8732d279b93d67e9e44bf9a8659c`, tree
`79770936ce08b63dfbb329f0bad2122779d4ad82` совпадает с head #8.
Ветка E13 создана от этой базы. [ADR 0009](adr/0009-console-owner.ru.md).

## Поведение

Host startup, tray и прежний SetConsoleVisible используют единственный owner.
Show создаёт консоль для headless Host, восстанавливает стандартные устройства,
поднимает скрытое/свёрнутое окно; Hide сохраняет attachment и работающий Host.
Ответ подтверждает фактическую видимость; ошибки не дают фиктивного успеха.
`--autostart` скрывает консоль, ручной старт сохраняет исходное состояние.
Повторные Show/Hide, desktop disconnect/reconnect не создают второй owner.

Ctrl+C обрабатывается без I/O в callback, затем Host выполняет существующий
graceful teardown. Ctrl+Break и закрытие крестиком пока используют OS default.
**Console X → hide с сохранением Host не реализовано.** Legacy TRUE handler не даёт
такой гарантии Windows; основание и следующий дизайн записаны в ADR.
Закрытие нативной консоли может завершить Host без полного teardown.
Для фонового режима используйте Hide, для остановки — существующие Close/stop/Ctrl+C.
Foreground/focus/DPI/session/UAC и shell/modal window lifecycle требуют operator gates.

## Проверки и границы evidence

| Проверка | Evidence |
|---|---|
| Все шесть комбинаций None/Hidden/Visible × Show/Hide; повторение, born-hidden attach, потеря окна, OS refusal/error | portable policy tests |
| Ack/error, очередь 16, cancellation, shutdown и потерянный ответ без retry | portable WebState tests |
| Единственный native owner, Show/Hide ×3, повторная регистрация, inherited ignore reset и настоящий Ctrl+C ×2 | isolated Windows child test, пока NOT_RUN |
| Headless Host → RPC Show/Hide, новые подключения, настоящий Ctrl+C → exit 0, закрытые pipe/Modbus | Windows process test, пока NOT_RUN |
| Прежний SetLayer toggle и shutdown event + Show/Hide/GetStatus | расширен grpc_control Windows regression, пока NOT_RUN |

Windows GNU workspace all-targets Clippy `-D warnings` PASS (включая новые Windows
tests); native unit и два Host process test EXE собраны без запуска. Portable
console/platform/WebState/desktop/logging tests: 32 PASS, из них 7 новых. Это не исполнение Windows tests и не MSVC build.
Окончательные команды/CI exact head фиксируются в PR.
Baseline: 512/513 локальных файлов совпали по Git blob hash, 12 точных approvals;
недоступен original PCAP, сохранён в remote base tree без подмены. Mapping 176 + 6
сохранён без изменения UI; completeness не является visual parity.

После merge #8 Rust CI [39139360000](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/39139360000)
снова failure **до steps** (job 117710395482, steps null), logs BlobNotFound.
Причина запуска не установлена; ошибка исходников не доказана. Legacy CI не исправляется.
SDK не подтверждён; real release workflow не запускался. AddIn load, COM/Npcap,
installer reboot/rollback/offline, sessions и Windows operator acceptance NOT_RUN.

PR #9 затем слит по отдельной команде пользователя; main
abd9055d65a9ca0f867e792a685287dcd473d3b0. Владелец сообщил об исчерпанном лимите
Actions. Неисполненные Windows tests остаются NOT_RUN. [Продолжение E14](HOST_E14.ru.md).
