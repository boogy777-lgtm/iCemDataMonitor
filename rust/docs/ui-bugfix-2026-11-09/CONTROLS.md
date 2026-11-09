# Управление протоколами и логами

## Доказательства

- `icem-web/src/shell.rs`: `Layer` содержит только Modbus, Web, LAN Sniffer и OPC DA; преобразование в frozen protobuf исчерпывающее. OPC UA и MQTT в нём отсутствуют.
- Исходный `icem-desktop/src/monitor.rs`: OPC DA выводился текстом, а переключатели OPC UA и MQTT отсутствовали. `toggle` рисует существующую GPUI pill-геометрию и использует клавиатурный и мышиный обработчики `action_button`.
- `iCemDataMonitor.Shell.WinUI/MainWindow.xaml:155–170`: исходная панель содержит Modbus, sniffer и текст OPC, но не содержит новых переключателей OPC UA/MQTT. `ThemeResources.xaml` задаёт палитру. Frozen `desktop-ui-inventory.json` содержит `OpcText`, но не новые элементы; инвентарь неизменён.
- Исходный `monitor.rs:565–570`: каждую секунду отправлялись GET логов; `send` устанавливал `pending`, а `logs.rs` отключал контролы при `pending`. Это наблюдаемый по коду механизм мерцания, а не измеренный GUI trace.
- `API_SPEC.ru.md:103–108, 320–334`: SetLayer сохраняет toggle-семантику; внутренний control логов уже использует отдельный HTTP/2 pipe endpoint и типизированные ответы, mutation не ретраится.

## Реализация и границы

Типизированный private pipe контракт `protocol_control` подключён к Shell worker без изменений frozen protobuf и legacy SetLayer. `protocol_view.rs::CONTROLS`: `OpcUaSwitch`, `MqttSwitch`, `OpcDaSwitch`. Все три используют имеющуюся GPUI pill-геометрию; отключённая конфигурация показывает конкретный CLI flag. Положение switch и инвертируемое значение Set теперь оба используют подтверждённый `enabled`; фактический `running` и ошибка отображаются отдельно. Включённый, но ещё не работающий MQTT визуально включён и может быть выключен без ложного утверждения «работает».

В `ProtocolView` фоновый GET и пользовательская мутация учитываются раздельно; FIFO worker сохраняет порядок их ответов. Фоновый GET не снижает opacity controls, mutation блокирует повтор и новый GET. Потеря ответа оставляет результат неизвестным без повторной мутации; следующий успешный GET восстанавливает актуальность и снимает временную диагностику. По `REVIEW.md`: disconnect и переход к reconnect инвалидируют freshness и помечают все ранее принятые GET/mutation; поздние ответы не подтверждают новую сессию, а следующий GET необходим до управления. В `monitor.rs` Host/Shell логи используют `LogView::can_submit/submitted` после успешного enqueue. Страница остаётся `StatusPage` с `overflow_y_scroll`, как исходный XAML ScrollViewer; фактическая DPI/GUI приёмка не выполнена.

## Проверки

Прочитаны Rust AGENTS, README, API_SPEC, SOURCE_AUDIT, DESKTOP_STATUS, инвентарь и исходные XAML/тема. Первоначальный Windows Release focused test `cargo test --release --offline --locked -p icem-desktop --lib protocol_view::tests` прошёл 3/3 до review-исправлений; тест после review выполняется отдельно. Runtime, визуальная и аппаратная приёмка не проводились; реальные Host/оборудование/брокеры не запускались.
