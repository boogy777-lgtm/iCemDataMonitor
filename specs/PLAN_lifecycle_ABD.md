# PLAN — Lifecycle ремедиация A → B → D (SWARM-декомпозиция)

Исторический legacy lifecycle plan. Текущая software архитектура —
[docs/architecture.md](../docs/architecture.md). Endpoint/B5 references ниже
относятся к прошлому; 21 dormant route retired [C11](../docs/LEGACY_REFACTORING_C11.ru.md).

> **ОБНОВЛЕНИЕ 2026-08-24 (W19, решения владельца) — ЭТАП E, ПЛАНИРУЕТСЯ:**
> три новых решения поверх выполненного BD:
> **E.1 ТОКЕН ДЛЯ WEB ОТМЕНЁН** — «всё будет в закрытом контуре + web»:
> HostTokenInterceptor/HostTokenStore/host.token выводятся; gRPC-Web вызовы
> анонимны; SPA токен-диалог удаляется. Безопасность = изоляция контура
> (фиксируется как осознанное решение; pipe остаётся под дефолтным DACL
> пользователя).
> **E.2 HOST ПОЛУЧАЕТ СВОЙ ТРЕЙ** — headless-хост живёт в трее; из трея
> запускается UI; ПОЛНОЕ закрытие UI (¬сворачивание) НЕ закрывает хост.
> Меню трея хоста: [Открыть панель/UI] · [Перезапуск] · [Закрыть].
> **E.3 ЕДИНСТВЕННОСТЬ СЕРВЕРА/ЭМУЛЯТОРА** — UI при старте запускает свой
> сервер, если он уже запущен — копия НЕ запускается (ensure-host уже так
> работает — подтвердить тестами); сервер и эмулятор — ровно один экземпляр;
> добавить взаимное исключение Host ↔ legacy Tray backend (двойной reader
> bus / конфликт :502 запрещены структурно).
>
> Детальная декомпозиция Этапа E — в конце файла.

> **ОБНОВЛЕНИЕ 2026-08-24 (W18, решение владельца): этапы A и B-as-planned
> ПРОПУЩЕНЫ, этап D ПЕРЕРАБОТАН в «BD» — гибрид B-топологии и D-транспорта.**
> Вводные game changer: на ПК инженера стоит UAC, установка ПО требует одобрения
> администратора ⇒ **путь Windows Service ОТКЛОНЁН** (D.3 в исходном виде мёртв).
> Целевая топология = **B**: headless `iCemDataMonitor.Host.exe` как процесс
> ПОЛЬЗОВАТЕЛЯ + UI-клиенты. Транспортная часть D (gRPC dual-endpoint, единый
> .proto, токен) от службы НЕ зависит и СОХРАНЕНА — D.0/D.1/D.2/D.4/D.5/D.6
> действуют с поправками ниже (поправка W19: токен отменён, см. Этап E). Выполнено к моменту решения: D.1 целиком
> (Contracts net10 + telemetry.proto + codegen×3 + golden-tests), спайки
> D.1.0a GREEN / D.3.2 GREEN (последний теперь информационный), D.4 частично
> (MetricsSampler; тесты pending). Подробности аудита прерванной сессии —
> STATE.md §18.

> Источник: аудит W17b (2026-08-21, STATE.md §17). Цель владельца: «тонкая связь —
> не терять управление приложениями». Доктрина: contract-first (record + contract-test
> ДО логики, §15.7); Core НЕ маршрутизирует payload; CliWrap only для спавна;
> процессы владельца не убивать; UI-сборка только `-p:Platform=x64`;
> деплой = bin\x64\Debug.
>
> База тестов на старте: Cli 17, Sdk 70, Data 84, Core 46, Plugins 147, UI 59,
> Transport 70/73 (3 live-gated). Итого 493 юнит + live-gate.
> Каждый этап ДОЛЖЕН заканчиваться green build (0 err) + неубывающим числом тестов.

---

## ЭТАП 0 — Гейт входа (перед A)

