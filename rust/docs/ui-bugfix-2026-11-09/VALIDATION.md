# UI / protocol controls — Windows runtime bugfix

Запрос пользователя: отсутствуют переключатели OPC UA/MQTT, не работают
кнопки OPC DA, мерцают панели логов Host и оболочки. Авторизован рой Terra.

## Границы

- Только Rust/Windows; исходная .NET реализация — read-only evidence.
- Frozen protobuf/SPA/desktop inventory остаются неизменными.
- Новые протоколы управляются через существующий private Host pipe, не через
  новые публичные REST endpoints. Existing SetLayer сохраняет toggle-семантику.
- Нет произвольного broker address или скрытой регистрации COM: пользовательская
  конфигурация и диагностируемые capability обязательны.
- Production iCem/Host/config/ACL/registry не изменяются. Первоначальный
  process-тест агента до исправления изоляции мог дополнить обычные startup-логи
  Host; они не удалялись и не откатывались. Итоговые прогоны изолируют данные.

## Отчёты направлений

- [CONTROLS.md](CONTROLS.md): GPUI, отображение и подтверждение состояния.
- [PROTOCOLS.md](PROTOCOLS.md): private MQTT/OPC UA управление Host consumers.
- [OPCDA.md](OPCDA.md): OPC DA UI → runtime/sidecar.
- [LOGS.md](LOGS.md): фоновый refresh и мутации состояния логов.

## Итог реализации

- Три переключателя расположены рядом с Modbus/Sniffer. Положение отражает
  подтверждённый `enabled`; отдельно показаны работа/подключение/ошибка (`running`).
  При отсутствии настройки показан соответствующий CLI-флаг, а не фиктивный ON.
- OPC DA управляет именно owned sidecar через private API; legacy `SetLayer`
  и его INI-флаг не переопределены. Старый текст явно подписан как legacy INI,
  не факт работы сервера. Registry/DCOM installation по нажатию не выполняется.
- Фоновые GET логов и protocol status не переводят свежие кнопки в disabled и
  «выполняется». Мутации блокируют повтор до собственного ответа; потерянный ACK
  не вызывает автоматический повтор. Очередь Get → Set проверена отдельно.
- Disconnect инвалидирует protocol snapshot. Reconnect Status и запоздалые ответы
  не разрешают действия до нового актуального protocol Get.
- Страница состояния прокручивается; новые контролы не скрыты ниже блоков логов.

## Общий Windows gate

Application/build commit: `c7b0b45884f5af04e7ee969cdb15cd26ede0fb58`.
Последующие documentation/evidence commits не меняют исполняемый код пакета.

| Проверка | Результат |
|---|---|
| Baseline/frozen proto/SPA | PASS; 487 existing + 26 approved deletions |
| Desktop mapping | PASS; 176 legacy + 6 logging + 3 protocol controls; 13 guard-regressions |
| fmt/check/build/test/clippy `-D warnings` | PASS, Windows x64 Release, Rust 1.99.1 |
| Общий Rust suite | 389 passed в 95 группах; 2 opt-in tests ignored, не считаются PASS |
| Private pipe typed GET/PUT и lost-ACK/no-replay | PASS; три protocol branches, bounded diagnostics, queue/shutdown/malformed |
| Настоящий Host + UA listener через private pipe | PASS; start/stop/restart, busy bind, failed ACK, отсутствие DA config |
| Logs refresh / mutation state | PASS; 100 повторных GET на ON/OFF без изменения текста/доступности |
| SDK-backed MSI/Setup и SHA256 | PASS; версия 0.1.2 |
| Извлечённый ZIP / relocation / Host / emulator ACK / Shell loader | PASS |
| Реальный DA owner, opt-in | PASS, 1/1 отдельно с `--ignored`; два цикла READY/start/stop, без `--register` |

Первый общий run прошёл тесты, но Clippy потребовал let-chain вместо nested if;
исправлено без allowance. Второй run завершился полностью. Известное стороннее
future-Rust предупреждение `proc-macro-error2 2.0.1` не подавлялось.

