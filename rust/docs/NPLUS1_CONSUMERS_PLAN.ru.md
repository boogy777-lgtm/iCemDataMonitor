# iCemDataMonitor: N+1-потребители существующих MMF-данных

Проектное предложение, версия 1.4, 23 октября 2026.
Основание исследования: Rust main `4578ab57266b7d25aed82df41c6276597a270c1e`.
Версия 1.4: основной вариант — использовать существующее поле Time & Date в body registers4–5 как общую метку источника для передаваемого кадра. Резерв header49–56 пользователь сохраняет для COM. Новое поле bus_time, сдвиг карты и layout bump не требуются для выбранного варианта. Уточнения по независимости зон приведены в разделе12.
Объём: архитектура и план рефакторинга Rust-конвейера, без реализации.

## Область применения

Этот документ задаёт работу в `rust/`: общий MMF snapshot, N+1-потребители, Modbus/Web и последующие MQTT/OPC DA/OPC UA адаптеры. Исследование legacy .NET/AddIn используется как доказательство внешних контрактов и особенностей источника.

Переработка legacy C#, изменение AddIn, смена формата MMF и перенос регистров в этот план не входят. Используется существующая метка времени MMF4–5; резерв49–56 остаётся для COM. Обнаруженные ошибки legacy регистрируются отдельно и не копируются автоматически в Rust; изменение принятого внешнего поведения требует явного решения.

Это план целевой Rust-архитектуры, а не параллельный рефакторинг двух реализаций. Legacy остаётся эталоном здоровых внешних сценариев; Rust может иметь собственные структуры и control flow. Закрытие legacy выполняется отдельной задачей после приёмки заменённой вертикали.

## 1. Решение

Один acquisition-компонент читает согласованные данные MMF и публикует immutable snapshot. Все потребители читают этот snapshot независимо: Modbus, Web, будущие MQTT publisher, OPC DA server и OPC UA server.

MMF остаётся источником типов и значений. Reader выполняет необходимое чтение бинарного layout и проверку границ по существующему контракту. Новый semantic catalog для восстановления типов не нужен. Опциональный словарь не определяет типы и не меняет значения.

Добавление потребителя затрагивает его модуль, typed configuration и одну регистрацию в composition root. Reader, механизм публикации и существующие адаптеры не получают новых protocol-specific ветвей.

## 2. Основной контракт данных

Основа — массив значений с каноническими именами `var…`.

| Элемент | Контракт |
|---|---|
| Порядок | Стабильный порядок существующего MMF layout |
| Имя | Каноническое var…; переиспользовать имеющееся правило именования |
| Значение | Исходный тип MMF; не приводить всё к f64, String или JSON |
| Метка времени MMF | Передаётся без подмены и изменения смысла |
| Служебные признаки | Активность зон, счётчики записи и доступность сохраняются согласно source contract |
| Доступ | Immutable snapshot; потребитель не меняет источник |

Концептуальное представление, не wire format и не готовая Rust-реализация:

```text
Snapshot
  source_version
  source_timestamp              // существующие registers4–5: Time & Date, int32 Unix seconds
  values[]                      // var… → уже типизированное значение
  source_status                 // факты чтения и активности источника/зон
```

MMF содержит метки отдельных зон, поэтому сохраняются эти метки; не выдумывается единое точное время измерения всех переменных. Существующая timestamp-переменная может оставаться элементом массива; служебная ссылка на неё не требует дублировать или преобразовывать значение.

Имена можно представлять implicit index→var-name либо разделяемой таблицей имён. Не нужно создавать String для каждого значения на каждом tick. Конкретные начальный индекс и формат номера фиксируются из текущего контракта, а не из примера документа.

Индекс переменной не приравнивается автоматически к адресу 16-битного Modbus-регистра: float/int32 могут занимать пару слов. Существующая проверенная адресная геометрия остаётся у Bus/Modbus mapping. Reserved/inactive позиции нельзя сжимать с перенумерацией остальных var….

## 3. Словарь необязателен

Словарь — дополнение к массиву, например:

```text
var… → { display_name, unit, description }
```

Без словаря адаптеры работают с каноническими var… именами. Добавление словаря не меняет номера, типы, значения, адреса и стабильную идентичность переменных. Display name — подпись, не новый primary key.

