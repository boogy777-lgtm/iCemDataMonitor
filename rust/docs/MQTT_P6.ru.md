# P6: MQTT state publisher

Реализован отдельный `apps/host/src/mqtt_consumer/`, typed Config и регистрация `mqtt` в Host lifecycle registry. Единственный acquisition остаётся 200 мс; MQTT получает только read-only core SnapshotReader. Reader/core, Modbus/Web, MMF writer/layout и frozen station INI не менялись. MQTT отключён без `--mqtt-config FILE`.

## Запуск

Из `rust/`, exact Rust 1.99.1:

```text
cargo build -p icem-host-app --locked --offline
./target/debug/icem-host-dev --modbus-fixture tests/fixtures/bus-v5-minimal.bin --bind 127.0.0.1:1502 --duration-ms 10000 --mqtt-config docs/examples/mqtt.json
mosquitto_sub -h 127.0.0.1 -p 1883 -t icem/station-1/state -q 1
```

Для Windows live source вместо fixture используйте существующий `--autostart --ini PATH` либо `--bus-live PATH` и MQTT config. Это инструкция по запуску, не свидетельство Windows runtime acceptance. Config загружается один раз при startup; hot reload нет. Restart с исходными argv сохраняет путь и перечитывает config. Уникальный `client_id` нужен каждому Host, иначе broker отключает предыдущее соединение с тем же ID.

[Пример config](examples/mqtt.json) — отдельный JSON, максимум 16 KiB. Все семь полей обязательны; unknown/duplicate/null/wrong-type fields отклоняются до начала acquisition/listeners с обычным config exit 2. `broker` — numeric unicast IPv4/IPv6 socket с ненулевым port, без DNS/URL. `client_id`: 1..128 UTF-8 bytes без controls. `topic`: 1..1024 UTF-8 bytes без controls, `+`, `#`. `qos`: 0, 1 или 2. `retain`: bool. `publish_interval_ms` и `reconnect_delay_ms`: 50..60000. Publication frequency не меняет частоту acquisition.

Первый scope — MQTT 3.1.1 plain TCP с anonymous broker. TLS, credentials, WebSocket/proxy, subscriptions и command path не реализованы. Размещайте broker в подходящей доверенной сети; удалённый DNS или transport configuration не принимается молча.

## Schema `icem.mqtt.state.v1`

Один UTF-8 JSON объект в одном PUBLISH, в стабильном порядке source variables. Это согласованная копия памяти, не обещание одной синхронной транзакции измерения всех зон. Каталог не нужен; имена не сжимаются по активности.

| Поле | Тип и смысл |
|---|---|
| `schema` | String, точно `icem.mqtt.state.v1` |
| `coherence` | String, `one_synchronized_memory_copy_not_one_measurement` |
| `capture.generation` | u64, локальный successful capture sequence, никогда producer ID; повторяется при source failure |
| `capture.generation_scope` | `local_successful_capture_not_producer_sequence` |
| `capture.status` | `initial`, `captured`, `retained`, `failed` — last completed capture attempt |
| `capture.source_error` | null либо имя SourceError; retained может иметь error |
| `capture.capture_age_ms`, `completion_age_ms` | null либо неотрицательные monotonic milliseconds возраста успешной копии / последнего completed attempt **на момент serialization** |
| `capture.age_clock` | `local_monotonic_age_at_serialization_not_delivery`; clocks не экспортируются как Unix/UTC |
| `capture.quality` | `absent` без первого capture; `retained` после retained/failed attempt; `expired` при captured state и возрасте capture/completion ≥5 s либо неизвестном возрасте; иначе `captured` |
| `capture.quality_scope` | `acquisition_only_not_stream_health`; captured не гарантирует новое измерение/здоровье COM/LAN/RAW |
| `com_source_time` | null без capture, иначе объект ниже |
| `zones` | null без capture, иначе массив COM/LAN/RAW metadata ниже |
| `variables` | null без capture, иначе lazy stable-order массив всех typed variables, включая reserve |

`com_source_time`: `variable="var4"`, `unix_seconds` — исходный i32 из registers 4–5, `provenance="COM_Time_and_Date_registers_4_5"`, `scope="COM_source_field_not_LAN_RAW_measurement_time"`, `validity="raw_source_field_not_independently_verified"`. Ноль сохраняется как Unix epoch; значение не считается missing автоматически. Наличие физической копии не доказывает пригодность vendor time. Поле не назначается LAN/RAW.

Каждый `zones[]` содержит `zone` (`COM`/`LAN`/`RAW`), исходные `last_write_utc_ticks` (i64), `producer_active` (bool), `write_count` (wrapping u32). `clock="dotnet_utc_ticks_100ns_since_0001"`, `provenance="AddIn_zone_writer_last_write_not_measurement_time"`; `write_policy="changed_pair_bits"` для COM/LAN, `"nonempty_raw_line"` для RAW. `upstream_measurement_time=null`: точный upstream measurement timestamp не представлен этим writer metadata. Active — raw producer flag, не вычисленная health. UTC ticks могут выходить за точный integer диапазон JS Number; сохраняйте integer точность в клиенте.

Каждый `variables[]` содержит ровно `id`, `register_address`, `raw_bits`, `type`, `value`, `non_finite`. `id=var{register_address}`, без leading zeros. Address — первый 16-bit register, а не array index: status words 0..3, live pairs 4,6,..598, reserve words 600..register_count−1. var4 — COM stream field, var6 — unit number; COM measurement section 8..199, LAN 200..399, RAW 400..599. Reserve не получает придуманного numeric type.

