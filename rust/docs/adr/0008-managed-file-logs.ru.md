# ADR 0008: управляемые файловые журналы

Class: журналы Rust Host, Shell и независимого emulator (`icem-logging`,
`apps/host`, desktop Bridge). AddIn TapLog, Data.dat, настройки оборудования,
templates и произвольные *.log вне класса.
Inv: один файловый владелец, сериализация append/set/clear, bounded память/диск,
подтверждённая настройка и fail-closed граница каталогов; Windows filesystem,
pipe/fault, model и injected disk-full проверки.
N+1: новый владелец добавляет данные в `Owner`; writer/rotation/persistence/clear
переиспользуются. UI использует общий `logs::controls` и explicit mapping extension.

## Решение и ownership

Общий safe crate `icem-logging` не содержит global logger, detached tasks или
нового supervisor. Host держит один `Logs` на прежнем serialized service loop;
Shell и emulator — один `SharedLogs` с mutex на существующих worker/control paths.
GPUI делает только bounded try_send в прежний Bridge (request/reply queues по 16).
Файловые операции не исполняются в UI callbacks. Инициализация Shell lazy на worker;
Host/emulator получают logger после своего существующего singleton, CLI перед ним.
Host не запускает и не останавливает emulator ради логов. Каталоги не общие файлы.

`rust-logger.lock` открывается без sharing на всё время жизни владельца. Второй
Rust writer того же каталога не получает available=true. Это дополнительная защита
файлового ownership, не второй singleton процесса и не launch lock. Legacy не знает
этого lock: взаимная эксплуатация legacy/Rust на одних production singleton/каталогах
не поддерживается; MSI уже разделяет семейства и блокирует такой cutover.

## Local control v1

Нельзя менять frozen `telemetry.proto`, активные REST/SPA или emulator pipe grammar.
В существующий **local Host named pipe** добавлен HTTP/2 endpoint
`/internal/desktop/v1/logs`: GET truth, PUT `{"enabled":bool}`, DELETE clear.
Нового listener/pipe/порта и второго protobuf нет. Этот router подключает только
`LocalRpcServer`; TCP `rest::router` его не монтирует (regression test).
Первая версия намеренно управляет **только Host**; Shell и emulator используют тот
же `Logs::request` локально. Удалённого path/app/pattern параметра нет.

Body <=1024 bytes; ответ <=16 KiB на клиенте; Host request queue=16; обработка
<=16 запросов за iteration. GET/PUT/DELETE возвращают `{ok,data}`; `data` содержит
nullable enabled, available, recording, directory, error, operation, written.
Ошибка файловой операции возвращает ok=false и действительные facts, включая
частичный clear. Transport timeout/disconnect — неизвестный исход мутации.
Автоматического повтора нет, даже для идемпотентного PUT. Очередной GET сверяет truth.
Отменённый queued request не исполняется; уже начатую filesystem operation нельзя
отменить по исчезновению клиента. Host wait=5 s, client entire request=6 s;
shutdown прекращает ожидание. HTTP/2 connection task abort/drop освобождает старый
pipe после каждого вызова; reconnect не сохраняет старый logger snapshot как fresh.

Версия в URI относится к этому internal JSON-контракту. Несовместимая семантика
потребует нового URI; frozen legacy v1 остаётся прежним, включая SetLayer toggle.
Не добавлены auth/ACL обещания: действуют existing pipe ACL/remote-client rejection;
проверка разных Windows sessions/token остаётся отдельным gate.

## Настройка и пользовательские факты

`Logs/<AppName>/rust-logging-v1.json`, UTF-8 `{"version":1,"enabled":bool}`,
<=128 bytes. Отсутствует → ON, как legacy. Неверный/недоступный файл → enabled=null,
видимая ошибка, записи запрещены; чтение его не исправляет. Явный set может сохранить
выбранное значение. Station INI и read-once схема не изменяются.
Готовый и sync_all временный файл публикуется handle-based rename с replacement.
Ошибка открытия/записи/публикации не подтверждает новое значение; прежнее значение
сохраняется в памяти. Ошибка атомарной публикации не заменяет старую настройку.
Не обещается durability при неисправном storage/power loss за гарантиями Windows.

enabled — настройка; recording — реально открытый, не faulted writer.
Смена в UI только после ответа владельца. Во время запроса control disabled;
после разрыва связи last-known виден с пометкой, control disabled до свежего чтения.
Ошибка записи (в том числе disk full) останавливает file writer и сохраняет enabled;
явный set или clear выполняет recovery probe, без фонового retry мутации.
Ошибка logger не логируется в logger. Файловая диагностика не ломает работу приложения.

