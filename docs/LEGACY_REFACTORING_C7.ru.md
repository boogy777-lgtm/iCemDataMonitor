# C7 / L09 — повторное использование ограниченного файлового журнала

## Class / Inv / N+1

Class: управляемые диагностические журналы с явно известным владельцем и путём. Данные Data.dat, конфигурация, шаблоны, триггеры и DEBUG-пробы не входят в этот класс.

Inv: запись best-effort без исключений в вызывающий код; одна временная метка; прежние пути, префиксы, кодировка и hooks фасадов; ограничение 25 MiB с сохранением хвоста 12.5 MiB и проверкой перед каждой 60-й записью; операции над одним файлом согласованы внутри процесса. Очистка и retention затрагивают только явно зарегистрированные файлы. Межпроцессная атомарность не обещается.

N+1: новый журнал задаётся путём и формой записи общего sink, а не копией ротации, блокировок и retention.

## Решения до реализации

* Общий BCL-only внутренний core находится в SDK и совместим с C# 7.3 / .NET Framework 4.8. SDK StationFileLog остаётся публичным фасадом; net48 AddIn подключает исходник внутреннего core, а не net10 assembly и не второй публичный SDK-фасад.
* Emulator Core использует существующий SDK. Host передаёт исходное сообщение SDK, чтобы убрать двойную UTC-метку, сохраняя console echo.
* CrashLog и TapLog сохраняют публичные API, тестовые hooks, default-directory try-once и запрет создания override directory. Wildcard-удаление чужих старых *.log исправляется на явное владение.
* Append, truncate, clear и owned retention используют единый координатор пути; Windows identity сравнивается без учёта регистра. Форматирование UI trace сохраняет local time, thread id и CRLF.
* DataBusLog dedup/rearm, test switch и test-host guard сохраняются. AddIn throttles не меняются.
* Dormant LogsCleanup остаётся C10; C8 lifecycle и C9 engine/AddIn seams здесь не изменяются. Нет новых production assemblies/packages, RPC, INI/UI/preferences framework.

## Проверки и границы

Проверяются пределы и хвост байтов, UTF-8, cadence, одна метка времени, явно owned retention/clear, чужие файлы и каталоги, конкурентные операции одного пути и независимость разных файлов. Существующие facade tests адаптируются только при намеренном изменении wildcard ownership. Windows net48 smoke использует точные linked sources и Roslyn доступного SDK с .NET Framework 4.8 reference assemblies; он не заменяет vendor build/loading. Linux не подтверждает Windows-only UI или vendor acceptance.

Исторический manifest и ledger не изменяются. Изменения коммитятся отдельными проверяемыми checkpoint-ами.

## Реализованная карта вызовов

| Канал | Путь / локальная policy | Общий механизм |
|---|---|---|
| Host StartupLog | Host ProgramData/startup.log; console echo отдельно | StationFileLog.Write, одна UTC O метка |
| DataBusLog | Host ProgramData/data-bus.log; прежние dedup/Success/Enabled/test-host guard | StationFileLog.Write |
| Shell crash | Shell ProgramData/shell-crash.log | StationFileLog.Write |
| Shell startup | рядом с exe/startup.log; local HH:mm:ss.fff [winui], platform newline | StationFileLog.WriteLocalTime |
| Emulator CrashLog | iCemEmule либо UseAppName, crash.log; default directory try-once, override без mkdir | StationFileLog.Write; retention только текущего owned path |
| TemplateCatalog / LantableRegistry | общий рядом с exe/iCemEmule.templates.log; UTC O, прежний prefix | StationFileLog.WriteUtcWithoutBom, один координатор |
| UiTrace | TEMP/emu_ui_trace.log; local time, tid, CRLF | StationFileLog.WriteLocalThread |
| net48 TapLog | прежний ProgramData/TapOut.log; hooks и directory policy сохранены | Compile Link точного OwnedFileLog.cs; UTC O, owned retention |

`WriteUtcWithoutBom`, `WriteLocalTime` и `WriteLocalThread` выбирают фиксированные формы общего sink, который один захватывает и форматирует время под общим per-file координатором; порядок меток не меняется из-за ожидания блокировки. Callers передают payload/prefix/thread id. Сохраняются local culture, CRLF/platform newline и UTF-8 без BOM исходных каналов. `Write` сохраняет Encoding.UTF8 прежних SDK/Tap/Crash каналов. Host отдельно сохраняет прежнюю метку console echo. Двухаргументная публичная сигнатура StationFileLog.Write сохранена; дополнительная перегрузка задаёт directory/retention policy для фасадов.

