# OPC DA: диагностика, 2026-11-09

## Причина

Кнопку нельзя исправить одним включением UI: v1 `SetLayer` для OPC DA — намеренный
внешний no-op (`apps/host/src/layers.rs:55`, `docs/SOURCE_AUDIT.ru.md` D28).
RPC (`crates/icem-web/src/rpc.rs`) игнорирует `desired_state` и передаёт запрос
owner; успешный ответ означает только обработку no-op, не запуск COM сервера.
`LayerKind::runtime_toggleable` возвращает false для OPC DA (`icem-core/src/lib.rs`).

`HostLayers.opc_da` берётся из `settings.opcda_on` (`apps/host/src/main.rs:241,453`),
а не из `consumers::Registry`: флаг может быть true без sidecar и false при
работающем sidecar. SPA показывает его как внешний слой (`web/dashboard/index.html`,
`main.js`), поэтому это не подтверждение реальной доступности COM. В frozen proto
отдельного поля доступности или состояния sidecar нет.

Sidecar запускается исключительно при явном `--opc-da-config`, независимо от INI;
его `Sidecar` регистрируется в Host registry и останавливается вместе с Host
(`apps/host/src/main.rs:358-363,633`). READY приходит после COM registration;
private IPC передаёт snapshots с ACK, watchdog убивает только собственного child
(`apps/host/src/opc_da_consumer/mod.rs`). Состояние registry выводится в диагностику
при старте, но не публикуется в v1 status и не управляется запросом UI. Ошибка
handshake сводится к `OPC DA startup handshake failed`; это не свидетельство
отсутствия реализации COM. Нет config — нет экземпляра; есть config — запуск может
завершиться ошибкой spawn/handshake или позже перейти в Faulted.

## Рекомендация и ограничения

### Реализация после согласования контракта

`apps/host/src/optional_consumers.rs` хранит configs MQTT/UA/DA и управляет
уже существующим `consumers::Registry`; `main.rs` подключает private protocol
queue к тому же owner. Запрос отменённый до исполнения пропускается, повторов
нет. Остановка с ошибкой не удаляет owner; текущие enabled/running не являются
синонимами. MQTT без CONNACK остаётся enabled, но running=false и ответ запуска
не утверждает успех. Отсутствующий config сопровождается именем CLI-флага.
Legacy v1 SetLayer/INI и COM registry CLI не менялись.

Добавлены тест реального UA listener с проверкой освобождения сокета и отказа DA
без config и отдельный Windows opt-in test реального release sidecar: запуск через
READY и остановка собственного child, без `--register`. Первоначальная версия
Windows Release UA owner test прошла (1/1); расширенные повторные запуски и
pipe/sidecar tests выполняются в фоне, их результатов пока нет. Vendor COM client и
производственное окружение не проверены.

Первый запуск нового process-теста был начат до изоляции `PROGRAMDATA` и
`LOCALAPPDATA`: Host мог создать или дополнить обычные каталоги логов. Их не
удаляли и не откатывали. Исправленный тест передаёт дочернему процессу собственные
каталоги и рабочую директорию, завершает его до очистки только своего test root.

Не объявлять capability управления на основании INI или успешного SetLayer. Для
реального start/stop используется отдельный внутренний typed API: наличие валидной явной
конфигурации, состояние и ошибки именно принадлежащего Host child, запуск/остановка
с сохранением ownership при timeout и без автоматического retry. Это отдельный
контракт, а не изменение legacy toggle/no-op; согласовать его с владельцем общего
web/control слоя. Без него UI должен честно показывать, что OPC DA внешний и не
управляется этим RPC; legacy flag не переименовывать в runtime truth. Сами adapters
поддерживают ограниченный read-only OPC DA профиль; отсутствие runtime/config,
ошибка COM регистрации и отсутствие поддержки конкретной операции различны.

Существующие тесты: `apps/host/tests/grpc_control.rs` подтверждает no-op v1;
`apps/host/src/layers.rs` проверяет отсутствие изменения INI; process/IPC тесты
`opc_da_consumer` сейчас только Unix и не доказывают Windows COM. Установленный
vendor COM сервер и registry не проверялись и не
изменялись. Для нового управления нужны изолированные Windows tests с реально
принадлежащим тесту sidecar, проверкой READY/ACK, fault и stop, плюс отдельная
приёмка с настоящим COM client/proxy и vendor окружением. Исходники, proto, SPA и
manifest не менялись.
