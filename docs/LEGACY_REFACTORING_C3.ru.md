# Legacy C3: station UI theme

23 октября 2026. C3/L08 из legacy-плана v1.4. База:
`7f89f2fb02626493ee4426512565bd98c2de1297` (C2, merged PR №31).

## Class / Inv / N+1

Class: station theme в `Core/Config/UiThemePreferenceStore.cs`, адаптер
`Host/Tray/UiPrefsStore.cs`, `HostClient/UiThemeCoordinator.cs` и theme-раздел
`Shell.WinUI/MainWindow.xaml.cs` (префикс проектов — `iCemDataMonitor.`).

Inv: канонический station-файл `ui-theme` рядом с `IniConfig.ProgramDataPath`,
токены `light`/`dark`, default-dark при отсутствии, неизвестном тексте или
ошибке чтения. После commit — свежая проекция и poke
`Local\iCemDataMonitorTray_v1_UiTheme`; после неудачной записи poke отсутствует.

N+1: Host и Shell используют общий store; новый consumer подключает его либо
`IHostApi` через узкий coordinator с read/write/apply delegates. Новых сборок,
зависимостей и общего event bus нет. HostClient не получает ссылку на Core.

## Изменение поведения

Shell применяет `reply.ThemeLight`, включая ответ, отличающийся от запроса.
Смена switch и title bar при reconciliation защищена `_syncingSwitches` и не
записывает значение повторно. Лог сообщает текущую тему, без заявления, что
Host сохранил запрос.

Shared store пишет полный токен в уникальный соседний temp-файл, делает
`Flush(true)`, затем rename с заменой. При ошибке старый target не
усекается, временный файл очищается. Parent создаёт station setup, а не theme
writer. Callback уведомления выполняется вне mutation lock и только после
commit; ошибка poke не отменяет сохранение. Host получает существующий
startup-log sink через DI. Shell-only adoption `mac-theme` сохраняет исходник;
если adoption не удалась, UI применяет station/default, а не legacy intent.
Host не получает новой миграции.

Coordinator сохраняет порядок toggle/refresh/listener read внутри session,
резервируя место в очереди до вызова callback. Ошибка consumer не отравляет
очередь. Attach заменяет session и отменяет старую; завершения старых RPC,
включая уже поставленные в UI context, и результаты после Closed не
применяются. Entry points принадлежат одному UI context. Shell отписывает
старую Link subscription, а dispatcher callbacks проверяют session/Closed.
Listener ставит в UI очередь запрос на чтение, а не заранее прочитанный bool.

Initial attach, `HostLink.Connected` и первая подтверждённая pipe-доступность
либо Down→alive получают Host truth через `GetUiPrefs`; offline intent при
reconnect не переигрывается. Используется существующая pipe-liveness feed.
Lost metrics при закрытом MMF не считается доказательством Down: healthy Host
продолжает получать RPC.

**Намеренная failure-policy delta:** локальная запись разрешена для viewer /
отсутствующего control либо подтверждённого pipe-Down, выбранного до отправки
RPC. Unknown liveness направляет RPC. После попытки RPC (`Unavailable`,
`DeadlineExceeded`, application error и другие exceptions) outcome может быть
неизвестен: второй локальный writer не запускается. Shell логирует
unconfirmed outcome и читает station-проекцию. RPC OK с иным bool принимает
ответ без local retry.

Wire не менялся: `UiPrefs` остаётся bool-only, successful RPC отвечает свежим
чтением, а не ACK записи. Нечитаемый файл по прежнему проецируется в dark;
по одному bool нельзя отличить действительный dark от ошибки чтения. После
неподтверждённого RPC немедленный read-back может предшествовать позднему
commit; последующий poke/refresh уточняет проекцию. Cross-process writes —
last-commit-wins, глобальная транзакция или отсутствие всех гонок не заявляются.

## Portable validation

.NET SDK `10.0.302`, существующие проекты, без новых packages. Команды:

```text
dotnet test iCemDataMonitor.Core.Tests/iCemDataMonitor.Core.Tests.csproj --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false --filter FullyQualifiedName~UiThemePreferenceStoreTests
dotnet test iCemDataMonitor.Host.Tests/iCemDataMonitor.Host.Tests.csproj --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false --filter FullyQualifiedName~UiThemeRpcTests
dotnet test iCemDataMonitor.UI.Tests/iCemDataMonitor.UI.Tests.csproj --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false --filter FullyQualifiedName~UiThemeCoordinatorTests
```

Store: **9/9**, включая missing/malformed/read-back, invalid path, missing
parent, parent-file/target-directory collision, recovery, отсутствие poke при
fault, complete-text-before-poke, poke failure, concurrent complete tokens,
adoption success/failure с сохранением source. Проверка FileShare.None и
ReadOnly имеет Windows guard: на Linux эти ветви не исполнялись; chmod под
root не выдаётся за проверку отказа в доступе.

RPC: **1/1** на отдельном fixture с unopened BusReader, без именованного MMF:
неудачный light commit возвращает current false, не pokes и не пишет ложный
saved-log; исправление fault позволяет повторить запрос успешно. Сборка этого
набора компилирует затронутые Host/Core/HostClient.

Coordinator: **13/13**: ответ отличается от запроса, offline-before-send,
Unavailable/DeadlineExceeded/application error без fallback, initial attach /
refresh, offline save→reconnect без replay, delayed old-session/Closed,
упорядоченные toggles и listener echo, reentrant callback / queue recovery,
initial failure→first alive при closed bus, уже поставленное UI continuation.
Единственный warning компиляции UI.Tests — существующий `xUnit1031` в
`HostClientTests.cs:662`, вне C3.

## Отдельный Windows gate

WinUI build и реальная работа switch/title bar/named event требуют Windows.
Проверить: light/dark ответ с отказом записи, viewer/pipe-Down local save,
healthy Host с closed bus, reconnect без replay offline intent, replacement /
Closed при задержанном RPC и несколько быстрых toggles. Эта portable проверка
не заменяет Windows CI и ручную визуальную приёмку. MMF/proto/SPA/AddIn/Rust
runtime не изменены; source-manifest baseline неизменяем.