`ClearOwned` и `DeleteOwnedOlderThan` принимают явные пути и продолжают работу после ошибки отдельного файла. Новый RPC/UI clear не вводится. Ownership не выводится из расширения или наличия в каталоге. Новые SDK-тесты проверяют также Windows case alias того же пути; net48 helper компилирует те же production sources, а не их модель, и требует фактический CLR 4 в дочернем процессе.

## Локальная проверка

Linux, SDK 10.0.302 / runtime 10.0.10; команды выполняются последовательно с `--no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`.

* SDK.Tests: полный набор, 99 passed, 1 Windows-only net48 test skipped.
* Data.Tests, `FullyQualifiedName~DataBusLogTests`: 6 passed, включая реальный illegal sink path и Success rearm.
* Host.Tests, `FullyQualifiedName~StationFileLogHostContractTests`: 7 passed. Прежний тест собирал Windows policy root из platform separator и не работал на Linux; теперь он точно проверяет `Path.Combine(StationFileLog.LogsRoot, appName)`, сохраняя Windows policy root.
* Emule.Tests, `CrashLogTests|WireFormatTests|PayloadSmokeTests|LanFreezeCouplingTests`: 15 passed; новая SDK reference собирается.

Windows CI должен собрать WinUI callers и выполнить net48 Roslyn golden helper (Linux его не исполняет). Полная vendor AddIn сборка и загрузка в iCem.exe требуют установленного proprietary SDK и остаются отдельной границей приёмки. Межпроцессная clear/append атомарность не проверяется и не обещается.

## Независимый review — уточнение до исправления

Class: фиксированные существующие формы записи (UTC O с/без BOM, local HH:mm:ss.fff, local HH:mm:ss.fff с thread id). Inv: время захватывает и форматирует только общий sink; callers передают payload/prefix/thread id, сохраняя прежнюю культуру, кодировку и newline. N+1: новый канал выбирает существующую форму, не копирует timestamp expression. Review обнаружил, что WriteFormatted оставлял владельца timestamp в callers; этот новый escape API заменяется закрытым набором форм. Проверки: точные векторы формы/байтов и source call-map guard всех мигрированных callers, включая linked net48 TapLog.

Review завершён: существенный gap владения timestamp исправлен; сырое форматирование осталось только внутренним IO-механизмом и test vectors. Полный SDK набор после исправления: 99 passed / 1 Windows-only skipped; DataBus 6, Host logging 7, Emule targeted 15 passed. Source guard покрывает семь мигрированных callers; UTC/local record vectors отдельно проверяют культуру, BOM, prefix и newline. Нулевой payload в UiTrace/TemplateCatalog по-прежнему форматируется как пустая строка. `git diff --check` проходит. Windows execution/helper и vendor acceptance на Linux не заявляются.

## Публикация и provenance

Последовательные агенты GPT-6.1 Sol medium выполнили analysis → implementation → independent review. Review исправило единственного владельца timestamp и сохранило capture/format под координатором файла. [PR36](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/36) публикует точный reviewed tree `4094a923a020ac09ac586651e433d8f0952abaaf`; reviewed code commit `10e5474fff0b9553e73ecb48a5303e26ef9f5807`. Ledger обновляет только изменённые historical paths с canonical Git blob hashes, фактическим последним source commit и сохранением прежних reasons. Historical manifest остаётся на `96e2206562c9389d1543095232115d81eae4b692`.

Полный Windows CI, CLR4 golden helper и WinUI ожидаются до merge. Результат CI и оставшиеся ограничения фиксируются в PR; vendor AddIn loading и межпроцессная atomic clear не заявляются. После C7 автономно продолжаются C8–C10.

Официальный `Verify-Baseline.ps1 -RequireComplete`: **513/513**, **57 exact approved divergences**, frozen proto/assets, D16/D17 predicates и REST inventory OK. `git diff --check` clean.

## Windows CI — Class / Inv / N+1 до исправлений

