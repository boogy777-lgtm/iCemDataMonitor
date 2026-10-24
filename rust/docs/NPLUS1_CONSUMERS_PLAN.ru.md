# iCemDataMonitor: N+1-потребители существующих MMF-данных

Проектное предложение, версия 1.7, 24 октября 2026.
Версия 1.7 фиксирует решение пользователя: единый acquisition с периодом 200 мс (5 Гц) достаточен для всех N+1-потребителей. Частота больше не является открытым вопросом; число этапов остаётся восемь.
Основание исследования: Rust main `4578ab57266b7d25aed82df41c6276597a270c1e`.
Версия 1.6 исправляет семантику времени: Time & Date в registers 4–5 приходит из COM-потока; это не внутренняя метка MMF, не время acquisition и не общий timestamp LAN/RAW. Прежнее решение версии 1.4 назначить его общей меткой outgoing frame отозвано. Разделы 2, 4 и 12 разделяют COM time, метки записи зон и локальное время чтения. Карта MMF, writer и резерв header 49–56 не меняются.
Версия 1.5: в этапы 2–4 включены правила владения буферами, сокращения копирований/аллокаций и измерений; раздел 13 задаёт условия автоматической и ручной SIMD-оптимизации. Число этапов реализации остаётся восемь. Изменение документа не включает реализацию оптимизаций.
Объём: архитектура и план рефакторинга Rust-конвейера, без реализации.

## Область применения

Отдельный план изменения существующего C#/.NET приложения: [LEGACY_REFACTORING_PLAN.ru.md](../../docs/LEGACY_REFACTORING_PLAN.ru.md). Эти планы имеют независимые реализации и общие MMF-контракты.

Этот документ задаёт работу в `rust/`: общий MMF snapshot, N+1-потребители, Modbus/Web и последующие MQTT/OPC DA/OPC UA адаптеры. Исследование legacy .NET/AddIn используется как доказательство внешних контрактов и особенностей источника.

Переработка legacy C#, изменение AddIn, смена формата MMF и перенос регистров в этот план не входят. Сохраняется существующая COM Time & Date в MMF 4–5 без назначения её времени другим зонам; резерв 49–56 остаётся для COM. Обнаруженные ошибки legacy регистрируются отдельно и не копируются автоматически в Rust; изменение принятого внешнего поведения требует явного решения.

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
| COM Time & Date 4–5 | Передаётся как время из COM-потока; не назначается общей меткой кадра или LAN/RAW |
| Метки записи зон | Передаются с именем зоны и смыслом writer last-write; не выдаются за время измерения |
| Служебные признаки | Активность зон, счётчики записи и доступность сохраняются согласно source contract |
| Доступ | Immutable snapshot; потребитель не меняет источник |

Концептуальное представление, не wire format и не готовая Rust-реализация:

```text
Snapshot
  capture_generation            // локальная версия публикации, не ID измерения producer
  com_source_time               // registers 4–5: Time & Date из COM, int32 Unix seconds
  values[]                      // var… → исходное типизированное значение
  zones[]                       // zone → last_write_utc_ticks, active, write_count
  captured_at_monotonic         // локальное время успешной копии: только runtime/диагностика
  source_status                 // факты чтения и активности источника/зон
```

COM Time & Date остаётся исходной переменной массива с собственным типом и происхождением. Служебная проекция не создаёт новое время и не меняет её биты. Zone last-write из header — отдельные факты записи AddIn. Локальное captured_at_monotonic не является Unix/UTC data timestamp и не экспортируется под таким именем. Первое успешное чтение и пригодность COM field определяются явно; ноль может означать Unix epoch, а не отсутствие.

Имена можно представлять implicit index→var-name либо разделяемой таблицей имён. Не нужно создавать String для каждого значения на каждом tick. Конкретные начальный индекс и формат номера фиксируются из текущего контракта, а не из примера документа.

Индекс переменной не приравнивается автоматически к адресу 16-битного Modbus-регистра: float/int32 могут занимать пару слов. Существующая проверенная адресная геометрия остаётся у Bus/Modbus mapping. Reserved/inactive позиции нельзя сжимать с перенумерацией остальных var….