Если словарь потребуется, он загружается один раз и разделяется immutable-ссылкой. В первой реализации не нужны catalog revisions, hot reload, universal TagValue и отдельная система TagId поверх уже определённых var….

## 4. MMF — единственный источник времени данных

Проверено по producer AddIn и Rust decoder на commit `4578ab57266b7d25aed82df41c6276597a270c1e`.

| Поле MMF | Место | Формат и фактический смысл |
|---|---|---|
| COM Time & Date | Body registers 4–5 | Unix seconds, raw int32 в CDAB. Значение приходит из vendor Time & Date, переводится из .NET ticks; дробные секунды отбрасываются |
| COM last-write | Header bytes 10–17 | .NET UTC ticks: метка zone liveness, записанная AddIn |
| LAN last-write | Header bytes 19–26 | Та же семантика для зоны LAN |
| RAW last-write | Header bytes 28–35 | UTC ticks получения/записи непустой raw line |
| Zone write counters | Header bytes 37–40 / 41–44 / 45–48 | Счётчики событий обновления метаданных зон, wrapping u32; это не общий sequence полного измерительного кадра |

Producer evidence:

- `Burservis.iCem.DataMonitor/BusLayout.cs:135–144,178–186` — offsets и типы.
- `Burservis.iCem.DataMonitor/DataMonitorModule.cs:475–488` — Time & Date записывается через WriteInt32Pair, остальные COM значения как float32.
- `Burservis.iCem.DataMonitor/UnixTimestamp.cs:32–34` — перевод vendor ticks в Unix seconds.
- `Burservis.iCem.DataMonitor/BusWriter.cs:683–688` — COM/LAN header обновляется только если TrackPairWords обнаружил изменение raw bits пары.
- `BusWriter.cs:866–878` — DateTime.UtcNow.Ticks записывается в zone header, active=1, counter+1.
- `BusWriter.cs:789–800` — RAW обновляет метаданные при строке, даже если все значения численно прежние; нераспарсенные поля могут удерживать прошлое значение.

Следствие: COM/LAN counters не являются счётчиком каждого пришедшего одинакового измерения. Они отражают изменения записываемых пар. У RAW иная политика. Это уточняет предположение версии 1.1; существующий producer-контракт не менять в рамках N+1-потребителей.

Правила архитектуры:

1. Все adapters берут время данных из соответствующего поля MMF, не создают `now()` взамен.
2. По проектному решению Time & Date из registers4–5 используется как общая метка источника передаваемого кадра. При этом исходники не гарантируют, что это время измерения каждого LAN/RAW значения. Смысл поля документируется как frame source timestamp, не синхронный sampling time всех зон.
3. При экспорте zone time переменная сохраняет явную связь со своей зоной. Это время обновления зоны, а не доказанное время изменения каждой переменной в ней.
4. Адаптеры выполняют только необходимое преобразование формата и единиц времени. Они сохраняют один и тот же исходный момент и не выдумывают точность выше источника.
5. Отсутствующая/непригодная source timestamp не заменяется локальным временем. Ноль является Unix epoch и сам по себе не доказывает отсутствие данных; initial availability определяется source state, не только числом.  Неизвестное/непригодное время передаётся с явным статусом по контракту адаптера.
6. Повторный read, reconnect, MQTT republish и OPC callback не освежают data timestamp. Последнее сохранённое значение сохраняет MMF time.
7. `received_at`, `sent_at` и server receipt time при необходимости существуют только как диагностика или отдельные обязательные protocol service fields. Они не подменяют время источника и не дают Good сами по себе.
8. Локальные часы нужны для timeout, latency и вычисления возраста source timestamp. Они не являются вторым источником времени данных.

Единственный источник истины для frame source timestamp — существующее поле MMF4–5. Его использование для всего outgoing frame является явным решением проекта, а не доказательством синхронного обновления COM/LAN/RAW.

Новые source updates определяются по фактическим zone metadata, с учётом изменения flags, reset/reconnect и counter wrap. Внутренний publication version нужен для доставки, но не превращает повторное чтение MMF в новое измерение. Сам timestamp не является уникальным ID: несколько записей могут иметь одинаковое время.