Class: asynchronous Modbus recovery observations в `Transport.Tests/ModbusContractTests.cs`; retained stale header — near nonmember полного восстановления активной зоны. Inv: header7 не доказывает copy CDAB [0,0x3F80]: `TickOnce` зеркалирует stale header, но удерживает зоны; проверять один TCP window3–5 и прежний 15s deadline без увеличения задержек. N+1: следующие recovery assertions привязываются к полному наблюдаемому результату; deterministic stale-first/fresh-next vector в `AcquisitionMeasurementsTests.cs` проверяет различие без clock/scheduler зависимости. Production synchronization и stale-hold law не меняются.

Class: differential C# oracle source closures в `rust/tests/support/{ConfigComOracle,ComCatalogOracle,EmulatorOracle,UnicodeIdentityOracle}.ps1`; production Rust и frozen source manifest не входят. Inv: helper компилирует фактические текущие TemplateCatalog/LantableRegistry вместе с exact SDK logging sources, без stub sink или изменения original methods; compilation guards каждой из четырёх closures. N+1: oracle linking нового C# source включает его реальные source dependencies. C7 разрешает это узкое обновление тестовой closure; Rust runtime/fixtures/manifest не меняются.

## Исправление Windows CI — результат и проверка

Windows run `40003483967`: legacy matrix 1055 passed / 1 failed; ошибка `TickLoop_OpenFail_RecordsError_LoopSurvives_AndRecovers` получила header7 и [0,0] вместо active CDAB [0,0x3F80]. Причина неверного acknowledgement в тесте: header зеркалируется даже для stale retained snapshot, а зоны удерживаются. `LastBusError` также очищается для retained snapshot. Header и зоны читаются/копируются под `_regLock`, поэтому предположение о чтении между header-copy и zone-copy не подтверждается. C6 publication expiry допускает такой stale-first сценарий при задержке scheduler; CI не содержит snapshot telemetry, и конкретное срабатывание expiry не заявляется измеренным фактом.

Тест теперь ждёт один TCP window3–5, равный [7,0,0x3F80], и отсутствие error. Сохраняются 15s cancellation deadline, 20ms polling delay и прежние точные header/CDAB assertions; production-код не меняется. Portable vector `CachedPublication_StaleFirstHeaderDoesNotProveActiveZoneRecovery` детерминированно доказывает ложное прохождение старого header-only condition: stale-first tick даёт header7 / null error / zone zeros, fresh-next tick — exact [7,0,0x3F80]. Named-MMF production loop на Linux не запускался и требует Windows CI; Linux не доказывает его прохождение.

Rust differential failure — неполная C# source closure после C7: четыре PowerShell Add-Type helpers включали текущие TemplateCatalog/LantableRegistry без новых SDK logger dependencies. Теперь они включают exact `OwnedFileLog.cs` и `StationFileLog.cs`, без logger stub, новых assemblies/packages или изменения original method bodies. Production Rust, frozen fixtures и source manifest не изменялись.

Локально фактически исполнен PowerShell 7.5, а не только проверен синтаксис: прежний ConfigComOracle из `3221d0f` воспроизводит missing logger compilation error; обновлённый выполняет восемь XML load/save cases и COM freeze/unit oracle. ComCatalogOracle загружает два настоящих templates из текущего каталога; EmulatorOracle исполняет simulation/wire/units/DFB/ACE/decode vectors; UnicodeIdentityOracle исполняет три identity/override simulation cases. Четыре helper smoke прошли. Rust toolchain здесь отсутствует; Cargo differential assertions и полный Rust suite не запускались. `git diff --check` проходит.

Transport.Tests после исправления собран на SDK 10.0.302 без warnings/errors; portable `CachedPublication_` — 2 passed, затем `CachedPublication_|TransportArchitectureTests` — 5 passed (2 из них повторены, это не семь уникальных tests). Полный ModbusContractTests / Windows named-MMF integration на Linux не запускались. Команды последовательны: `--no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false`; restore использует общий package cache и `--disable-parallel`.

## LAN oracle closure — Class / Inv / N+1 до исправления

Class: динамический source list `rust/tests/support/LanPayloadOracle.ps1`, включающий текущий emulator LantableRegistry; Dfb-only и отдельный Plugins.Icem LantableRegistry — near nonmembers. Inv: Add-Type включает exact SDK OwnedFileLog/StationFileLog без stub и изменения методов; фактически исполняются Run и Loading на формах существующих `lan_loading.rs` fixtures. N+1: audit всех support ps1 учитывает как literal paths, так и массивы имён и recursive source enumeration; каждый consumer C7 logger получает обе реальные dependencies. Production Rust, fixtures и source manifest не меняются.