## 3. Словарь необязателен

Словарь — дополнение к массиву, например:

```text
var… → { display_name, unit, description }
```

Без словаря адаптеры работают с каноническими var… именами. Добавление словаря не меняет номера, типы, значения, адреса и стабильную идентичность переменных. Display name — подпись, не новый primary key.

Если словарь потребуется, он загружается один раз и разделяется immutable-ссылкой. В первой реализации не нужны catalog revisions, hot reload, universal TagValue и отдельная система TagId поверх уже определённых var….

## 4. Время COM, метки записи зон и время acquisition

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

1. Registers 4–5 сохраняют Time & Date из COM. Reader, acquisition и adapters не перезаписывают его локальным now и не используют как общий timestamp полного snapshot.
2. COM Time & Date не присваивается LAN/RAW. Даже для COM это исходное поле потока, а не автоматически доказанное точное время измерения каждого сигнала.
3. Zone last-write сохраняет связь с COM/LAN/RAW и происхождение writer. Это время записи/изменения зоны по текущей политике AddIn, не универсальный sampling time.
4. Для LAN/RAW точное upstream measurement time, если оно не представлено контрактом, считается неизвестным. Не подставлять COM time или local now. Если новый протокол требует timestamp, его отдельная политика должна явно задавать смысл и происхождение обязательного поля; writer time допустимо экспортировать только под соответствующим описанием.
5. Повторный read, reconnect, republish и callback не освежают COM time и zone last-write. Изменение captured_at при новой физической копии доказывает только чтение памяти, не получение нового измерения.
6. Runtime использует собственные monotonic captured/completed timestamps для watchdog, deadlines и latency; это уже иной класс времени. Optional observed_at_utc/sent_at/server receipt time — явно отдельная диагностика/служебные protocol fields, не подмена исходных данных.
7. Initial absence и пригодность COM field определяются source state; ноль не является универсальным missing marker. Отсутствие upstream time само по себе не доказывает отказ MMF или остановку другой зоны. Quality и stream freshness требуют собственных проверяемых фактов.
8. LAN/RAW update проходит при неизменном, отсутствующем или историческом COM time. Liveness вычисляется из относящихся к зоне metadata и существующей политики; publication expiry — из локального времени acquisition.
9. Source updates определяются по значениям, flags и zone metadata с учётом reset/reconnect/counter wrap. Capture generation — порядок runtime publications, не producer sequence. Counters COM/LAN отражают изменения пар, RAW — приход строки; из них нельзя вывести поступление каждого одинакового измерения.

Ни в MMF, ни в новом общем snapshot не вводится фиктивное единое время измерения всех зон. Если потребуется отдельная producer-side метка получения каждого пакета/общего кадра, это новое требование к writer и контракту; оно не входит в данный рефакторинг.

## 4.1 Что Rust уже читает и где пока есть расхождение

`rust/crates/icem-bus/src/lib.rs:36–52,86–90` уже читает zone last_write_utc_ticks, active и write_count. `Snapshot::unix_seconds():127–128` читает int32 из регистров 4–5. Новое поле producer или новый таймер для получения этих меток не нужны.

При этом текущая Web history использует локальное время sampling: `rust/apps/host/src/main.rs:545` передаёт utc_now_ms в probe, а `rust/crates/icem-web/src/model.rs:203` сохраняет `(at, value)`. Это время наблюдения history sampler, не source timestamp из MMF. Его нельзя переименовать в source time без изменения реализации и согласования внешнего контракта.

В плане: новые adapters сохраняют COM Time & Date только в его исходной области; zone writer timestamps и локальное observation time остаются отдельными фактами. Существующее sampling time Web history не заменять COM time для всех сигналов. Изменения отображения/формата проходят отдельную compatibility или versioning политику. Исторический `model.rs:194` переводит int32 COM Unix timestamp в f32 для history — новое общее представление сохраняет исходный int32; существующие внешние projection contracts не переписываются молча.

Source MMF гарантирует mutex atomicity отдельной COM/LAN пары вместе с её metadata, а RAW — всей строки. Это согласованное состояние памяти на момент чтения, но не обещание, что весь массив был измерен и записан одной атомарной транзакцией. N+1-delivery не должна усиливать обещания producer.

