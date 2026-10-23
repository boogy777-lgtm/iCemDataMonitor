# C7 / L09 — повторное использование ограниченного файлового журнала

## Class / Inv / N+1

Class: управляемые диагностические журналы с явно известным владельцем и путём. Данные Data.dat, конфигурация, шаблоны, триггеры и DEBUG-пробы не входят в этот класс.

Inv: запись best-effort без исключений в вызывающий код; одна временная метка; прежние пути, префиксы, кодировка и hooks фасадов; ограничение 25 MiB с сохранением хвоста 12.5 MiB и проверкой перед каждой 60-й записью; операции над одним файлом согласованы внутри процесса. Очистка и retention затрагивают только явно зарегистрированные файлы. Межпроцессная атомарность не обещается.

N+1: новый журнал задаётся путём и локальной политикой форматирования, а не копией ротации, блокировок и retention.

## Решения до реализации

* Общий BCL-only внутренний core находится в SDK и совместим с C# 7.3 / .NET Framework 4.8. SDK StationFileLog остаётся публичным фасадом; net48 AddIn подключает исходник внутреннего core, а не net10 assembly и не второй публичный SDK-фасад.
* Emulator Core использует существующий SDK. Host передаёт исходное сообщение SDK, чтобы убрать двойную UTC-метку, сохраняя console echo.
* CrashLog и TapLog сохраняют публичные API, тестовые hooks, default-directory try-once и запрет создания override directory. Wildcard-удаление чужих старых *.log исправляется на явное владение.
* Append, truncate, clear и owned retention используют единый координатор пути; Windows identity сравнивается без учёта регистра. Форматирование UI trace сохраняет local time, thread id и CRLF.
* DataBusLog dedup/rearm, test switch и test-host guard сохраняются. AddIn throttles не меняются.
* Dormant LogsCleanup остаётся C10; C8 lifecycle и C9 engine/AddIn seams здесь не изменяются. Нет новых production assemblies/packages, RPC, INI/UI/preferences framework.

## Проверки и границы

Проверяются пределы и хвост байтов, UTF-8, cadence, одна метка времени, явно owned retention/clear, чужие файлы и каталоги, конкурентные операции одного пути и независимость разных файлов. Существующие facade tests адаптируются только при намеренном изменении wildcard ownership. Windows net48 smoke использует точные linked sources и Roslyn текущего SDK с .NET Framework 4.8 reference assemblies; он не заменяет vendor build/loading. Linux не подтверждает Windows-only UI или vendor acceptance.

Исторический manifest и ledger не изменяются. Изменения коммитятся отдельными проверяемыми checkpoint-ами.