## 4.1 Что Rust уже читает и где пока есть расхождение

`rust/crates/icem-bus/src/lib.rs:36–52,86–90` уже читает zone last_write_utc_ticks, active и write_count. `Snapshot::unix_seconds():127–128` читает int32 из регистров 4–5. Новое поле producer или новый таймер для получения этих меток не нужны.

При этом текущая Web history использует локальное время sampling: `rust/apps/host/src/main.rs:545` передаёт utc_now_ms в probe, а `rust/crates/icem-web/src/model.rs:203` сохраняет `(at, value)`. Это время наблюдения history sampler, не source timestamp из MMF. Его нельзя переименовать в source time без изменения реализации и согласования внешнего контракта.

В плане: для новых adapters frame source timestamp исключительно из MMF4–5; zone timestamps остаются отдельными фактами. Для существующей Web history отдельно изменить/версионировать отображение времени данных; если sampling time нужен старому API, оставить его как явно отдельный sampling field, а не подменять значение MMF. Текущий `model.rs:194` также переводит int32 Unix timestamp в f32 для history — новое общее представление сохраняет int32, чтобы не терять секундную точность.

Source MMF гарантирует mutex atomicity отдельной COM/LAN пары вместе с её metadata, а RAW — всей строки. Это согласованное состояние памяти на момент чтения, но не обещание, что весь массив был измерен и записан одной атомарной транзакцией. N+1-delivery не должна усиливать обещания producer.

## 5. Общий механизм публикации

Acquisition владеет MMF reader, читает его по установленному расписанию и публикует `Arc<Snapshot>`. Под native mutex создаётся owned copy, decode выполняется после освобождения mutex. Последующие потребители разделяют данные через Arc вместо deep copy всего массива на каждого.

Latest-state cell хранит только последнее согласованное состояние. У неё один writer и read-only handles. Читатель забирает Arc под коротким lock, затем освобождает lock до преобразования, сети или COM. Уведомление об изменении может объединять промежуточные версии; чтение по timer тоже допустимо. Для первой версии достаточно существующих std sync primitives; новая lock-free библиотека без измерений не нужна.

При read failure сохраняется последнее значение и обновляется source status. Повторная выдача кэша не создаёт новое обновление producer. Отсутствие первого успешного snapshot представляется явно, а не массивом фиктивных нулей.

Когда acquisition не публикует вообще, адаптер обнаруживает silence по установленному deadline. Source health вычисляется из фактов источника и времени, не из количества запросов Web или количества consumer ticks. Политику compatibility D09 локализовать в существующих внешних фасадах; она не определяет новую универсальную семантику Fresh.

Доставка — latest state. Она не гарантирует получение каждого промежуточного состояния. Если требуется надёжный журнал событий/измерений, это отдельный класс механизма, включая producer-side фиксацию изменений. MQTT QoS не восстанавливает то, чего MMF reader никогда не прочитал.

## 6. Контракт потребителя

Общее у потребителей: read-only snapshot handle, start/stop/status, cancellation и ограниченные ресурсы. Способ обработки и протокольный runtime остаются внутри адаптера.

Минимальные роли:

- factory принимает собственную typed configuration;
- start создаёт worker/server и возвращает lifecycle handle;
- worker берёт latest, определяет source update и использует сохранённые MMF timestamps;
- status показывает фактический lifecycle/connection state;
- stop отменяет работу, прекращает новые запросы и завершает worker с явной политикой deadline.

Не нужен общий синхронный `consume()` callback, вызываемый acquisition по очереди для всех протоколов. Не требуется одинаковый async runtime для всех SDK. Blocking API и COM остаются на собственных workers или в sidecar.

У потребителя нет MMF handle, ссылки на mutable reader или другой адаптер. При сбое MQTT остальные продолжают обслуживать данные; процессная/системная изоляция не обещается обычной границей потока. Для внешнего native SDK при необходимости используется отдельный процесс.

Общий lifecycle registry работает с ConsumerId и handles. Старый LayerKind остаётся совместимым legacy facade: существующие toggle и OpcDa no-op не меняют смысл автоматически при появлении нового server adapter.

## 7. Различия адаптеров

