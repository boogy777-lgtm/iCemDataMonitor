# Управление MQTT, OPC UA и OPC DA: частное API

## Доказательства и границы

`MQTT_P6.ru.md` и `OPCUA_P7A.ru.md`: оба consumer запускаются только при
переданном валидном `--mqtt-config` / `--opcua-config`; конфигурация читается
при запуске. `apps/host/src/main.rs` регистрирует фактически созданные владельцы
в lifecycle registry. MQTT Running означает CONNACK, OPC UA Running — работающий
связанный и инициализированный сервер. Отсутствие конфигурации не разрешает
создание произвольного broker/endpoint. Legacy SetLayer имеет toggle-семантику;
для OPC DA это no-op, а не управление sidecar.

## Реализация

`icem-web/src/protocol_control.rs`: типизированные `Protocol`, `Command`,
`Status`, `Reply`, очередь на 16 запросов и HTTP/2 adapter на существующем
локальном pipe `/internal/desktop/v1/protocols`. Публичный TCP router его не
монтирует. Состояние различает configured, enabled, running и error; успешный
ON подтверждается только фактическим running. Истечение срока или потеря ответа
означает неизвестный результат без автоматического повтора. JSON строго
проверяет поля и ограничен по размеру. Три consumer выбираются exhaustive match.
Диагностика каждого consumer ограничена 512 байтами (`MAX_ERROR_BYTES`),
превышение отклоняется, а не превращается в подтверждение операции.
Подключение Host owner и UI выполняется параллельно другими участниками; сам
этот модуль не создаёт consumer и не подтверждает их доступность.

## Проверки

Добавлены focused unit tests: отсутствие ложного подтверждения, полная проверка
трёх вариантов, изоляция от публичного router, отклонение неверных запросов до
owner. Первый запуск `cargo test --release --locked --offline -p icem-web
protocol_control` не состоялся: cargo отсутствовал в PATH; повтор с установленным
toolchain на Windows прошёл: 4/4 теста, Release, locked/offline. После него
добавлены проверки отменённого запроса, полной очереди и shutdown, а также
Windows pipe с fake owner, GET, ON/OFF всех трёх протоколов и потерянным ACK.
Расширенный `cargo test --release --locked --offline -p icem-web --lib
protocol_control` на Windows завершился успешно: 8/8, включая ограничение
диагностики и реальный локальный именованный pipe (fake owner, без broker).
Сборка ожидала блокировку общего target; полная workspace-проверка не запускалась.
Форматирование отдельного файла выполнено rustfmt. Реальный broker, оборудование,
production-конфигурация и процессы не использовались. Windows UI/operator и
конечная интеграционная проверка не выполнены; disabled без конфигурации не
является доказательством работающего переключения.