## Файлы, ротация, очистка

| Owner | Каталог внутри `%ProgramData%/Halliburton/iCem/DataMonitoring/Logs` | Allowlist |
|---|---|---|
| Host | iCemDataMonitor.Host | startup.log, legacy data-bus.log |
| Shell | iCemDataMonitor.Shell | shell-crash.log |
| Emulator | iCemEmulator | crash.log |

Host startup/lifecycle пишет startup.log, bus transitions — data-bus.log; остальные
владельцы пишут свой единственный журнал. Dedup шины остаётся на `previous` error.
Owner держит максимум один writer handle: при смене declared файла предыдущий закрывается.
Startup/config/fatal возвращаемые ошибки Host и startup/settings diagnostics emulator
подключены; Shell backend errors идут через LoggedPort. Полная .NET unhandled
exception/panic equivalence **не заявляется**. CLI stdout и hardware evidence неизменны.

Формат: UTC ISO timestamp, пробел, UTF-8 message, CRLF. Встроенные CR/LF заменяются
пробелом; запись <=8192 bytes с явным truncated marker. На каждом append проверяется
25 MiB cap без legacy overshoot до 60 строк. При превышении newest half <=12.5 MiB
готовится во временном файле; отрезанная первая строка отбрасывается, UTF-8/строки
не рвутся. Handle rename заменяет текущий журнал после sync, затем он переоткрывается.
Максимум дополнительной памяти 12.5 MiB + 8 KiB, временного диска 12.5 MiB
(для JSON <=128 bytes). Каждый declared журнал занимает <=25 MiB: Host <=50 MiB плюс временные 12.5 MiB,
Shell/emulator <=25 MiB плюс временные 12.5 MiB. Чужие/ранее созданные файлы в budget не включены
и автоматически не удаляются. Для emulator старый owned crash.log >7 суток очищается
перед первым append; глобальный sweep по *.log не переносится.

Append failure откатывает длину к предыдущей полной записи. Если и rollback не
удался — отдельная recovery-required ошибка, writer остановлен. Следующее явное
восстановление срезает incomplete trailing record; без record boundary в последних
8 KiB отказ, без неконтролируемого чтения старого файла.

Clear сначала освобождает writer, затем **один раз** обрабатывает каждый exact
allowlist файл и rust-logging.tmp; нет glob, recursion или remove_dir_all.
Настройка, lock, соседние файлы/подкаталоги, AddIn, templates и Data.dat сохраняются.
Каждый отказ учитывается; удаление остальных продолжается. При ON переоткрывается
пустой последний активный owned журнал; при OFF он не создаётся. После частичного отказа доступный writer
продолжает запись по прежнему enabled, а ошибка clear остаётся видимой.
Повторный clear — новая явная операция, не автоматический retry.

## Новая узкая Win32 граница

`icem-windows-native::LogDirectory` — отдельное дополнение ADR 0001. Прикладной
forbid unsafe и native deny/undocumented-unsafe policy не ослаблены.
OpenOptionsExt открывает каждую директорию от local disk root с
OPEN_REPARSE_POINT/BACKUP_SEMANTICS и без write/delete sharing; handles живут до Drop.
Reparse/не-directory ancestor отклоняется; path<=1000 UTF-16 units/32 components.
UNC/device/relative/parent paths не поддерживаются. Только ASCII single-component
filenames, без ADS, reserved devices и traversal. Новый root из control не принимается.

Файлы открыты no-follow; GetFileInformationByHandle отклоняет reparse/directory и
link count!=1 **до записи/удаления**. Открытый handle исключает competing write/delete.
Удаление выполняет FileDispositionInfo на проверенном handle, а не повторном path.
Публикация — FileRenameInfo на source handle в pinned directory. repr(C) buffer,
compile-time FileName offset check и ограниченная UTF-16 длина проверяют ABI.
Цель заменяется как directory entry, а не открывается для изменения её содержимого.
Не выдаётся ACL/elevation; обычные Windows отказы возвращаются вызывающему.
Это защита от reparse/path races обычного пользователя, не от администратора/kernel.

Синхронный filesystem/driver call нельзя прервать Rust deadline. UI остаётся
незаблокированным, RPC ожидание конечно; join existing Bridge при зависшем storage
не имеет доказанного wall-clock deadline. Faulty storage/OS hang — отдельный gate,
не повод заявлять guaranteed cancellation.

Первичный источник handle rename: Microsoft FILE_RENAME_INFO / SetFileInformationByHandle:
https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info