| # | Условие | Кто проверяет |
|---|---|---|
| 0.1 | Ordered restart выполнен владельцем: стоп всех процессов + iCem.exe → iCem.exe ПЕРВЫМ (создаёт Data.dat с НОВОЙ картой `Global\iCemDataMonitorBus_v1`) → новый `iCemDataMonitor.WinUI.exe` | владелец |
| 0.2 | Live-проверка: `icemdatamonitor-cli header` — heartbeat тикает; `curl :8090/api/status` — busOpen=true | insite-core |
| 0.3 | Корневой каталог переименован (iCemPOC → iCemDataMonitor) ИЛИ решение отложено — пути в run-live-gate.ps1/LiveGateGui/tools зафиксированы | insite-core |
| 0.4 | `C:\ProgramData\Halliburton\StationBus\` удалён (после 0.1) | владелец |

---

## ЭТАП A — «Монолит + гарантированный recall» (1–2 дня)

Цель: закрыть GAP-1, GAP-3, GAP-5(partial), GAP-6(partial), GAP-7. Архитектура
НЕ меняется. Только управляемость и наблюдаемость.

### A.1 [insite-ui] Fail-safe close-to-tray (GAP-1, корневая боль)
Файлы: `iCemDataMonitor.Shell.WinUI\MainWindow.xaml.cs:123-141`,
`TrayIconHost.cs:16-54`, `App.xaml.cs:58-106`.
- A.1.1 `TrayIconHost` → exposed свойство `bool IsTrayIconUsable` (icon created ∧
  context menu registered). ForceCreate результат логировать в startup.log.
- A.1.2 `AppWindow.Closing`: ветвление —
  · tray usable → текущее поведение (Cancel+Hide);
  · tray NOT usable → MessageBox «Tray недоступен: [Скрыть без трея] [Выход]
    [Отмена]»; дефолт = Отмена (окно остаётся). НИКОГДА не прятать окно в
    неуправляемый процесс.
- A.1.3 `SessionEnding`/`WM_QUERYENDSESSION` → graceful: `_host?.StopAsync(5s)` +
  kill own engine-child (сейчас только «Выход» из трея это делает,
  App.xaml.cs:113-115).
- A.1.4 Второй инстанс (App.xaml.cs:71-78): если mutex занят, но named event
  `ShowWindow` не открывается/таймаут 3с → MessageBox «Экземпляр запущен, но не
  отвечает: [Активировать ещё раз] [Завершить его и запустить заново] [Отмена]»
  вместо молчаливого выхода. Kill чужого процесса — ТОЛЬКО по явному выбору.
- A.1.5 Named event создавать ДО ShellHost.Start (долгий синхронный старт не
  должен блокировать recall).
- Тесты (UI.Tests): TrayToggleRules-стиль юниты на решающую таблицу
  ClosingDecision(trayUsable, mode) → {HideToTray, AskUser, Cancel}; юнит на
  second-instance decision table. DoD: UI.Tests 59 → 59+N green.

### A.2 [insite-core] Минимальный control surface (GAP-6)
Файлы: `ShellHost.cs:104-149`, `iCemDataMonitor.Host.Core\Endpoints\*`,
`iCemDataMonitor.Cli\Program.cs`.
- A.2.1 Contract-first (sdk, см. A.2.0 ниже) затем endpoint `POST /api/host/shutdown`
  → graceful `IHostApplicationLifetime.StopApplication()` + own engine kill;
  ответ 202 + JSON {verdict}. Guard: только loopback (RemoteIpAddress ∈
  127.0.0.1/::1), иначе 403.
- A.2.2 Подключить СУЩЕСТВУЮЩИЕ спящие endpoints в прод-маппинг (сейчас только в
  B5ContractTests): SnifferEndpoints, Com0ComEndpoints, ModbusPreviewEndpoints —
  read-only, риск низкий. SourceEndpoints/RegistryEndpoints/TemplatesEndpoints —
  аудит перед включением (могут тащить write).
- A.2.3 `icemdatamonitor-cli status` — GET :8090/api/status/summary → тот же JSON
  на stdout (pure-JSON контракт CLI сохранить); при web_on=0 → честный
  exit code 2 + {error:"web_off"} (¬молчание).
- A.2.4 Startup-signal: ShellHost.Start пишет однострочный READY-маркер в
  startup.log («READY modbus=bound web=:8090 bus=open») — машиночитаемый якорь
  для скриптов и live-gate.
- Тесты: contract-test на shutdown endpoint (loopback guard — обе ветки),
  cli status parse; DoD: Core.Tests 46→+, Cli.Tests 17→+ green.

### A.2.0 [insite-sdk] Контракты control API (блокирует A.2)
Файлы: новые records в `iCemDataMonitor.Sdk` (напр. `Control\HostControlContracts.cs`).
- A.2.0.1 record `ShutdownRequest{}`/`ShutdownAck{Verdict, StoppingLayers[]}`,
  `CliStatusResult` — additive, SchemaΔ первым артефактом.
- A.2.0.2 Contract-tests в Sdk.Tests: roundtrip JSON (camelCase policy как у
  /api/status), unknown fields tolerated (forward-compat).
- DoD: Sdk.Tests 70→+ green. Seam-классификация: schema-delta (§15.7) —
  ¬strategy-классы.

### A.3 [insite-ui] Видимый старт + живой трей (GAP-3, GAP-7)
- A.3.1 Splash/progress в WinUI shell на время ShellHost.Start: минимальное окно
  со стадиями (ini → bus → modbus → web → sniffer → ready), источник стадий —
  READY-маркер-модель из A.2.4 (события ShellHost, ¬парсинг лога в UI).
  Дедлайн-страж: старт >30с → явное «завис на стадии X» (¬вечный спиннер).
- A.3.2 Tray tooltip динамический (TrayIconHost): «iCem Data Monitor —
  Modbus:502✓ Web:8090✓ Bus:open HB 3s» из StationHealthState (уже в DI);
  обновление 1Гц подпиской на BusHealthMonitor.
- A.3.3 Tray-меню: + «Состояние» (открыть Status-страницу), + «Дашборд»
  (localhost:8090). Пункт «Выход» — с confirm при running engine.
- Тесты: UI.Tests на tooltip-форматтер (pure fn). DoD green.

### A.4 [insite-core] Engine-lifecycle fail-safe (GAP-4, минимум)
- A.4.1 Учёт engine-children в ShellHost: реестр spawned PIDs (incl. те, что
  spawned embedded UI — embedded UI сообщает свой engine PID через pipe-команду
  `status` уже отдаёт PID? если нет — additive поле в status, contract-first).
- A.4.2 ShellHost.Dispose (:395-398): kill ВСЕХ учтённых engine PIDs,
  ¬только своего.
- A.4.3 CloseChild (EmulatorEmbedHost, ui-зона — координация): перед
  Process.Kill(UI) → попытка graceful `stop` по pipe (2с таймаут) → затем kill;
  engine явно добивается хостом по A.4.2.
- Тесты: Core.Tests на PID-реестр (add/remove/killAll semantics, fake processes).
- DoD green; ручной сценарий: закрыть шелл при работающем эмуляторе → в
  Task Manager не остаётся iCemEmule.exe.

### A.5 [insite-transport] Pipe-наблюдаемость эмулятора (GAP-5, минимум)
- A.5.1 EmuPipeClient: счётчик подряд-неудач + последняя ошибка (для UI-статуса);
  contract клиента не ломать (connect-per-command остаётся — persistent channel
  отложен в B).
- A.5.2 Transport.Tests: fake pipe server — fail-счётчик, timeout-ветки.
- DoD: Transport 70/73→+ green.

### ЭТАП A — Гейт выхода (все сценарии вручную + green):
1. Запуск шелла → виден splash со стадиями → READY.
2. Закрытие окна → трей ГАРАНТИРОВАННО есть (или окно не скрыто).
3. Повторный запуск exe → окно активируется; при мёртвом первом — диалог.
4. Трей-меню: Состояние/Дашборд/Выход работают; tooltip живой.
5. `icemdatamonitor-cli status` возвращает JSON; `POST /api/host/shutdown`
  с localhost гасит хост graceful; с не-loopback — 403.
6. Закрытие шелла с работающим эмулятором → engine не осиротел.
7. sln build x64 0 err; все тесты green; STATE.md ← W18.

---

## ЭТАП B — «Headless Host + UI = viewer» (1–2 недели)

Цель: физическое разделение тела и голов (GAP-2, GAP-4 полный, GAP-5 полный).
Новый процесс `iCemDataMonitor.Host.exe` владеет ВСЕМИ сервисами; WinUI/Tray —
клиенты. A-артефакты (control API, cli status) переиспользуются.

### B.0 [insite-sdk] Контракты управления хостом (блокирует всё B)
- B.0.1 Records: `LayerControlRequest{Layer, Action}`, `LayerState{Layer, Status,
  SinceUtc, Detail}`, `HostStatus{Version, Pid, StartedUtc, Layers[], Engine}`,
  `EngineControlRequest{Action}`, error-конверт {Code, Message, Runbook}.
  Все — additive-эволюционируемые, camelCase JSON.
- B.0.2 Версионирование: `HostStatus.ProtocolVersion` (int, старт=1); клиент
  обязан терпеть неизвестные поля; контракт-тест roundtrip + forward-compat.
- B.0.3 Named-pipe control канал UI↔host? На этап B — НЕТ, канал = HTTP loopback
  (порты уже есть). Pipe остаётся только UI↔engine. ВНИМАНИЕ: этап D (D.0,
  решение владельца) заменяет HTTP control plane на gRPC dual-endpoint —
  B-контракты проектировать с расчётом на 1:1 перенос полей в .proto (D.1.1).
- DoD: Sdk.Tests contract-suite green; specs\CONTRACT_HostControl.yaml создан.

### B.1 [insite-core] Вынос хоста (ядро этапа)
- B.1.1 Новый проект `iCemDataMonitor.Host` (console, BackgroundService-модель):
  перенос DI-регистрации из ShellHost.cs:225-285 / Tray Program.cs:478-548 в
  общий `HostComposition` (единая точка — сейчас ДВА зеркальных списка, дрейф).
  CliWrap не нужен (хост сам — процесс); запуск пока ручной/из tray.
- B.1.2 Single-instance хоста: НОВЫЙ mutex `Global\iCemDataMonitorHost_v1`
  (¬трей-mutex — хост и трей теперь РАЗНЫЕ роли и могут жить вместе).
  Повторный запуск хоста → exit 1 + stderr (fail-fast, ¬молчание).
- B.1.3 Control endpoints (на базе A.2): POST /api/layers/{kind}/start|stop
  (через LayerRuntimeController — уже существует), POST /api/emule/start|stop,
  POST /api/host/shutdown, GET /api/host/status (B.0.1). Все write — loopback
  guard. LayerToggleRules остаются законом (Web = persist-only).
- B.1.4 Engine supervision в хосте (полный GAP-4): владение iCemEmule.exe
  переходит хосту ИСКЛЮЧИТЕЛЬНО (UI больше НИКОГДА не спавнит engine —
  EmulatorPaths.LaunchEngine/EmulatorViewModel:136 reroute → POST /api/emule/start).
  Watchdog 1Гц: HasExited → reincarnate с anti-bounce 3s→6s→12s→24s (cap 60s),
  max 5 подряд → fatal-state в HostStatus (¬молчаливый цикл). FSM_C канон.
- B.1.5 Конфиг: station.ini — хост читает next-to-exe (как сейчас); рассинхрон
  ini между host/shell/tray устраняется тем, что ЧИТАЕТ ini ТОЛЬКО хост;
  UI получает флаги через /api/host/status. IniConfig не трогаем (data-зона B.5).
- B.1.6 startup.log хоста + READY-маркер (A.2.4 наследуется).
- Тесты: Core.Tests — supervision FSM (reincarnate/backoff/fatal), loopback
  guard на каждый write-endpoint, composition smoke (host starts, /api/host/status
  200). DoD green.

### B.2 [insite-ui] Перевод UI в режим клиента
- B.2.1 WinUI shell: режимы запуска — `default` = клиент (ensure-host:
  host жив? → connect; нет → диалог [Запустить хост] [Только просмотр bus]
  [Выход]); флаг `--host` = legacy in-proc режим (откатной путь на этап B,
  удаляется в D). Mutex шелла освобождается от роли «владелец бэкэнда».
- B.2.2 StatusViewModel → источник данных переключается BusReader-in-proc →
  HTTP-клиент /api/host/status (polling 1Гц); stale-индикация: потеря связи с
  хостом >3с → баннер «Хост недоступен» + кнопка reconnect (¬stub-молчание).
  Тогглы слоёв → POST /api/layers (A/B.1.3) вместо in-proc LayerRuntimeController.
- B.2.3 EmulatorPage: embed-механика без изменений, но engine lifecycle — только
  через host API (B.1.4); parent-pid watchdog сохраняется; при смерти хоста
  embedded engine НЕ умирает (хост владелец, UI — зритель) — показать
  состояние «engine жив, хост мёртв» невозможно по построению (хост=владелец
  pipe? нет — pipe UI↔engine прямой, остаётся; статус engine виден и при
  мёртвом... нет: watchdog engine = хост. UI показывает pipe-status engine
  напрямую + host-status отдельно — два индикатора, явно).
- B.2.4 Legacy Tray → роль «host-агент»: меню [Запустить хост] [Остановить хост]
  [Панель] [Выход]; сам бэкэнд НЕ хостит (in-proc StartWeb/StartHeadless
  decommission). Откатной exe остаётся собираться, но без hosted services.
- B.2.5 Трей шелла: + индикатор «host: alive/dead» в tooltip (A.3.2 расширение).
- Тесты: UI.Tests — ViewModel stale-логика, решение ensure-host таблица,
  toggle→POST маппинг (fake HTTP). DoD green.

### B.3 [insite-transport] Persistent status-канал engine (GAP-5 полный)
- B.3.1 EmuPipeClient v2: долгоживущее соединение + reconnect backoff
  (1s→2s→4s, cap 15s) + событие ConnectionStateChanged; команды мультиплексируются
  поверх одного pipe (JSON-lines framing уже есть). Старый connect-per-command
  остаётся для --ctl (одноразовый режим).
- B.3.2 Engine side (ControlPipeServer) — проверить устойчивость accept-loop к
  долгоживущим клиентам; изменения минимальны (zone: transport координирует с
  владельцем движка).
- Тесты: Transport.Tests — reconnect-таблица, framing при разрыве посреди line.
- DoD green.

### B.4 [insite-plugin] AddIn — точка невовлечённости (guard-задача)
- B.4.1 Подтвердить contract-test'ом: AddIn ↔ bus контракт (Global\
  iCemDataMonitorBus_v1, layoutVer5) НЕ зависит от того, кто хостит BusReader —
  in-proc или Host.exe. ¬изменений в AddIn. Если контракт-тест красный — СТОП,
  эскалация (значит в B.1 просочилась domain-логика в хост-состав).
- B.4.2 specs\iCemAddIn: топология Э1 — потребитель bus = «Host.exe (вне AddIn
  scope)», правка одной строки.

### B.5 [insite-data] BusReader/конфиг — guard + мелочь
- B.5.1 BusReader одновременное использование host.exe + shell-клиентом: хост —
  единственный ЧИТАТЕЛЬ bus среди серверных процессов; shell больше не открывает
  BusReader в client-режиме (иначе двойной ring/health-дрейф). Guard-тест:
  BusReader открывается только в Host (grep-страж в ArchTests).
- B.5.2 BusHealthMonitor/StationHealthState переезжают в хост без изменений
  логики (перенос registration only); Data.Tests остаются green без правок —
  подтвердить.

### ЭТАП B — Гейт выхода:
1. Host.exe standalone: стартует, READY-маркер, все слои, cli status green.
2. Shell без хоста → диалог ensure-host (3 ветки работают).
3. Shell + хост: статус/тогглы/эмулятор через API; kill хоста → баннер +
   reconnect; хост поднялся → UI сам восстановился.
4. Kill engine → хост reincarnate с backoff (видно в /api/host/status);
   5 подряд → fatal-state.
5. Закрыть shell полностью → хост и engine живут; открыть shell → полный
   контроль восстановлен (= корневая боль владельца закрыта СТРУКТУРНО).
6. Legacy tray как host-агент: старт/стоп хоста из меню.
7. sln 0 err; все тесты green; live-gate green; STATE.md ← W19;
   specs: CONTRACT_HostControl.yaml + Э1/Э2 топология обновлены.

---

## ЭТАП D — «Windows Service + gRPC-транспорт» (2–4 недели, ТОЛЬКО после ≥1 нед стабильного B)

Цель: стенд-прибор 24/7 + единый gRPC-канал UI↔служба (локальный WinUI,
локальный и УДАЛЁННЫЙ браузер в доверенном контуре).

### D.0 АРХИТЕКТУРНОЕ РЕШЕНИЕ ВЛАДЕЛЬЦА (2026-08-21, принято, ¬пересматривать)

- Транспорт данных/управления UI↔служба = **gRPC, единый .proto** на все стороны.
- Kestrel службы слушает ДВА endpoint одновременно:
  1. **Named pipe `iCemDataMonitor.Host`** — WinUI 3 на той же машине
     (ConnectCallback + NamedPipeClientStream);
  2. **TCP :8090 ListenAnyIp** (HTTP/1.1 + gRPC-Web middleware) — браузеры
     локально и удалённо; тот же endpoint отдаёт статику SPA
     (UseDefaultFiles + UseStaticFiles + MapFallbackToFile("index.html")) →
     same-origin, CORS отсутствует по построению.
- Один TelemetryService обслуживает все каналы; сервис НЕ знает канал вызова.
- Безопасность: pipe = явный PipeSecurity ACL (service account + интерактивный
  пользователь, дефолтные права ЗАПРЕЩЕНЫ); TCP = токен в gRPC-metadata на
  КАЖДЫЙ вызов (интерсептор), токен обязателен даже в контуре, хранение =
  ACL-файл рядом со службой; TLS off (контур изолирован, зафиксировано);
  компромисс «токен открытым текстом внутри контура» принят.
- ЗАПРЕЩЕНО: REST/Minimal API для данных, SignalR/WebSockets, raw gRPC для
  браузера (только gRPC-Web), внешние прокси (Envoy), CORS-политики, второй
  .proto, client/bidi-стриминг через gRPC-Web.
- Эволюция контракта: additive-only, номера полей никогда не переиспользовать
  (= наша доктрина SchemaΔ §15.7 — совместимо).
- Стыковка с A/B: REST control surface из A.2/B.1 — ПЕРЕХОДНЫЙ; в D control-
  операции переезжают в тот же .proto (additive RPC); существующие read-only
  /api/status* замораживаются как legacy ops-surface, новые data-endpoints
  НЕ добавляются; SPA дашборда переходит на gRPC-Web, wwwroot-моки выводятся.

### D.1 [insite-sdk] Contracts-проект — единый источник правды (блокер D)
- D.1.0 TFM-директива владельца: этап D = **.NET 10**. Contracts и Host
  создаются сразу на `net10.0`; связанные клиенты (Shell/Tray/Emulator.WinUI)
  поднимаются на net10.0 в рамках D.5; проекты, не вовлечённые в D (Data,
  Transport-ядро, Plugins), остаются net8.0 до отдельного решения — смешанный
  TFM в солюшене допустим (net10.0-проект ¬ссылаться на... нет: net10 МОЖЕТ
  ссылаться на net8-библиотеки; проверить совместимость WinUI 3 / WASDK на
  net10 в spike D.1.0a — полдня, gate).
- D.1.1 Новый проект `iCemDataMonitor.Contracts` (net10.0, Grpc.Tools):
  `telemetry.proto` → service TelemetryService {
    `GetStatus(HostStatusRequest) returns (HostStatus)` — unary
    (payload = модель B.0.1 HostStatus, перенос полей 1:1);
    `SubscribeMetrics(SubscribeRequest{interval_ms}) returns (stream MetricsSample)`
    — server-streaming, сенсор читается в момент отправки;
    control-RPC (additive, перенос A.2/B.1.3): `SetLayer(LayerControlRequest)
    returns (LayerState)`, `ControlEngine(EngineControlRequest) returns
    (EngineState)`, `ShutdownHost(ShutdownRequest) returns (ShutdownAck)` }.
  Номера полей — реестр в комментарии .proto; reserved-блоки под удалённое.
- D.1.2 Codegen ×3 из ОДНОГО .proto: server (Grpc.AspNetCore), WinUI
  (Grpc.Net.Client + Google.Protobuf), web (protoc-gen-grpc-web,
  @improbable-eng/grpc-web, binary mode). Скрипт `tools\gen-proto.ps1`
  (все три генерации одной командой; вывод web → SPA src\generated\, в git? нет
  — генерится при build, msbuild/targets хук).
- D.1.3 Contract-тесты (Sdk.Tests): golden-file на .proto (номера полей
  заморожены — diff = fail), roundtrip message↔JSON где применимо,
  additive-check (новый файл обязан быть надмножеством golden).
- DoD: Sdk.Tests green + golden-freeze test; specs\CONTRACT_HostControl.yaml
  заменяется ссылкой на .proto (proto = канон, yaml = удалить во избежание
  двух источников — урок CONTRACT_BusV2).

### D.2 [insite-transport] Kestrel dual-endpoint + безопасность каналов
Файл: Program.cs службы (новый `iCemDataMonitor.Host` из B.1).
- D.2.1 ConfigureKestrel: `ListenNamedPipe("iCemDataMonitor.Host")` +
  `ListenAnyIp(8090)` (HttpProtocols.Http1AndHttp2 — gRPC-Web идёт по
  HTTP/1.1). Порт 8090 = СУЩЕСТВУЮЩИЙ web endpoint решения (директива
  владельца — ¬новый порт): Kestrel расширяется gRPC-Web middleware + статикой
  SPA, legacy /api/status* на нём же заморожены; Modbus :502 нетронут.
  `UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true })`;
  `MapGrpcService<TelemetryService>()`; статика SPA: UseDefaultFiles +
  UseStaticFiles + MapFallbackToFile("index.html") на том же :8090.
- D.2.2 PipeSecurity: явный ACL на named pipe endpoint — service account
  службы + интерактивный пользователь (WellKnownSidType InteractiveSid);
  дефолт НЕ оставлять. Тест: процесс вне ACL → connect denied (интеграционный,
  пометить [Trait("Category","Host")]).
- D.2.3 gRPC-интерсептор токена: metadata "authorization"/"x-icemdatamonitor-token" на
  КАЖДЫЙ вызов TCP-канала → UNAUTHENTICATED при отсутствии/несовпадении;
  pipe-вызовы освобождены (ACL = авторизация) — различение канала по
  HttpContext.Connection.LocalPort/transport feature; интерсептор логирует
  отказ (¬молчание). Токен: файл рядом со службой (ProgramData, ACL
  SYSTEM+service account RW, Users — нет), читается один раз при старте.
- D.2.4 Reconnect-политика клиентов (shared helper в Transport или Contracts):
  exponential backoff 3s→6s→12s→24s, cap 60s, ограниченный jitter (±10%,
  ¬jitter-шторм); FSM: Connected→Lost(индикатор)→Reconnecting→Connected.
- D.2.5 ЗАПРЕТЫ этапа (страж в ArchTests): ¬MapControllers/¬MapGet данных в
  Host (кроме legacy /api/status* freeze-списка), ¬SignalR-пакеты, ¬CORS-
  middleware, ¬второй .proto (grep-страж: один telemetry.proto в солюшене).
- Тесты: Transport.Tests — interceptor обе ветки (TCP unauthenticated / pipe
  exempt), backoff-таблица, gRPC-Web roundtrip через TestServer.
- DoD green.

### D.3 [insite-core] ~~Служба~~ → Хостинг как процесс пользователя (W18: служба ОТКЛОНЕНА — UAC)
- ~~D.3.1 UseWindowsService / SCM recovery / EventLog~~ — ВЫЧЕРКНУТО решением
  владельца 2026-08-24: на ПК инженера UAC, установка ПО требует одобрения
  администратора; служба не подходит. ЗАМЕНА:
  - `iCemDataMonitor.Host.exe` — ТОЛЬКО консольный режим (процесс пользователя,
    рабочий режим навсегда, закон отката п.5 сохраняется);
  - автозапуск БЕЗ админа: HKCU Run-key (`HKCU\...\Run` → host.exe) или
    ярлык в Startup — tools\install-autostart.ps1 + uninstall pair
    (замена install-service.ps1; НИКАКИХ sc/firewall/UAC в базовом сценарии);
  - надзор за хостом = tray-агент (B.2.4/D.5.4): мониторинг процесса хоста 1Гц,
    рестарт с anti-bounce 3s→6s→12s→24s cap 60s, N подряд → баннер оператору
    (замена SCM recovery);
  - crash → snapshot.json (FSM_C канон) остаётся.
- D.3.2 SPIKE ВЫПОЛНЕН 2026-08-24: GREEN — SYSTEM/session 0 видит bus
  (heartbeat растёт). После отказа от службы результат ИНФОРМАЦИОННЫЙ (задел на
  будущее); ACL-рекомендация спайка (explicit ACL при CreateNew в AddIn)
  переезжает в D.6 как hardening, не блокер.
- D.3.3 Session 0 гигиена — снимается (хост живёт в сессии пользователя),
  grep-страж на System.Windows в Host остаётся (headless-закон).
- D.3.4 Удалённый доступ — РЕШЕНО владельцем 2026-08-24: «удалённый браузер =
  локальный браузер — программа не должна знать, кто к ней подключился.
  Функционал нужен. Включается при настройке в ini-файле». Следствия:
  - НОВЫЙ ключ station.ini (additive SchemaΔ): web_remote_on (0/1, default 0);
    0 → Kestrel слушает localhost-only, 1 → ListenAnyIp :8090;
  - ЗАКОН НЕДИСКРИМИНАЦИИ: ни один кодовый путь не различает источник вызова
    (¬IP-loopback guard'ов на control-plane, ¬разного поведения для remote);
    авторизация TCP = ТОЛЬКО общий токен (shared secret, не идентификация);
  - Windows firewall всё равно режет inbound ⇒ одноразовое admin-одобрение
    правила — OPTIONAL-шаг runbook'а (install-autostart.ps1 -EnableRemoteAccess);
  - без правила функционал деградирует до localhost (pipe + локальный браузер),
    это штатный режим, ¬ошибка.

### D.4 [insite-data] Источник метрик + backpressure (критерий «сенсор в момент
отправки»)
- D.4.1 MetricsSampler: читает StationHealthState/BusReader НЕПОСРЕДСТВЕННО в
  итерации отправки (¬кэш, ¬предзаполненная очередь); interval_ms из запроса
  (min 200ms — защита от шторма, clamp + флаг в ответе).
- D.4.2 Per-subscriber Channel<MetricsSample> capacity=1,
  BoundedChannelFullMode.DropOldest — отстающий клиент теряет сэмплы,
  очередь НЕ накапливается; счётчик dropped в HostStatus (наблюдаемость).
- D.4.3 ProgramData-миграция (из старого плана, сохраняется): station.ini +
  логи → C:\ProgramData\Halliburton\iCem\DataMonitoring\; IniConfig search
  order [ProgramData, next-to-exe dev-fallback]; ACL BUILTIN\Users:Modify
  (прецедент INC-02).
- Тесты: Data.Tests — sampler freshness (значение меняется между сэмплами),
  drop-oldest семантика, search-order таблица. DoD green.

### D.5 [insite-ui] Клиенты: WinUI (pipe) + SPA (gRPC-Web)
- D.5.1 WinUI-клиент: GrpcChannel через SocketsHttpHandler.ConnectCallback →
  `NamedPipeClientStream(".", "iCemDataMonitor.Host", PipeDirection.InOut,
  PipeOptions.Asynchronous | PipeOptions.WriteThrough)`; KeepAlivePing
  ОТКЛЮЧЁН (named pipe — требование); подписка SubscribeMetrics заменяет
  HTTP-polling из B.2.2; индикатор «связь потеряна» явный (устаревшие данные
  НЕ маскировать — визуальный stale-overlay + возраст последнего сэмпла).
- D.5.2 SPA: TypeScript + @improbable-eng/grpc-web, binary-режим, base URL =
  window.location.origin (same-origin), токен в metadata каждого вызова
  (чтение токена — из install-time конфигурации страницы/диалога; модель
  доставки токена оператору = часть install-runbook D.3.4); генерация из
  того же .proto (D.1.2); тот же индикатор потери связи + reconnect по D.2.4.
- D.5.3 Legacy wwwroot-дашборд (mock JSON) → decommission; новый SPA входит в
  статику службы. Shell-страница «Панель» = тот же gRPC-клиент (¬WebView2
  к localhost:8090 как единственный путь — WebView2 может остаться как
  вложение SPA, решение при реализации, ¬дубль логики).
- D.5.4 Трей-агент: автозапуск (Run-key), статус службы в меню
  (QueryServiceStatus), UAC-flow для start/stop службы; shell-баннер
  «служба остановлена» + кнопка запуска; снос in-proc hosting из WinUI
  (флаг --host из B.2.1 удаляется — GAP-2 закрыт навсегда).
- Тесты: UI.Tests — reconnect-FSM viewmodel, stale-индикатор логика (fake
  stream). DoD green.

### D.6 [insite-plugin] AddIn co-existence (guard)
- D.6.1 AddIn ¬служба (живёт в iCem.exe, пользовательская сессия): producer
  session>0, consumer session 0 — спека co-existence + ACL из spike D.3.2;
  live-gate расширение: heartbeat читается службой.
- D.6.2 Реестр именованных объектов в specs\dotnet-server\Э5: bus MMF/mutex,
  engine pipe `iCemEmule.Control`, НОВЫЙ pipe `iCemDataMonitor.Host` — разные
  имена, пересечений нет. Литерал pipe-имени определён ОДИН раз (const в
  Contracts, D.1) — server и WinUI-клиент берут его оттуда; grep-страж:
  строковый литерал pipe-имени вне Contracts = fail (¬дублирование литерала).

### ЭТАП D — Гейт выхода (W18-редакция: пользовательский хост, ¬служба; критерии приёмки владельца):
1. Host.exe стартует как процесс пользователя (консоль/скрытый); netstat
   показывает :8090; локальный браузер открывает http://localhost:8090 и видит
   SPA. При web_remote_on=1 + одноразовом firewall-правиле удалённый ПК видит
   ТОТ ЖЕ SPA БЕЗ различий (закон недискриминации, D.3.4).
2. WinUI (pipe), локальный браузер и (если включено) удалённый браузер получают
   ОДИН стрим метрик ОДНОГОВРЕМЕННО.
3. Вызов без токена на TCP → UNAUTHENTICATED; pipe доступен только процессам
   пользователя (ACL/hardening D.6).
4. Kill host-процесса → tray-агент рестартует с anti-bounce ≤60с; клиенты
   восстанавливают стрим, UI показывает индикатор потери связи (stale-данные
   помечены, ¬замаскированы).
5. Один .proto → три codegen'а (server/WinUI/web) воспроизводимо через
   tools\gen-proto.ps1. ✅ ВЫПОЛНЕНО 2026-08-24 (web codegen через npm
   protoc-gen-grpc-web + protoc-gen-js, см. STATE §18).
6. Logon машины → автозапуск по Run-key БЕЗ UAC-промптов: tray-агент + хост +
   (emule_on=1) движок; Modbus :502 отвечает; выход из трея гасит цепочку
   корректно. (Замена старого гейта «reboot без логина».)
7. iCem.exe (AddIn) + хост co-exist в одной сессии: heartbeat в GetStatus
   тикает. (Спайк session 0 GREEN — задел, не блокер.)
8. sln 0 err; тесты green; live-gate green; STATE.md ← W18/W19; README —
   runbook autostart/uninstall + опциональный firewall + токен.

---

## Сводная матрица SWARM-нагрузки

| Агент | A | B | D (W18: BD-гибрид, ¬служба) |
|---|---|---|---|
| insite-core | A.2 control surface, A.4 engine fail-safe | B.1 хост + supervision (ЯДРО) | Host.exe (пользовательский процесс) + autostart/tray-watchdog (замена службы) |
| insite-ui | A.1 close-to-tray, A.3 splash/трей | B.2 UI→клиент (ЯДРО) | D.5 WinUI pipe-клиент + SPA gRPC-Web |
| insite-sdk | A.2.0 контракты control | B.0 контракты host API (блокер) | ✅ D.1 .proto единый источник — ВЫПОЛНЕНО 2026-08-24 |
| insite-transport | A.5 pipe-счётчики | B.3 persistent pipe | D.2 dual-endpoint Kestrel + токен-интерсептор (ACL = hardening) |
| insite-data | — | B.5 BusReader guard | D.4 sampler freshness + drop-oldest + ProgramData (частично ✅) |
| insite-plugin | — | B.4 AddIn guard | D.6 co-existence + explicit ACL hardening |

## Кросс-этапные законы (для всех агентов, все этапы)
1. Contract-first: любой новый wire/control формат = record + contract-test ДО
   логики (§15.7). Pipe-строки как контракт ЗАПРЕЩЕНЫ (архетип stringly_protocol).
2. Core не маршрутизирует payload; хост в B/D — транспорт+надзор, ¬domain.
3. Запрещено: strategy-классы в Core вне whitelist; молчаливые fallback'и;
   kill чужих процессов без явного выбора оператора.
4. Каждый этап завершается: build 0 err + тесты green + STATE.md + гейт-сценарии.
5. Откат: A — git-less, точечные правки (backup файлов перед правкой);
   B — флаг --host сохраняет монолит до D; D — служба опциональна, консольный
   host.exe остаётся рабочим режимом навсегда.

---

## ЭТАП E — «Хост в трее, UI = окно, контур без токена» (W19, ПЛАН — ждёт approve владельца)

Источник: решения владельца 2026-08-24 (вечер). Хост уже процесс пользователя
(служба отменена) ⇒ трей у хоста ВОЗМОЖЕН без session-0 ограничений.

### E.1 [insite-transport] Отмена токена web (закрытый контур)
- E.1.1 Удалить из pipeline хоста HostTokenInterceptor (все вызовы gRPC-Web
  анонимны). Классы HostTokenInterceptor/HostTokenStore УДАЛИТЬ; host.token
  НЕ создавать (существующий файл на стенде оставить/удалить — безразличен,
  в runbook пометить obsolete).
- E.1.2 SPA: токен-диалог и metadata во всех вызовах УДАЛИТЬ; UNAUTHENTICATED-
  ветки убрать; localStorage-ключ вычистить при загрузке.
- E.1.3 Тесты: GrpcChannelIntegrationTests bearer/x-header ветки → переписать
  «анонимный вызов → status=0»; HostTokenTests удалить; D2GuardTests не трогать.
- E.1.4 Документы: README-runbook пункт про host.token → ЗАЧЁРКНУТ (контур);
  PLAN D.0/D.2.3 пометить отменёнными решением W19. Закон недискриминации
  остаётся (¬IP-guard'ов).
- DoD: build 0 err; Host.Tests/Transport.Tests green; live: анонимный GetStatus
  по TCP → status=0.

### E.2 [insite-core] Трей хоста (Host.exe = tray application)
- E.2.0 ПРИНЦИП (уточнение владельца): хост использует УЖЕ ПОДГОТОВЛЕННЫЙ
  для него UI — НИКАКОГО нового UI в хосте не создаётся. Вся визуальная
  часть = существующий WinUI-шелл `iCemDataMonitor.WinUI.exe` (Панель/
  Состояние/Эмулятор); иконка трея хоста = существующие ассеты приложения
  (iCemPOC.ico / зелёный ECG — Station.Tools.IconGen), ¬новый дизайн.
  Хост добавляет ТОЛЬКО NotifyIcon с меню управления (¬окна, ¬дашборд,
  ¬WebView).
- E.2.1 Host.csproj: UseWindowsForms=true (FrameworkReference WindowsDesktop)
  ТОЛЬКО ради NotifyIcon. ПОПРАВКА СТРАЖА: HeadlessHygieneTests разрешает
  System.Windows.Forms (трей), продолжает запрещать WPF/WinUI
  (Microsoft.UI.Xaml, System.Xaml, MessageBox в headless-пути).
- E.2.2 HostTray (новый класс в Host): NotifyIcon с ПОЛНЫМ меню —
  «Перезапуск» и «Закрыть» = ДОБАВЛЯЕМЫЕ команды, НЕ единственные:
  · [Открыть панель] — spawn УЖЕ ПОДГОТОВЛЕННОГО UI: iCemDataMonitor.WinUI.exe
    (резолвер sibling → dev-walk-up, тот же что EnsureHost); если UI уже
    запущен (mutex шелла) — сигналить ShowWindow-event (существующий механизм
    второго инстанса). Хост НЕ содержит собственного UI кроме этого меню;
  · [Показать консоль] / [Скрыть консоль] — диагностика: показать/спрятать
    собственное консольное окно хоста (лог в реальном времени);
  · [Перезапуск] (ДОБАВЛЕНО) — graceful self-restart: spawn detached-копию
    себя с теми же аргументами (--ini/--port) НОВЫМ процессом ДО выхода
    текущего (mutex освободится после Exit ⇒ новый инстанс подхватит), затем
    graceful StopApplication; ¬kill себя;
  · [Закрыть] (ДОБАВЛЕНО) — полный graceful shutdown всех слоёв + движка +
    exit.
  Меню расширяемое (additive): новые пункты не ломают существующие.
- E.2.7 ЗАКОН (уточнение владельца): «хост закрывается ТОЛЬКО по команде из
  трея». Следствия: RPC ShutdownHost УДАЛЯЕТСЯ из telemetry.proto (сообщения
  ShutdownRequest/ShutdownAck → tombstone-комментарий, номера НЕ
  переиспользовать); TelemetryServiceImpl теряет ShutdownHost; клиенты
  (GrpcHostClient, SPA) теряют метод; тестовые экземпляры хоста гасятся
  Kill СВОЕГО child-процесса (свой child ≠ чужой процесс, закон п.3 не
  нарушает). Единственный graceful путь остановки хоста = [Закрыть] в трее
  (+ self-restart через [Перезапуск]).
- E.2.3 Tooltip трея хоста — динамический: «iCemDataMonitor Host — bus:open
  HB {N}s · web:{port} · engine:{pid|off}» (1Гц из StationHealthState).
- E.2.4 Режимы запуска хоста: default = скрытая консоль + трей (трей ЕСТЬ
  ВСЕГДА — это теперь главный интерфейс хоста); --no-tray не вводить (YAGNI).
  Консольное окно: скрыто по умолчанию при спавне из трея/UI (CreateNoWindow),
  видимо при ручном запуске exe (диагностика) — как у iCemEmule (W11.9).
- E.2.5 ЖИЗНЬ БЕЗ UI: закрытие шелла (любое: крестик/выход из трея шелла)
  НЕ трогает хост. ИЗМЕНИТЬ Shell: выход из трея шелла сейчас делает
  ShutdownHost RPC — УДАЛИТЬ этот RPC из пути выхода (хост живёт); остаётся
  только собственный выход шелла. [Остановить хост] в трее ШЕЛЛА убрать
  (эта команда переезжает в трей хоста как [Закрыть]) — единая точка
  управления жизнью хоста.
- E.2.6 Два трея (хост + шелл) — ОК по построению (разные роли), tooltips
  различимы («... Host» vs «iCem Data Monitor»). В документации описать.
- Тесты: Core.Tests/Host.Tests — меню-действия как команды (decision table:
  restart = spawn-new→exit-self; close = graceful-all), tooltip-форматтер,
  hygiene-страж новая матрица. DoD green.

### E.3 [insite-ui] Единственность сервера/эмулятора + чистый выход UI
- E.3.1 Подтвердить тестами уже реализованное: ensure-host НЕ создаёт копию
  (mutex✓+pipe✓ → Running/connect); второй Host.exe → exit 1; второй движок —
  reuse (EmuleSupervisor.FindRunning). Гейт-сценарий в UI.Tests.
- E.3.2 Взаимное исключение Host ↔ legacy Tray backend: при старте Host.exe
  проверять Global\iCemDataMonitorTray_v1 mutex — занят ⇒ fail-fast с
  сообщением «legacy Tray backend запущен — закройте его или используйте
  Tray вместо Host» (структурный запрет двойного reader bus / :502).
  Симметрично: legacy Tray НЕ трогаем (откатной путь), но документируем
  несовместимость.
- E.3.3 Выход шелла: убрать ShutdownHost RPC из onExit (E.2.5); крестик окна
  = close-to-tray (как сейчас), «Выход» из трея шелла = завершение ТОЛЬКО
  шелла. UI.Tests обновить.
- DoD: green; ручной сценарий: старт UI → хост поднялся; закрыл UI полностью →
  хост и движок живут; повторный запуск UI → подключился к существующему.

### ЭТАП E — Гейт выхода
1. Анонимный gRPC-Web GetStatus по TCP → status=0 (токена нет нигде).
2. Хост в трее: иконка видна, tooltip живой; [Открыть панель] поднимает UI;
   [Перезапуск] перезапускает хост (pipe кратко недоступен → клиенты
   восстанавливаются по backoff ≤60с); [Закрыть] гасит всё graceful.
3. Полное закрытие UI → хост + движок работают; повторный запуск UI →
   подключение без копии сервера.
4. Запуск Host.exe при живом legacy Tray → честный отказ (не молчание).
5. sln 0 err; все наборы green; live-gate; STATE ← W19; README/runbook
   актуализированы (токен зачёркнут, трей хоста описан).

### Матрица SWARM Этапа E
| Агент | Задачи |
|---|---|
| insite-transport | E.1 отмена токена (+SPA-сторона по координации с ui) |
| insite-core | E.2 трей хоста + self-restart + исключение с legacy Tray |
| insite-ui | E.2.5/E.3 выход UI без остановки хоста + тесты единственности |
| insite-sdk | — (proto не меняется) |
| insite-data | — |
| insite-plugin | — |
