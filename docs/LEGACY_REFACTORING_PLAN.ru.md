# iCemDataMonitor: план рефакторинга и упрощения legacy .NET

Исторический план/отчёт своего checkpoint; факты и прежние failures сохранены.
Текущее завершение software и retirement 17-file closure описаны в
[C11](LEGACY_REFACTORING_C11.ru.md). Старые dormant route/serial references не
означают доступную текущую API или remaining migration.

Версия 1.0, 23 октября 2026. Статус: план, реализация не начата.
База исследования: `4578ab57266b7d25aed82df41c6276597a270c1e` в `boogy777-lgtm/iCemDataMonitor`. Исследованные .NET участки сверены с этой базой; выводы первоначального аудита использованы повторно.

## 1. Область и цель

Это отдельный план для существующего C#/.NET приложения: Host, Host.Core, Data, Transport, SDK, Plugins.Icem, HostClient, WinUI shell, emulator и net48 AddIn. Он дополняет Rust-план N+1, но не подменяется переносом улучшений в Rust.

Цель — уменьшить количество независимых правил, источников изменяемого состояния, повторяющихся операций и неиспользуемых путей. После каждого инкремента .NET приложение должно оставаться работоспособным. Дробление классов и число интерфейсов сами по себе не являются результатом.

В текущем запросе разрешены исследование, план и публикация документа. Исходники, MMF layout и CI не меняются. Будущая реализация этого плана — отдельная работа, включая согласование с сохранением migration baseline. Наличие политики замороженного .NET в Rust migration не отменяет запрошенный план C#; эту политику нельзя незаметно обойти при реализации.

Новый код сначала размещать в существующих проектах. Не добавлять универсальный plugin framework, второй command bus и новые assemblies ради симметрии. Для сервисного lifecycle использовать существующий .NET hosting и явное владение ресурсами.

## 2. Неизменяемые внешние контракты

| Контракт | Что сохранить |
|---|---|
| MMF | AddIn — единственный producer; Host read-only; имена mapping/mutex, layout5, byte order и адреса |
| Время | Общая source timestamp из регистров4–5, int32 Unix seconds; не заменять локальным now и не приводить к float32 |
| Резерв | Header49–56 остаётся для COM по решению пользователя; сдвигов карты нет |
| Асинхронные зоны | LAN/RAW могут обновляться при прежнем COM Time & Date; доставка не блокируется только из-за неизменного общего timestamp |
| Modbus | FC03/04/06/16, отдельное writable mirror, full overwrite активных зон на tick, inactive/reserve hold, stale behavior и resize |
| REST/gRPC/CLI | Действующие маршруты, proto field numbers, pipe names, stdout и exit codes |
| Команды слоёв | Существующий SetLayer фактически toggle; Web persist-only, OpcDa external no-op не меняются под видом refactor |
| UI и lifecycle | Host и shell имеют прежние роли; закрытие панели не останавливает Host; emulator независим |
| Эмулятор | COM/ACE bytes, units, DFB→freeze→manual→units порядок и существующие отличия encoder policies |
| AddIn | net48/vendor API, latch, poll resolution, per-pair atomicity и RAW per-line atomicity |

Изменение здорового внешнего поведения, request-driven read timing, stale semantics или timestamp history оформлять отдельной intentional delta. Документированную ошибку исправлять с конкретным acceptance, не объявлять такое исправление чистым переносом методов.

## 3. Фактический активный путь и исторический код

Активная композиция: `Host/HostAppFactory` → `Host.Core/HostComposition` → singleton BusReader → Modbus publisher, health monitor, ring sampler и RPC. Reader один как DI instance, но физических чтений несколько по независимым вызовам потребителей.

Подтверждения: `HostComposition.cs:64–80,103–109`; `ModbusTcpPublisher.cs:259–262`; `Host.Core/Health/BusHealthMonitor.cs:122–125`; `Data/Bus/BusRingService.cs:70–76`; `Host/Grpc/TelemetryServiceImpl.cs:114,186`.

`Core/Extensions/CoreServicesExtensions.cs:10–28` явно сохраняет v1 PipelineWorker/HealthCheckService и unbounded channels, которые текущие hosts не регистрируют. Наличие файлов и tests не доказывает production usage. Нельзя тратить основной refactor budget на неактивную оркестрацию.

Составить dependency/call map: entry point → DI registration → service → external contract → test. Отдельно отметить dormant endpoints: наличие Endpoint-файла не делает route активным. В частности, подписи из RegisterMirrorEndpoints проверяются только после установления фактической регистрации маршрута.