## 5. Общий механизм публикации

Acquisition владеет MMF reader, читает его с единым установленным периодом 200 мс (5 Гц) и публикует `Arc<Snapshot>`. Этот период принят пользователем как достаточный для всех N+1-потребителей. Добавление адаптера, запрос клиента или изменение частоты выдачи не создаёт отдельного MMF polling и не меняет период acquisition. Потребители могут читать latest snapshot и выдавать данные со своей протокольной частотой; более частая выдача использует уже опубликованное состояние. Период acquisition не является гарантией end-to-end latency 200 мс: ожидание MMF mutex, обработка и доставка измеряются отдельно. Под native mutex создаётся owned copy, decode выполняется после освобождения mutex. Последующие потребители разделяют данные через Arc вместо deep copy всего массива на каждого.

Latest-state cell хранит только последнее согласованное состояние. У неё один writer и read-only handles. Читатель забирает Arc под коротким lock, затем освобождает lock до преобразования, сети или COM. Уведомление об изменении может объединять промежуточные версии; чтение по timer тоже допустимо. Для первой версии достаточно существующих std sync primitives; новая lock-free библиотека без измерений не нужна.

При read failure сохраняется последнее значение и обновляется source status. Повторная выдача кэша не создаёт новое обновление producer. Отсутствие первого успешного snapshot представляется явно, а не массивом фиктивных нулей.

Когда acquisition не публикует вообще, адаптер обнаруживает silence по установленному deadline. Source health вычисляется из фактов источника и времени, не из количества запросов Web или количества consumer ticks. Политику compatibility D09 локализовать в существующих внешних фасадах; она не определяет новую универсальную семантику Fresh.

Доставка — latest state. Она не гарантирует получение каждого промежуточного состояния. Если требуется надёжный журнал событий/измерений, это отдельный класс механизма, включая producer-side фиксацию изменений. MQTT QoS не восстанавливает то, чего MMF reader никогда не прочитал.

## 6. Контракт потребителя

Общее у потребителей: read-only snapshot handle, start/stop/status, cancellation и ограниченные ресурсы. Способ обработки и протокольный runtime остаются внутри адаптера.

Минимальные роли:

- factory принимает собственную typed configuration;
- start создаёт worker/server и возвращает lifecycle handle;
- worker берёт latest, определяет обновления с учётом зоны и сохраняет происхождение COM time/zone writer timestamps;
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

Web requests больше не вызывают source.snapshot. История имеет собственную частоту и лимит; её observation/sampling time остаётся явно отличным от COM Time & Date и zone writer timestamps. Существующий внешний формат Web сохраняется совместимой сериализацией.

MQTT на первом этапе публикует состояние без управляющих subscriptions. Полный state payload предпочтителен для согласованности массива; раздельные var topics требуют frame identifiers, если клиент собирает общий кадр. Retained payload и offline reconnect сохраняют исходные COM/zone времена и их provenance; LAN/RAW payload не получает COM time как timestamp своих измерений. Свежая доставка не означает свежее измерение. Формат non-finite float задаётся явно, без потери типа под видом обычного JSON number.

OPC UA/DA используют канонические var… как стабильную основу идентичности; словарь меняет display descriptions. Клиентская update/sampling period не инициирует MMF I/O. COM source time и zone writer times берутся из соответствующих MMF fields с явным смыслом. Отсутствующее measurement time LAN/RAW не заполняется COM time; обязательные protocol timestamp/status fields требуют отдельной documented mapping policy. Server-time поля отделены от исходных данных.

Для OPC DA рекомендован sidecar из-за COM lifecycle и изоляции native SDK. Host передаёт тот же массив, типы, COM time, zone writer metadata и статус через версионированный bounded IPC. Sidecar сохраняет scope/provenance каждого времени; время его приёма при необходимости учитывается отдельно в диагностике. Bitness, версии DA, registration и DCOM acceptance — задачи самого адаптера.

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