| Потребитель | Вход | Собственные правила |
|---|---|---|
| Modbus | Тот же snapshot / существующее register view | Writable mirror, FC03/04/06/16, hold/overwrite и адресная карта |
| Web | Массив var…, source status, опциональный словарь | Ответы API, подписи и bounded history sampling |
| MQTT publisher | Массив var… и времена | Topics/payload, QoS, reconnect, bounded pending/in-flight |
| OPC UA server | var… с исходными типами | Stable NodeId, DataValue/status/timestamps, subscriptions |
| OPC DA server | var… с исходными типами | Stable ItemID, value/quality/timestamp, COM groups/callbacks |

Modbus зеркало остаётся отдельным mutable состоянием. Его клиентские записи не меняют MMF snapshot и не подменяют данные для других потребителей. Если нужен экспорт overrides, это отдельное явно выбранное представление.

Web requests больше не вызывают source.snapshot. История имеет собственную частоту и лимит; её timestamp sampling не подменяет source_timestamp. Существующий внешний формат Web сохраняется совместимой сериализацией.

MQTT на первом этапе публикует состояние без управляющих subscriptions. Полный state payload предпочтителен для согласованности массива; раздельные var topics требуют frame identifiers, если клиент собирает общий кадр. Retained payload и offline reconnect сохраняют реальные времена; свежая доставка не означает свежее измерение. Формат non-finite float задаётся явно, без потери типа под видом обычного JSON number.

OPC UA/DA используют канонические var… как стабильную основу идентичности; словарь меняет display descriptions. Клиентская update/sampling period не инициирует MMF I/O. Source time берётся из MMF; необходимые protocol server-time поля отделены и не подменяют source time.

Для OPC DA рекомендован sidecar из-за COM lifecycle и изоляции native SDK. Host передаёт тот же массив, типы, source timestamps и статус через версионированный bounded IPC. Sidecar сохраняет MMF timestamps; время его приёма при необходимости учитывается только в диагностике. Bitness, версии DA, registration и DCOM acceptance — задачи самого адаптера.

## 8. Размещение кода

Сначала использовать существующие модули. Новые crates не создавать только ради количества слоёв.

| Место | Ответственность |
|---|---|
| `icem-bus` | Существующий typed/layout decoder и register geometry |
| `icem-platform-windows` | MMF и native boundary |
| `icem-core` | Небольшие source/snapshot/lifecycle contracts |
| Host runtime module | Acquisition, latest-state, lifecycle registry |
| `icem-protocols` / `icem-transport` | Существующие Modbus codec/TCP |
| `icem-web` | Web adapter и его history |
| Будущие MQTT/OPC modules | Protocol mapping, SDK, configuration и ресурсы |
| `apps/host` | Подключение выбранных adapters и startup policy |

Отдельный `icem-telemetry` с повторной типизацией, обязательным каталогом, TagId и revision cache исключён из обязательного плана. Shared helpers выделять только при появлении действующего одинакового поведения нескольких потребителей.

## 9. План реализации

1. Зафиксировать существующий типизированный массив MMF: layout, имена var…, timestamp и признаки новых записей. Не менять source format. Подтвердить, что variable index и register address не смешиваются.
2. Ввести один published snapshot и read-only handles. Проверить initial absence, coherent copy, retained data и bounded memory.
3. Выделить единственного acquisition owner; убрать source.snapshot из Web и consumer-specific ветвей main.
4. Перевести Modbus/Web на общий snapshot. Сохранить mirror byte semantics; отдельно принять изменения cache age и timing относительно старого request-driven read.
5. Ввести ConsumerId/lifecycle registry и общий небольшой adapter harness. Source timestamps адаптеров берутся из MMF; локальное время остаётся только для служебных задач.
6. Добавить MQTT как первую проверку N+1: новый module+config+registration без правок reader, Modbus, Web.
7. Добавить OPC UA, затем OPC DA sidecar; проверить типы, identity, quality/timestamps и реальные Windows clients.
8. Подключать словарь отдельно, когда появится практическая потребность. Проверить, что отсутствие словаря не нарушает базовую работу.

На P4/P5 не сохранять два параллельных reader owner ради перехода. После переключения consumer удалить старый физический read path. Не строить сейчас event bus, plugin loader или dynamic Rust ABI.

## 10. Class / Inv / N+1