## 4. Backlog с доказательствами

В таблице пути после имени проекта относятся к корню репозитория. Номера строк относятся к исследованной базе.

| ID / приоритет | Факт | Изменение именно в legacy | Приёмка |
|---|---|---|---|
| L01 / P1 | `Host/Program.cs:117` читает INI, затем `Host/HostAppFactory.cs:83` читает его повторно | Один loaded boot context: options, фактический путь и выбранный bind plan. Build получает его, не читает файл снова | Подмена INI между load/build не смешивает настройки; CLI overrides и defaults сохранены |
| L02 / P1 | gRPC `TelemetryServiceImpl.cs:125–138` читает controller.Actual*, но probes из `LayerRuntimeController.cs:63–78` не подключены в factory `HostComposition.cs:131–136`; REST читает services напрямую `:216–219` | Сначала подключить фактические probes и проверить discrepancy; затем один Host runtime status snapshot с тонкими REST/proto projections | Bind failure/sniffer unavailable одинаково видны на поверхностях; requested/persisted/observed не слиты в один bool |
| L03 / P1 | `Data/Bus/BusReader.cs:69–80`: BusSnapshot record содержит публичный mutable ushort[] Words | Скрыть writable backing array, выдавать read-only view/accessors; после публикации данные больше не изменяются | Потребитель не может обычным API изменить чужой snapshot; endian/NaN/-0/int32 time сохранены; serializers совместимы |
| L04 / P1 | `Host.Core/LayerRuntimeController.cs:116–142,224–248`: Busy check/set без общей синхронизации | Сериализовать изменяющие команды через один существующий owner или async gate; Busy — наблюдаемый статус, не механизм исключения. Убрать sync wait поверх Task.Run в RPC пути | Concurrent commands не перекрывают actuator/persist, ошибки отражены, wire toggle semantics прежние. Гонка установлена по коду, не воспроизводилась |
| L05 / P2 | Modbus/health/history/RPC независимо вызывают BusReader.Snapshot | Один acquisition BackgroundService и read-only latest snapshot interface. После cutover потребители не владеют reader | HTTP load не увеличивает MMF read rate; Modbus timing измерен; отсутствие source не блокирует rest/control |
| L06 / P1 | `Plugins.Icem/DeviceCatalogRegistry.cs:15–20,40–79`: injected registry для catalogs, отдельный file load для Modbus metadata | Один injected immutable registry snapshot. Убрать скрытый второй file load и необоснованное повторное построение catalog | Injected registry, отличный от файла, последовательно используется всеми его потребителями; missing/malformed policy явная |
| L07 / P2 | FlecsVariableCatalog:15–48, PlcVariableCatalog:14–47, LanVariableCatalog:23–56 повторяют load/lookup/palette | Один небольшой catalog projector/helper в Plugins.Icem; family-specific offsets/fallback остаются явно заданными | Следующая same-class family задаётся данными/локальной policy; старые aliases, order и offsets совпадают |
| L08 / P1 | Shell MainWindow.xaml.cs:690–703 игнорирует ответ SetUiTheme; Host UiPrefsStore:83–99 подавляет write error | UI применяет возвращённое persisted state, RPC success не считается доказательством записи. Затем один небольшой preference store для net10 Host/Shell | Locked/read-only file не показывает ложное сохранение; offline fallback и reconnect определены |
| L09 / P2 | StationFileLog, AddIn TapLog и emulator CrashLog повторяют file append/rotation; Shell App.xaml.cs:68–76 пишет startup.log напрямую | Сначала net10 callers используют существующий StationFileLog с общей policy; timestamp формирует один sink. net48 отдельным шагом, без зависимости на net10 | Один timestamp, cap/retention, отказ log не ломает telemetry; clear только owned files, concurrent clear/append согласованы |
| L10 / P2 | Boot Modbus в HostComposition:256–315 и runtime actuator в DefaultLayerActuator:22–47 | Один внутренний lifecycle конкретного Modbus service; boot retry policy может отличаться. Типизированная bind failure, cancellation вместо Thread.Sleep | Retry cancel, repeat start/stop и rebind детерминированы. Не объявлять отсутствие await текущим дефектом: StartAsync сейчас синхронно bind-ит socket |
| L11 / P2 | Host Program.cs:279–312 содержит background IPC listeners с общим ресурсом без явного ожидания обоих | Сохранить task handles; cancel → дождаться обоих → dispose. Использовать host lifetime вместо разрозненных fire-and-forget | Restart/stop не оставляют handles/tasks; возможная dispose race не воспроизводилась |
| L12 / P2 | Emulator SignalWireCodec.cs:14–60 повторяет UnitConvertingPayloadProvider.cs:97–144 native decode | В .NET emulator один pure numeric decoder. Общая validated signal-definition parsing для Lantable files; merge policy отдельно | Bounds/endian/signedness/factor проверены vectors; не объединять разные encode policies |
| L13 / P2 | Emulator EmuEngineHost.cs совмещает timer, state, pipe, emitters и settings; MainWindow code-behind совмещает bootstrap/session/settings/view | Выделить только фактические seams: deterministic engine Step, runtime owner, command adapter; Shell session/bootstrap отделить от view. Не переписывать UI на новую framework | Engine проверяется без COM/Npcap/window; UI не дублирует command logic; конструкторы не выполняют необратимый I/O |
| L14 / P1 после проверки потребителей | Неактивный v1 pipeline держит no-op ConfigureAsync: SDK IModbusPublisher:13–18,29–30; Transport ModbusTcpPublisher:166–172 | Удалить неиспользуемый вертикальный срез вместе с его registrations/contracts/tests/dependencies после доказательства отсутствия внешних consumers | Рабочие entry points и package scripts не потеряли возможности; удалённые APIs не используются внешними clients |
| L15 / P3 | Core.Tests/ArchTests.cs:68–69 требует >=6 constructor dependencies | Заменить метрики формы реализации на проверки запрещённых project/namespace dependencies и поведения; сохранять применимые guards | Уменьшение числа зависимостей не ломает architecture test; неправильная зависимость обнаруживается |