1. Зафиксировать существующий типизированный массив MMF: layout, имена var…, COM Time & Date, zone writer metadata и признаки обновления каждой зоны. Не менять source format. Подтвердить, что variable index и register address не смешиваются.
2. Ввести один published snapshot и read-only handles. Проверить initial absence, coherent copy, retained data и bounded memory. По разделу 13 использовать Vec для действительно формируемых/рабочих буферов, сохранить подходящее непрерывное представление готовых данных и разделять готовый snapshot через Arc без deep copy на каждого потребителя. Измерить allocations/bytes на capture и число одновременно удерживаемых snapshots; заранее выделять ёмкость там, где размер известен.
3. Выделить единственного acquisition owner с периодом 200 мс (5 Гц), общим для всех N+1-потребителей; убрать source.snapshot из Web и consumer-specific ветвей main. Owned copy MMF выполняется под существующей native синхронизацией; decode и пакетная обработка — после её освобождения. Рабочие буферы не могут изменять опубликованные snapshots или переиспользовать их память при наличии читателей.
4. Перевести Modbus/Web на общий snapshot. Сохранить mirror byte semantics; сохранить принятый период acquisition 200 мс и проверить cache age и timing относительно старого request-driven read. Снять Release baseline полного capture/decode/publication и задержек Modbus/Web; сначала уменьшить лишние чтения, копирования и аллокации. Автовекторизацию безопасных циклов проверить по generated code и benchmark. Ручной SIMD рассматривать только для измеренного горячего участка по критериям раздела 13; отсутствие доказанного выигрыша завершает этот этап без ручного SIMD.
5. Ввести ConsumerId/lifecycle registry и общий небольшой adapter harness. COM time и zone writer timestamps сохраняют исходный scope; локальное acquisition time используется для runtime/служебных задач. Harness проверяет независимое обновление LAN/RAW при замершем COM time.
6. Добавить MQTT как первую проверку N+1: новый module+config+registration без правок reader, Modbus, Web.
7. Добавить OPC UA, затем OPC DA sidecar; проверить типы, identity, quality/timestamps и реальные Windows clients.
8. Подключать словарь отдельно, когда появится практическая потребность. Проверить, что отсутствие словаря не нарушает базовую работу.

На P4/P5 не сохранять два параллельных reader owner ради перехода. После переключения consumer удалить старый физический read path. Не строить сейчас event bus, plugin loader или dynamic Rust ABI.

## 10. Class / Inv / N+1

Class: независимые потребители текущего состояния одного типизированного MMF массива. Event log и управляющие записи в producer вне класса.

Inv: один acquisition owner с периодом 200 мс, не зависящим от числа и частоты запросов потребителей; исходные типы, var identity и MMF time сохраняются; optional dictionary не влияет на работоспособность; медленный consumer не удерживает reader; время данных не меняется от повторного чтения, reconnect или republish.

N+1: adapter module + typed configuration + composition registration. Нет новой protocol branch в source или существующих consumers. Общий harness проверяет start/fail/stop, source outage и slow client.

Acceptance cases: единое расписание acquisition 200 мс при разном числе и частоте потребителей; запросы adapters не вызывают дополнительные физические чтения MMF; медленный consumer не задерживает acquisition; COM/LAN identical-pair rewrite не обновляет zone time/counter; RAW identical line обновляет метаданные; повтор той же версии; reset counters/reconnect; нет первого snapshot; MMF read error; source stopped; различная частота adapters; broker offline; словарь отсутствует/меняет только подпись; FC06/16 не меняют source values; bounded queues при длительном disconnect.

## 11. Принятые упрощения

- Типы уже задаёт MMF: отдельного каталога для их восстановления нет.
- Канонический массив var… работает без semantic naming.
- COM time и zone writer metadata читаются из MMF без подмены; локальное acquisition/adapter receipt time имеет отдельный runtime/diagnostic смысл.
- Необязательный словарь — обогащение, не обязательный runtime dependency.
- Общий механизм — получение и публикация snapshot; протокольные детали остаются в adapters.
- Архитектура создаёт ровно те границы, которые нужны Modbus/Web и запланированным MQTT/OPC.

Этот документ описывает план; реализация исходников и CI в рамках его подготовки не выполнялась.


## 12. Области памяти и исправленная семантика registers 4–5

