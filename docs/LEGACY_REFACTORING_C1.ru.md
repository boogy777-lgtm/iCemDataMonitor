# Legacy C1: единая конфигурация запуска Host

23 октября 2026. Отдельный инкремент C#/.NET по C1/L01 из
[legacy-плана в PR №29](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/29).
База: `4578ab57266b7d25aed82df41c6276597a270c1e`.

## Проблема и результат

`ProgramMain` загружал INI для startup layers, а `HostAppFactory.Build`
повторно загружал файл для DI, HTTP endpoints и binding. Если файл менялся
между этими операциями, один запуск использовал две конфигурации. Кроме того,
выбор persistence path при сборке зависел от глобального `IniConfig.ActivePath`,
который мог быть изменён другим вызовом загрузки.

Теперь `LoadBootContext` один раз получает options, абсолютный фактический путь
INI и существующий `KestrelBindPlan`. `ProgramMain`, composition и startup layers
используют этот контекст. `Build(context)` не читает INI; совместимый
`Build(spec)` загружает контекст и делегирует ему сборку. CLI port override
выбирает listener, не переписывая сохранённый `IniOptions.WebPort`.

## Class / Inv / N+1

Class: конфигурация одного запуска в `iCemDataMonitor.Host/HostAppFactory.cs`
и `Program.cs`; runtime commands находятся вне этого класса.

Inv: startup, DI и endpoints получают одни options и bind plan; persistence
использует захваченный путь. `HostBootContextTests` проверяет замену INI после
load и отдельную загрузку другого файла перед build.

N+1: новое поле проходит существующую schema/load и своего потребителя;
добавлять второй file-read path не требуется. Существующие compiler checks
и регрессия согласованности startup options проверяют эти связи.

## Проверенная карта запуска

| Путь | Роль |
|---|---|
| `Host/Program.cs`, `ProgramMain` | Аргументы, singleton, загрузка контекста, build, start layers, tray |
| `Host/HostAppFactory.cs` | Однократная загрузка и композиция Kestrel/DI/endpoints |
| `Host.Core/HostComposition.cs` | Регистрация options, runtime controller, reader и протокольных сервисов |
| `Core/Config/IniConfig.cs` | Существующая схема, strict parsing, defaults, discovery/migration |
| `Host.Core/KestrelBinding.cs` | Существующий чистый выбор bind plan |
| `Host.Tests/HostBootContextTests.cs` | Fault между load/build, persistence path, port precedence, defaults |

Пути в таблице сокращены относительно корня репозитория: `Host` означает
`iCemDataMonitor.Host`, аналогично для остальных проектов. Это карта затронутого
startup-сценария, а не доказательство отсутствия внешних потребителей dormant v1.

## Проверка

- SDK из `global.json`: .NET `10.0.302`.
- Сборка `iCemDataMonitor.Host.Tests` Debug: успешно, 0 warnings/errors.
- Portable focused tests: 24/24, включая все 9 новых boot-context регрессий.
- Routing regression suite: 37/37; baseline guard regression suite: 25/25.
- Расширенный focused run: 24 passed, 7 failed из-за
  `PlatformNotSupportedException: Named pipes transport requires a Windows operating system`.
  Это существующие real-start CompositionSmoke/GrpcChannelIntegration tests.
- Windows CI: обязательная незавершённая проверка до принятия инкремента.
  Linux не подтверждает named pipes, Win32, MMF или операторскую приёмку.

Portable filter:

```text
FullyQualifiedName~HostBootContextTests|FullyQualifiedName~HostArgsTests|FullyQualifiedName~KestrelBindingTests|FullyQualifiedName~Build_SetsShortShutdownTimeout_ForFastRestartDrain
```

Для локальной сборки использованы `--no-restore -m:1 /nodeReuse:false
-p:UseSharedCompilation=false`; начальный restore выполнен последовательно.
Изменять зависимости или CI для обхода ограничений Linux не потребовалось.

## Migration baseline и следующие инкременты

Исторический `rust/migration/source-manifest.json` с pin
`96e2206562c9389d1543095232115d81eae4b692` сохраняется. Изменения существующих
исходников учитываются точными hashes и provenance в
`rust/migration/legacy-divergences.json`, отдельно от исторического эталона.
Новые файлы не подменяют старые oracle fixtures.

C1 не меняет MMF layout5, source timestamp из регистров 4–5, COM reserve49–56,
правила независимых LAN/RAW updates, внешние протоколы, AddIn или Rust runtime.

Следующий пакет исходного плана — C2: L02 observed status и L04 сериализация
изменяющих команд, отдельными коммитами. Далее C3 preferences, C4 registry,
C5 immutable snapshot, C6 acquisition, C7 logs, C8 lifecycle, C9 emulator/AddIn
seams и C10 доказанное удаление dormant v1. Эти пакеты в C1 не реализованы.