Сейчас L03 является конкретным нарушением обещанной immutable surface, а не доказательством найденной порчи данных в runtime. Один BusReader не гарантирует единственного schedule чтения. Аналогично один status class без подключения фактических источников не гарантирует истинного статуса.

## 5. Целевая legacy-структура

Сохраняются существующие projects; сначала меняется ответственность модулей внутри них.

| Место | Владеет | Не делает |
|---|---|---|
| SDK / небольшой shared contract | Read-only snapshot access, stable IDs/types, прикладные порты | Не загружает каталоги и не знает UI/COM/HTTP |
| Data/Bus | Reader и acquisition owner, immutable latest state | Не вызывает потребителей последовательно и не генерирует source time |
| Transport/Modbus | TCP, PDU, register image и mirror policy | Не меняет MMF и не интерпретирует UI names |
| Host.Core | Composition, runtime state, команды и startup policy | Не содержит копий Web/proto serialization и WinUI rules |
| Plugins.Icem | Registry/catalog projections и iCem metadata | Не создаёт второго владельца source data |
| Host REST/gRPC | Входные DTO и output projections | Не читает MMF заново на каждый request и не копирует lifecycle rule |
| Shell / Emulator UI | Представление и input bindings | Не решает повторно, как работает Host command |
| Emulator Core | Pure engine/codec и явные providers | Не смешивает numeric law с pipe/COM ownership |
| AddIn net48 | Vendor boundary, producer и существующие проверенные helpers | Не тянет net10 SDK и не меняет wire map |

Existing Data project ссылается на Plugins.Icem; Core — на Data/Transport/Plugins. После удаления доказанно лишнего кода повторно проверить, нужны ли эти references. Не удалять зависимость по эстетике: сначала убрать конкретные use sites. Предпочтительное направление — composition знает реализации, contracts не знают transport/UI. Создание новых проектов допускается только при реальной границе и уменьшении независимых правил.

## 6. Упрощение активного MMF-конвейера

1. Reader остаётся единственным владельцем MMF handles и mutex. Acquisition service — единственный caller его Snapshot после cutover.
2. Consumer получает latest read-only snapshot через короткую atomic reference publication или короткий lock. Через await, socket write и callback общий lock не удерживается.
3. Не использовать Channel с одним competing reader на несколько consumers: они должны видеть одно состояние, а не делить сообщения между собой.
4. Публикуется последнее состояние, промежуточные версии можно coalesce. Event log с гарантией каждого измерения — отдельная задача.
5. Modbus обновляет writable image на каждом tick даже при одинаковых bytes источника, сохраняя overwrite внешних FC06/16 writes в active zones. Latest-state publication не отменяет его periodic policy.
6. Health/history имеют свои часы и интервалы, но не инициируют source I/O. Изменение acquisition phase и cache age оформляется как timing delta с измерениями.
7. Source timestamp берётся из4–5. Независимое LAN update проходит при прежнем timestamp. Общая метка не является ни unique frame ID, ни availability gate.
8. No source: продолжаются service loops/ответы с определёнными retained/absent результатами; отсутствующие значения не выдаются как новые нули или новое время. Существующие no-snapshot Modbus header zeros остаются его protocol rule.