Class: независимые потребители текущего состояния одного типизированного MMF массива. Event log и управляющие записи в producer вне класса.

Inv: один acquisition owner; исходные типы, var identity и MMF time сохраняются; optional dictionary не влияет на работоспособность; медленный consumer не удерживает reader; время данных не меняется от повторного чтения, reconnect или republish.

N+1: adapter module + typed configuration + composition registration. Нет новой protocol branch в source или существующих consumers. Общий harness проверяет start/fail/stop, source outage и slow client.

Acceptance cases: COM/LAN identical-pair rewrite не обновляет zone time/counter; RAW identical line обновляет метаданные; повтор той же версии; reset counters/reconnect; нет первого snapshot; MMF read error; source stopped; различная частота adapters; broker offline; словарь отсутствует/меняет только подпись; FC06/16 не меняют source values; bounded queues при длительном disconnect.

## 11. Принятые упрощения

- Типы уже задаёт MMF: отдельного каталога для их восстановления нет.
- Канонический массив var… работает без semantic naming.
- MMF — единственный источник времени данных; adapter receipt time не входит в основной data contract.
- Необязательный словарь — обогащение, не обязательный runtime dependency.
- Общий механизм — получение и публикация snapshot; протокольные детали остаются в adapters.
- Архитектура создаёт ровно те границы, которые нужны Modbus/Web и запланированным MQTT/OPC.

Этот документ описывает план; реализация исходников и CI в рамках его подготовки не выполнялась.


## 12. Выбранное размещение: существующие регистры4–5

Уточнение пользователя: header bytes49–56 сохраняются в резерве для COM. Предложение версии1.3 использовать их под новое bus_time отозвано. Ничего туда не записывать и не менять существующую карту в рамках N+1-потребителей.

Существующая карта сохраняется: status0–3, Time & Date4–5, Unit Num6–7, COM до199, LAN200–399, RAW400–599. Timestamp уже представляет int32 Unix seconds, CDAB raw bits; Rust Snapshot::unix_seconds читает именно его.

Путь подтверждён кодом:

1. DataMonitorAddressMap.cs:31 связывает vendor Time & Date с address4.
2. DataMonitorModule.cs:471–483 берёт vendor numeric ticks из результата COM poll, вызывает UnixTimestamp.TicksToUnixSeconds и WriteInt32Pair.
3. UnixTimestamp.cs:32–34 переводит .NET ticks в int32 Unix seconds.
4. icem-bus/src/lib.rs:127–128 восстанавливает signed int32 из той же пары регистров.

Контракт outgoing frame: `source_timestamp = snapshot.unix_seconds()`; массив var… передаётся без повторной типизации; optional dictionary лишь добавляет подписи. Все adapters получают то же source timestamp. Повторная публикация не меняет его.

Эта метка обновляется только при наличии соответствующего успешно прочитанного source field. PollStreams имеет ранние выходы при no-project/no-active-case (DataMonitorModule.cs:419–437). Непрерывное продвижение4–5 без устройств/активного case исходниками не гарантировано. Это не мешает непрерывной работе конвейера: он передаёт последнее состояние и последнее время источника.

Независимый LAN update при прежнем4–5 должен пройти через pipeline. Поэтому:

- общий timestamp не является ID кадра и единственным change detector;
- source metadata/values и flags используются для определения обновлений;
- остановка Time & Date сама по себе не блокирует LAN/RAW и не доказывает их невалидность;
- heartbeat и zone active/time сохраняются для фактической живости;
- timestamp outgoing frame не объявляется точным временем изменения каждого var в каждой зоне.

Если впоследствии понадобится непрерывно текущее время publisher, даже когда source Time & Date не обновляется, это другое требование. Тогда необходимо отдельно решить, меняется ли writer/семантика4–5. Нельзя молча подмешать DateTime.UtcNow в существующий vendor Time & Date или дать адаптерам генерировать разные значения.

Acceptance: source time замер, но LAN обновился — данные переданы с тем же source timestamp; два adapters получили один frame — source timestamp совпал; reconnect/republication не меняет время; отсутствие поля не заменяется now; исторический timestamp не приводится к f32; reserved49–56 и все текущие addresses неизменны.

Реализация не выполнялась; публикуется только план.