Финальный build/test запуск передал дочерним fixtures отдельные PROGRAMDATA и
LOCALAPPDATA в `%LOCALAPPDATA%\Temp\opencode\icem-ui-build-data-e3b44e3ad97a401b9ca53c2476bffa5b`.
Изоляция нового process-теста также закреплена в самом тесте. Установщик
обнаружил установленную Rust **0.1.1**; install/uninstall не вызывались.
Transcript: `%LOCALAPPDATA%\Temp\opencode\build-icem-ui-20261109.log`.

Отдельный DA run на том же application head:
`cargo test --release --locked --offline -p icem-host-app --bin icem-host-dev optional_consumers::tests::real_da_sidecar_owned_start_and_stop -- --ignored --nocapture`.
Перед запуском проверено отсутствие уже работающего sidecar; использованы отдельные
PROGRAMDATA/LOCALAPPDATA. Получен настоящий READY и подтверждено удаление owner после
каждого stop. Это проверка COM class registration/IPC/lifetime самого sidecar,
не external vendor COM client/proxy gate. Другой ignored test
`actual_cross_process_class_factory_group_read_and_callbacks` в этой задаче не запускался.
Результат: [evidence/sidecar-result.json](evidence/sidecar-result.json).
Transcript: [evidence/sidecar-transcript.log](evidence/sidecar-transcript.log)
фиксирует wrapper PASS и exit; native Cargo stdout сохранён в завершённом
tool result, не в PowerShell transcript.

## Живая проверка пакета

Обычный не elevated Host из нового ZIP, собственные config/log dirs, pipe и
loopback-порты. При работающем iCem: config создан, busOpen=true,
stale=false, heartbeat **13307 → 13308**. После X собственного diagnostic HWND
и X настоящего Shell HWND Host остался доступен. Использован ранее сохранённый
`rust/docs/bugfix-2026-11-09/artifacts/Test-LiveHost.ps1` без изменения.
Evidence: `%LOCALAPPDATA%\Temp\opencode\icem-live-bugfix-95f2b74d12134112989875df82e43512`.
Краткий сохранённый результат: [evidence/live-result.json](evidence/live-result.json).
Этот smoke подтверждает загрузку GPUI и прежние MMF/lifetime свойства, но не
заменяет визуальную проверку всех переключателей оператором.

## Конфигурация и ограничения

Три adapter используют явные startup JSON configs:
`--mqtt-config FILE`, `--opcua-config FILE`, `--opc-da-config FILE`.
См. [MQTT](../MQTT_P6.ru.md), [UA](../OPCUA_P7A.ru.md),
[DA](../OPCDA_P7B.ru.md) и `rust/docs/examples/`.
Без config контрол виден, но выключен и недоступен с объяснением. UI не
угадывает адрес broker и не создаёт настройки/COM registration самостоятельно.
ON/OFF — управление текущим runtime, не редактор JSON и не hot reload.
Запуск Host с прежними startup configs сохраняет прежнюю startup-политику.

Независимый внешний broker/industrial UA client, vendor DA client/proxy/DCOM,
постоянная регистрация COM, ручная visual/DPI/keyboard/Explorer acceptance
в этой задаче НЕ выполнялись. Нативные state/render-mapping tests не доказывают
полную визуальную parity. Маркировка rust-dev сохранена.

## Выдаваемые файлы

`dist/rust/ui-bugfix-20261109/iCemDataMonitor-rust-dev-0.1.2-windows-x64-Setup.exe`
(13 854 208 bytes).

- Setup SHA256: `C0AB9772B7D255640B32EE39476DC1B4F5AF14E5B8F0D3CD0F7EEFE8F08E6004`.
- MSI SHA256: `A6899926EEFE437858D39BED309DC660473432B6AA93CF77754D4F9E8F61C557`.
- ZIP SHA256: `393FFA72A16F8F8DE136A22E5676FBF9319643E9BAF996F29C3597D0EAB921E3`.