| `type` | `value` | `raw_bits` / `non_finite` |
|---|---|---|
| `u16` | целое 0..65535 | u32 с исходным word; non_finite=null |
| `i32_com_unix_seconds` | исходный signed i32, без float conversion | bit-preserving u32; non_finite=null |
| `ieee754_binary32` | JSON number для finite, null для non-finite | исходный u32 включая −0 и NaN payload; non_finite=null/`nan`/`positive_infinity`/`negative_infinity` |

`raw_bits` — точное представление и источник восстановления binary32; decimal value предназначено для просмотра. No JSON NaN/Infinity literals. Source capture, reconnect, failed attempt и republish не освежают COM time/zone writer clocks.

## Доставка, ресурсы и lifecycle

Publisher повторяет latest full state с выбранным interval, даже при прежней generation. Поэтому новый failed/retained status и expired age не скрываются dedup по generation. Только один pending PublishedSnapshot/Arc заменяется последним, один сериализованный запрос находится в channel **или** protocol in-flight, rumqttc channel cap=1 и inflight=1. Body остаётся каноническим shared `Box<[u16]>`; нет `Vec<Value>`, deep-copy тела или String на var-name: serde `collect_str` форматирует ID напрямую. Encoded payload ограничен 16 MiB, inbound packet — 1024 bytes. Maximum Bus layout 65535 registers проверен. Это bounds owned application publish state, не обещание фиксированного RSS OS/TCP/runtime.

QoS 0/1/2 и RETAIN используются на настоящем MQTT wire. QoS 1 ждёт PUBACK, QoS 2 — PUBREC/PUBREL/PUBCOMP; пока ACK не завершён, pending coalesces. При network error старый clean-session client/eventloop со старым in-flight уничтожается, retry через config delay начинает новую clean session и отправляет latest state. Нет offline history и end-to-end exactly-once журнала при QoS 2; промежуточные состояния могут пропускаться. Clean session сознательно ограничивает это latest-state требование, не persistent-event delivery.

Registry status Running — получен CONNACK; Faulted — offline/connecting либо фатальный worker error; Stopping — cancellation запрошена, поток ещё жив; Stopped — поток завершился после cancellation. Network failure сам worker не завершает и acquisition/Modbus/Web не останавливает. Общая cancel-all/deadline политика сохраняется: heartbeat 25 ms прерывает ожидание connect/ACK, network timeout 2 s, stop deadline не продлевается; timeout сохраняет owner для повторного stop. Это cooperative scheduling, не hard real-time SLA. Graceful shutdown закрывает TCP и может отбросить in-flight; delivery drain не обещается.

RETAIN сохраняет последний документ в broker после Host exit. Его source timestamps и capture ages остаются фактами **на serialization**, а не живым health ответом: новое получение retained сообщения не обновляет их. Нет Last Will/retained tombstone при shutdown; потребитель обязан учитывать факт retained delivery и собственную liveness policy. В частности, поздний подписчик не выводит текущую acquisition age из одного сохранённого monotonic age без внешнего knowledge времени хранения.

## Проверки и границы

Exact Rust 1.99.1 Linux: Host tests 19/19, fmt/check/build/clippy `-D warnings`, locked/offline. Seven new tests проверяют strict config, types/raw bits/non-finite/absence/failure/retained/expiry, 2000 pending replacements и maximum layout, все QoS wire handshakes, source failure при прежней generation, reconnect с coalescing 500 versions и fixed COM time, slow ACK/timeout/repeated stop, broker offline/неответивший CONNACK, actual acquisition+Modbus независимость. Loopback protocol fixture — не самостоятельный broker.

Отдельно проверен **реальный Mosquitto 2.0.18** на Linux loopback: Host process + QoS2 subscriber, broker restart/reconnect, возрастающая capture generation, прежние source bits/COM/zone clocks, Host exit0, retained message после Host exit с retain flag1. Результат: [nplus1-p6-mosquitto-linux.json](perf/nplus1-p6-mosquitto-linux.json). Это функциональный smoke, не performance benchmark и не Windows/MMF acceptance.

Повторяемый external broker gate из repository root (Mosquitto binaries устанавливаются отдельно, Python standard library):

```text
python rust/tools/acceptance/nplus1_mqtt_broker.py --host rust/target/debug/icem-host-dev --mosquitto PATH_TO_MOSQUITTO --subscriber PATH_TO_MOSQUITTO_SUB --fixture rust/tests/fixtures/bus-v5-minimal.bin --output result.json
```

Gate создаёт собственный temporary anonymous/nonpersistent broker на loopback ephemeral port, завершает только свои processes. Windows exact-head compilation/runtime, real MMF/source outage + broker, production broker security/configuration и hardware остаются OPEN. Этот инкремент не объявляет полный production replacement.


R3 Windows independent gate подготовлен: [pins, licenses, isolated build и scope](../tools/acceptance/COUNTERPARTS.md).
CI после полного Scaffold запускает real Mosquitto 2.0.22 broker/CLI и asyncua2.1.0
с hash-locked Windows wheels; JSON и bounded logs сохраняются always.
Actual Windows результат этого gate ещё не получен; R3 остаётся OPEN.