### 12.1. Физическая карта и логические представления

Header MMF занимает отдельные 64 bytes перед body. Его offsets 49–56 сохраняются в резерве для COM; ничего туда не записывать. Body registers 0–7 можно представить одной логической служебной областью 0. Это не новый физический header и не новая зона живости.

| Логическое представление | Body register addresses, включительно | Исходный смысл/owner |
|---|---|---|
| Service: status | 0–3 | COM port, case/LAN status, AddIn heartbeat |
| Service: COM time | 4–5 | Time & Date из COM, int32 Unix seconds, CDAB raw bits |
| Service: unit | 6–7 | Unit Num из COM, остаётся float32 |
| COM measurements | 8–199 | COM values; не менять исходные адреса и пары |
| LAN | 200–399 | LAN values и собственная zone metadata |
| RAW | 400–599 | Values parsed из COM raw line и отдельная RAW metadata |
| Reserve | 600–RegCount-1 | Сохранить адреса/биты; не придумывать назначение |

Существующая producer COM-зона остаётся 4–199, включая поля 4–7. Группировка 0–7 в ServiceView не меняет Zone::Com range, source ownership, правила Modbus mirror или liveness. Region geometry описывается в одном месте рядом с bus layout; ServiceView и общий RegionView дают read-only projections над одним owned snapshot без отдельного Vec/copy на каждый регион. Внутренние SIMD kernels выбираются для проверенных однородных диапазонов; timestamp/Unit Num не теряют свои типы. Пустые/зарезервированные места не сжимаются.

### 12.2. Registers 4–5 — поле потока COM

На проверенном main `a0f1bd7f47fd8fd92a8dfe034dd25f04a8a0d081`:
- `DataMonitorAddressMap.cs` связывает vendor Time & Date с address 4.
- `PollPairPreparation.PrepareCom` преобразует переданное из COM poll значение через `UnixTimestamp.TicksToUnixSeconds`; Unit Num и прочие пары сохраняют отдельную float32 policy.
- `icem-bus::Snapshot::unix_seconds` восстанавливает int32 из этой пары; reader не создаёт локальную метку в 4–5.

Следовательно, `com_source_time = snapshot.unix_seconds()` — проекция поля COM. Прежнее назначение общей frame source timestamp в v1.4 отменено. Все adapters видят один и тот же исходный COM value, но не назначают его временем LAN/RAW. Отсутствие устройств/активного case или historical playback может остановить/изменить это время, не останавливая acquisition.

Если нужен timestamp наблюдения полного memory snapshot, это `captured_at_monotonic` внутри runtime или отдельно названный UTC observation field. Он не означает, что значения всех зон одновременно измерены, и не требует изменения MMF. COM last-write / LAN last-write / RAW last-write из header также остаются отдельными writer events.

### 12.3. Проверенные границы завершённого legacy refactor

На том же main C6 `BusAcquisitionService` использует TimeProvider.GetTimestamp/GetElapsedTime для publication expiry; registers 4–5 в этот gate не входят. `ModbusTcpPublisher.TickOnce` сохраняет stale-hold и независимые zone active gates. C9 pair preparation сохраняет COM field, а не записывает now. Оснований откатывать C1–C10 из-за назначения COM time в этом плане не найдено в этих проверенных местах; это целевая проверка, не утверждение об отсутствии любых исторических time/projection defects.

Существующие regression sources: `BusAcquisitionTests.IdenticalBytesAndSourceTime_NewReferenceIsNewSuccess_ExpiryHasNoSourceIO` и `ModbusContractTests.FC03AndFC04_RawBitsAndIndependentZones_PreserveFixedComTime`. При исправлении документа изучены исходники; новое исполнение tests/build не выполнялось.

Acceptance для реализации Rust:
- COM time стоит/отсутствует, LAN или RAW меняется: соответствующие значения и metadata доставлены; их измерениям COM time не приписан.
- Два adapters читают одну публикацию: COM time и zone metadata совпадают побитно; их разные delivery times не меняют source fields.
- Reconnect/read/republication не освежает исходные COM/zone timestamps; локальное captured_at не используется как producer update ID.
- Старый успешный snapshot удерживается при capture failure, а acquisition expiry проверяется локальными monotonic часами.
- COM/LAN identical-pair rewrite не выдумывает новый zone writer event; RAW identical line сохраняет отдельную политику.
- COM timestamp остаётся int32, Unit Num — float32, адреса/byte order/header reserve 49–56 неизменны.