.NET record не делает backing array неизменяемым. Нельзя опубликовать pooled buffer, вернуть его в pool и продолжить отдавать ссылку consumers. Избежать копий можно владением одной completed buffer и безопасным read-only API; не добавлять unsafe ради экономии нескольких массивов до profiling.

## 7. AddIn: небольшой целевой рефакторинг

AddIn включён в этот legacy-план как действующий net48 компонент, а не кандидат на слепое замораживание навсегда.

Уже существующие helpers сохраняются: PollResolution, ComFeedLatch, LanSourceTracker, BusWriteQueue, RawCsvFields. DataMonitorModule должен координировать их, а BusWriter — владеть MMF и codecs. Извлекать чистую политику из длинных методов по реальным seams, не создавать GenericController с mode flags.

Минимальные задачи:

- отделить подготовку COM/LAN write requests от вызовов vendor object model;
- сохранить source-order, first-convertible и fresh-first tie-break;
- объединить повторённые bounds/codec primitives без изменения wire representation;
- явно закрепить policy времени: COM/LAN zone metadata обновляется на изменении пары; RAW на line arrival;
- сохранить per-pair/per-line mutex scope; не обещать atomic measurement frame всего массива;
- net48 logging делить с net10 только через совместимый BCL/source component, если reuse оправдан. Иначе две маленькие реализации с одними vectors предпочтительнее нового dependency tower.

Не менять reserve49–56, не переписывать4–5 локальными часами и не повышать layout version для структурного refactor.

## 8. Как переиспользовать без изменения смысла

Numeric decode в emulator одинакового native payload формата можно объединить. Monitor ACE full-frame decode имеет другие offsets, factor/sentinel/HX formatting — общими являются primitives, а не весь high-level метод.

Base/manual integer encoder и units converted encoder имеют разные width/round/clamp policies. Один общий метод с множеством bool flags не считается улучшением. Сохранить явные policies и общий primitive там, где law совпадает.

Catalog identity и адресная карта не одно понятие. Необязательный словарь помогает UI, но базовые var… и register routing не должны зависеть от его успешной загрузки. Существующие registry-dependent sniffer rules сохранять до отдельной функциональной задачи, не исправлять incidental coupling незаметно.

BusLayout net48 и SDK дублируют схему на разных target frameworks. На первом этапе применять общие golden bytes и geometry checks, не объединять их несовместимой ссылкой сборок. Generator оправдан только измеримой стоимостью регулярного изменения схемы.

Собранный main.js содержит библиотечный grpc-web code; большой размер не доказывает монолит author code. Не дробить bundle вручную. Восстановление frontend source project — отдельная работа; frozen assets не заменять generic UI.

## 9. Порядок небольших PR

| Пакет | Объём | Условие завершения |
|---|---|---|
| C0 | Карта активных сценариев, tests baseline и список внешних потребителей | Найдены production entry points; inactive не спутано с обязательным |
| C1 | L01: read-once config | Один boot context, precedence сохранён |
| C2 | L02/L04: observed status и сериализация команд, отдельные commits | Ошибки bind/persist и concurrency имеют определённые результаты |
| C3 | L08: проверка результата theme, затем общий preference store | UI отражает реальное saved state при fault/reconnect |
| C4 | L06/L07: один registry snapshot, затем projector | Потребители одного registry не расходятся; семейные политики сохранены |
| C5 | L03: безопасная snapshot surface | Подготовлена база общего fan-out; compatibility compile sites проверены |
| C6 | L05: single acquisition owner, cutover Modbus/Web/health/history | Нет прямых reader calls у consumers; latency/cache semantics приняты |
| C7 | L09: logger reuse и единый формат записи | Бounded owned logging без дублирования timestamp/rotation |
| C8 | L10/L11: lifecycle и shutdown ownership | Stop/restart закрывают workers/handles и повторный bind успешен |
| C9 | L12/L13: emulator codec/engine seams, затем AddIn seams | Golden vectors и source behavior сохранены; проект остаётся net48-compatible |
| C10 | L14/L15: удалить v1 срез, ослабить связанные зависимости/неверные tests | Каждый удалённый элемент имеет доказательство отсутствия потребителя |

Доказанные отдельные unused methods и package references можно удалить раньше, но только отдельным малым diff с проверкой build/packaging. Не смешивать deletion, semantic defect fix и большой перенос файлов в одном PR. Общий план не означает, что всё должно быть выполнено одной веткой.

