# Конфигурация: config.ini и emule_config.xml

## config.ini — где живёт и кто создаёт

- Канонический путь: `C:\ProgramData\Halliburton\iCem\DataMonitoring\config.ini`.
- **В MSI не входит**: host создаёт файл при первом запуске (UTF-8 с BOM) с точными дефолтами; дефолты в коде (`IniOptions`) — единственный источник истины.
- Деинсталляция MSI стирает папку вместе с `config.ini`; апгрейд — сохраняет (см. [install.md](install.md)).

## Закон read-once (горячей перезагрузки нет)

Host читает `config.ini` **один раз при старте**. Правка файла вступает в силу только после рестарта host'а; hot-reload запрещён. Чекбоксы слоёв в статус-окне shell — это runtime-запуск/остановка опубликованных слоёв (публишер зарегистрирован всегда), а не правка ini; состояние чекбоксов сохраняется в ini только при включённом `ui_set_as_default`.

## Миграция legacy station.ini

Старый `station.ini` автоматически мигрируется в `config.ini` при первой загрузке: legacy-файл **копируется** в канонический путь (существующий `config.ini` никогда не перезаписывается) и **не удаляется** — остаётся на месте. Провал копирования (IO/права) не фатален: host продолжает работать по legacy-пути и пишет строку-свидетельство в лог. Порядок поиска: канонический `config.ini` → `station.ini` в канонической папке → `config.ini` рядом с exe → `station.ini` рядом с exe (dev).

## Ключи и дефолты (краткая таблица, с версии 1.4.4)

| Ключ | Дефолт | Смысл | Рестарт |
|---|---|---|---|
| `modbus_on` | `1` | Включает ModbusTcpPublisher на `:modbus_port` (чекбокс в shell = runtime start/stop) | да |
| `web_on` | `1` | Включает Kestrel SPA-дашборд на `:web_port` (SPA + gRPC-Web + `/api/regs/history` + кольцевой сэмплер 1 Гц). OPTIONAL: отсутствует ⇒ 1 (старый файл получает дашборд, не теряет). При `0` ops-поверхность `/api/status*`, `/api/regs` всё равно обслуживается | да |
| `lansniffer_on` | `1` | Регистрирует AceSnifferService → LanBusSink (в v5 sink отложен — записей в шину нет) | да |
| `opcda_on` | `0` | Заглушка: OPC DA — внешняя проекция, in-process слоя нет (чекбокс серый) | да |
| `web_remote_on` | `0` | `0` = привязка `:web_port` только loopback; `1` = ListenAnyIp (закрытый контур, правило firewall — шаг runbook) | да |
| `modbus_port` | `502` | Порт Modbus TCP ([1..65535]) | да |
| `web_port` | `8090` | Порт веб-дашборда ([1..65535]) | да |
| `mmf_name` | `Global\iCemDataMonitorBus_v1` | Имя memory-mapped file шины | да |
| `mmf_path` | `C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat` | Файловая подложка шины (`Data.dat`) | да |
| `total` | `10001` | Полное число holding-регистров шины (runtime-данные ∈ [650..65535]) | да |
| `modbus_tick_ms` | `500` | Период зеркалирования шины в регистровый файл, PLC-диапазон [200..500], полная перезапись каждый тик | да |
| `ui_set_as_default` | `1` | Начальное состояние чекбокса «Сохранять как настройки по умолчанию» (OPTIONAL: отсутствует ⇒ 1) | да |

**Каноническая таблица всех ключей и retired-ключей — [specs/dotnet-server/Э5_config.csv](../specs/dotnet-server/Э5_config.csv).** Там же перечислены retired-ключи (`com_port`, `com_base/com_len`, `lan_base/lan_len`, `raw_base/raw_len`, `emule_on`): они принимаются, но игнорируются (никогда не фатальны) и больше не записываются при сохранении — не возвращать.

Парсер строгий: синтаксис, неизвестные/дублирующиеся/пропущенные ключи, нечисловые значения, диапазоны (порты, `modbus_tick_ms`, `total`, чётность длин зон для float32 CDAB, непересечение зон) — любое нарушение даёт `IniConfigException` со списком всех нарушений.

## emule_config.xml (кратко)

- Путь: `C:\ProgramData\Halliburton\iCem\DataMonitoring\emule_config.xml`.
- Создаётся **приложением iCemEmulator** (не host'ом); host его не пишет и не управляет эмулятором.
- Читается эмулятором **при старте** (один раз); все тумблеры эмуляции (например, `ComEnabled`/`LanEnabled`) по умолчанию **OFF** — свежесозданный файл ничего не стримит; потоки стартуют только при явном включении в файле.
- Стёрт деинсталляцией MSI вместе с папкой (см. [install.md](install.md)).

## Data.dat и mmf_name

Шина данных — memory-mapped file с именем `mmf_name` (`Global\iCemDataMonitorBus_v1`), файловая подложка — `mmf_path` (`Data.dat`). AddIn внутри iCem.exe — единственный писатель шины (CreateFromFile на `Data.dat`); host и CLI — читатели. Геометрия зон — compile-time (`BusLayout`), `total` — runtime-данные. Wire-формат регистров см. [architecture.md](architecture.md) и [../PROTOCOL.md](../PROTOCOL.md).
