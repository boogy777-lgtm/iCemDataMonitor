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
- Production iCem/Host/config/логи/ACL/registry не изменяются тестами.

## Отчёты направлений

- [CONTROLS.md](CONTROLS.md): GPUI, отображение и подтверждение состояния.
- [PROTOCOLS.md](PROTOCOLS.md): private MQTT/OPC UA управление Host consumers.
- [OPCDA.md](OPCDA.md): OPC DA UI → runtime/sidecar.
- [LOGS.md](LOGS.md): фоновый refresh и мутации состояния логов.

## Gate

Результаты общего Windows Release-прогона, package smoke и ограничения
операторской проверки будут добавлены после завершения интеграции.