Реализация нового общего Rust pipeline здесь не выполнялась; исправлен только план.

## 13. Непрерывные буферы, Vec и условная SIMD-оптимизация

Это уточнение этапов 2–4, не девятый обязательный этап и не разрешение переписывать весь конвейер ради SIMD. Проверенное исходное представление на main `a0f1bd7f47fd8fd92a8dfe034dd25f04a8a0d081`: `icem-bus::Snapshot` уже хранит `Box<[u16]>`, выдаёт `&[u16]` и восстанавливает int32 COM time отдельной проекцией. Замена Box на Vec сама по себе не доказывает ускорение. Vec задаёт непрерывный динамический массив; SIMD выполняет одну операцию над несколькими элементами.

Class: обработка непрерывных массивов исходных регистров/байтов в `icem-bus` и буферов acquisition owner; сеть, MMF lock/lifecycle и COM callbacks вне класса численных SIMD kernels.
Inv: сохраняются исходные биты, адреса, типы, timestamps, metadata и ошибки; published snapshot неизменяем; эквивалентность проверяется scalar differential vectors, границами/хвостами и Windows Release measurements.
N+1: следующий однородный kernel меняет только свой codec/runtime module и общий набор correctness/performance vectors; новый потребитель использует тот же snapshot handle без новой CPU branch в reader или других adapters.

### 13.1. Владение и аллокации

| Представление | Когда применять |
|---|---|
| `Vec<T>` | Формирование массива или рабочий буфер с изменяемой длиной; capacity резервируется по известному размеру, рост ограничен проверенным source/protocol limit |
| `Box<[T]>` | Готовый массив без потребности изменять длину; существующий `Box<[u16]>` не заменяется без причины |
| `&[T]` | Заимствованный доступ к данным на время операции без владения и копии |
| `Arc<Snapshot>` | Разделение одного готового snapshot между независимыми потребителями; клонируется handle, а не весь массив |

- Не создавать строки var-имён и отдельные heap objects для каждого значения на каждом tick. Существующее правило identity и опциональный immutable словарь сохраняются.
- Исходные u16/int32/float bits не превращать в общий `Vec<f32>` или `Vec<f64>` ради векторизации. Time & Date 4–5 остаётся int32; типизированные проекции и отдельная геометрия регистров сохраняются.
- Заранее выделять и, где владение позволяет, повторно использовать рабочие буферы. Capacity и фактический объём retained data учитывать отдельно. Не обещать zero allocations без измерений.
- Пока читатели удерживают старый Arc, его данные нельзя перезаписывать следующим кадром. Переиспользование требует доказанного единоличного владения средствами Rust. Два ping-pong буфера сами по себе такого доказательства не дают.
- Latest cell ограничивает собственную очередь одним состоянием, но не все живые ссылки. История, pending/in-flight и удержание старых snapshots у каждого adapter должны иметь явный предел. Не добавлять pool/allocator/lock-free механизм без отдельного измеренного требования.
- Согласованная owned copy под MMF mutex остаётся необходимой. Преобразования выполняются после освобождения mutex; Arc не является разрешением выдать consumer ссылку на изменяемую MMF.

### 13.2. Последовательность оптимизации

1. Один acquisition и общий snapshot, затем устранение повторного decode, deep copies и ненужных allocations.
2. Безопасные циклы/итераторы по срезам на закреплённом stable toolchain. Проверить, что компилятор действительно векторизовал нужный участок; форма исходника и использование Vec сами по себе это не гарантируют.
3. Если измерения показывают существенную стоимость оставшегося kernel, сравнить ручной SIMD с scalar baseline. Кандидаты: пакетное преобразование порядка байтов, побитовое сравнение регистров, однородные численные операции при реально возникшей потребности.
4. Принять реализацию только при заранее заданном значимом выигрыше и неизменной корректности, ограничениях памяти и задержках полного пути. Если выигрыша нет, сохранить безопасный простой путь и записать результат.

