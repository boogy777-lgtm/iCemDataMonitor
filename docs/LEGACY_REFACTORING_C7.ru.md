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