## 10. Приёмка и измеримые результаты

На этапе реализации сначала использовать существующие tests: BusLayout/BusWriter/CdabCodec, BusOpenTruthTable/BusDecodeTruthTable, ModbusContract, TelemetryServiceImpl, LayerRuntimeController, HostClient, BusAddrRingService, LantableRegistry и emulator wire tests. Дополнять отсутствующими fault/concurrency vectors по изменяемому поведению; не писать tests ради зеркалирования реализации.

Обязательные сценарии:

- startup без Data.dat, затем появление producer; retained после исчезновения файла;
- source time4–5 стоит, но LAN меняется; независимая остановка COM/LAN;
- MMF lock timeout и source failure не блокируют Host command processing;
- FC06/16 writes в active/inactive/reserve, resize-before-stale;
- разные request rates Web и Modbus без изменения MMF acquisition rate;
- ошибка сохранения theme/INI, повтор команды, competing layer changes;
- нет/повреждён registry, dict optional path, устойчивые var identities;
- graceful stop/restart во время активных RPC/COM/Npcap operations;
- fixed/frozen payload, manual overrides, units conversion и firmware selection.

Снять before/after measurements: число MMF capture/copy/decode в секунду, allocations per capture/request, p95/p99 Modbus tick delay при Web нагрузке, bounded history/log size и stop duration. Целевые числа задаются из baseline и эксплуатационных требований, а не из размера класса.

Критерии упрощения: один boot-load; один registry owner; один физический acquisition schedule после C6; одна policy status и commands; меньше фактически неиспользуемых проектов/packages; новый same-class consumer требует adapter+DI registration. LOC и число interfaces — вспомогательные показатели.

Windows build/integration и live operator acceptance обязательны для затронутых Windows границ. Linux tests не доказывают WinUI, MMF, pipe или COM. В рамках подготовки этого документа tests/build/live не запускались.

## 11. Class / Inv / N+1 по v3.1

| Изменение | Class / ближайший nonmember | Inv и проверка | N+1 |
|---|---|---|---|
| Read-once boot | Настройки одного запуска / runtime commands вне класса | Composition и startup видят один объект; load/build fault test | Новое поле меняет schema и потребителя, не создаёт новый file read path |
| Shared snapshots | Потребители текущего состояния / event log вне класса | One owner, no shared mutations; stressed slow-consumer test | Consumer+DI, без изменения reader/других consumers |
| Layer mutations | Меняющие слой команды / telemetry read вне класса | Одна mutation за раз; concurrent/failure tests | Новый handler проходит ту же serialization, не добавляет другой busy механизм |
| Catalog projection | Catalogs из одного registry / wire decode вне класса | Единая версия и сохранённые aliases/offsets; fixtures | Новая family description/local policy без нового loader |
| Managed logs | Owned diagnostic files / Data.dat вне класса | Bounded append/clear, writer ownership, fault vectors | Channel descriptor/policy вместо отдельного logger |
| Pure emulator decode | Один native payload law / units encoder вне класса | Bounds/endian/signedness; golden vectors | Новый encoding добавляет явную policy и vectors |

Перед кодом каждой future task записать Class, Inv, N+1 в2–4 строки с целевыми paths. Новый механизм допустим, если обеспечивает новый проверяемый инвариант. Не переносить доменные знания в core ради DRY и не вводить unrelated coupling для сокращения нескольких строк.

## 12. Связь с Rust migration и baseline

.NET и Rust планы независимы по реализации, но должны использовать одну согласованную MMF/внешнюю семантику. Нельзя требовать готовности всех C# refactors для продолжения Rust migration или автоматически переносить новые class hierarchies в Rust.

Pinned source manifest остаётся историческим эталоном, а не указателем на последнюю legacy ветку. При реальной C# реализации нужно сохранить старые oracle fixtures, записать provenance intentional deltas и обновить только соответствующие текущему legacy gates/allowed deltas. Не менять baseline hashes, чтобы скрыть drift. Производственные исправления C# могут требовать отдельных comparisons в Rust, но структурные переносы не обязаны порождать Rust изменения.

Рекомендуемый первый implementation increment — C1 read-once config: два подтверждённых места чтения, небольшой diff, понятная проверка и отсутствие MMF/протокольной миграции. Затем C2/C3. Глубокое изменение acquisition выполнить после immutable snapshot и timing baseline.

Документ не запускает рефакторинг автоматически. В этом изменении публикуется только план legacy .NET; исходники C#, Rust и AddIn не изменены.