SIMD не устраняет время ожидания mutex, сетевую задержку или COM вызовы. В исходном тестовом fixture 650 регистров (1300 bytes body); это один размер для benchmark, не установленный максимум production. Число элементов и нужность SIMD определяются реальной нагрузкой, а не названием технологии.

### 13.3. Stable, CPU и unsafe policy

Закреплённый Rust 1.99.1 сохраняется. В его документации `std::simd` отмечен как nightly-only (`portable_simd`); переключение production на nightly ради этого плана не допускается. `std::arch` имеет архитектурные intrinsics, но требует соблюдения CPU features и правил безопасности. Сам факт стабильности API не отменяет unsafe review.

Существующий `forbid(unsafe_code)` в application/shared crates не ослабляется. Native исключение `icem-windows-native` остаётся только Win32 boundary; SIMD не переносится туда ради обхода запрета. Первым вариантом остаётся автовекторизация безопасного Rust. Safe API сторонней SIMD-библиотеки рассматривается только после проверки её роли, поддерживаемого MSRV/CPU, лицензии, используемых features, unsafe implementation и изменений lockfile по действующим правилам. Новая библиотека этим документом не добавляется. Прямые unsafe intrinsics требуют отдельного архитектурного решения об изменении политики до реализации.

Если будет принят CPU-specific путь, он имеет совместимый scalar fallback; корректность не зависит от наличия AVX2. Выбор реализации локален kernel и проверяется для реально поддерживаемых архитектур/CPU. Нельзя включить `target-cpu=native` или AVX2 для всего распространяемого EXE и считать его пригодным для всех целевых машин. Широкое SIMD alignment не следует автоматически из выравнивания Vec/Box по T; загрузки, хвосты и границы проверяются выбранным безопасным API.

### 13.4. Доказательство корректности и пользы

| Проверка | Что требуется |
|---|---|
| Scalar equivalence | Те же decoded values/raw bits, metadata, error outcomes и полный Modbus register image; COM timestamp и reserve 49–56 неизменны |
| Float semantics | NaN payloads, infinities, +0/-0 и subnormals; change detection по raw bits, не float equality. Арифметика не получает неоговорённые reassociation/FMA/округление |
| Bounds и tails | Пустые/минимальные допустимые и реальные большие массивы, нечётное число регистров и длины не кратные числу SIMD lanes; truncated/malformed input сохраняет прежнюю политику ошибок |
| CPU fallback | Принудительно проверенные scalar и доступный accelerated paths; целевая Windows MSVC сборка, отсутствие illegal instructions на минимально поддерживаемом CPU |
| Snapshot ownership | Медленный consumer удерживает старый кадр; публикация нового не меняет его и не создаёт неограниченный backlog |
| Измерения | На одном оборудовании и сопоставимом Release profile: CPU/time capture+decode+publication, allocations и allocated bytes на capture/request, peak live snapshots/memory, MMF lock hold, p95/p99 Modbus/Web задержек |
| Нагрузка | Реальная частота единственного acquisition, несколько потребителей с разной частотой, LAN при замершем COM time и медленный/отключённый consumer. Microbenchmark kernel дополняет end-to-end measurement, не заменяет его |

До сравнения зафиксировать оборудование, toolchain, profile/target flags, размеры/частоты данных, warm-up и метод сбора распределений. Первый scalar correctness path остаётся эталоном проверки. Обещания кратного ускорения и отсутствие allocations без результатов не публикуются. Новый механизм принимается по v3.1 только с проверяемой пользой и сохранёнными инвариантами.

Источники: [Vec](https://doc.rust-lang.org/1.99.1/std/vec/struct.Vec.html), [portable SIMD](https://doc.rust-lang.org/1.99.1/std/simd/index.html), [architecture intrinsics и CPU dispatch](https://doc.rust-lang.org/1.99.1/std/arch/index.html), [rustc vectorization options](https://doc.rust-lang.org/rustc/codegen-options/index.html#no-vectorize-loops).

Это изменение плана. SIMD, новые зависимости и benchmark implementation ещё не реализованы.
