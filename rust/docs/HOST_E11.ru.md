# Host E11: первый запуск, ensure и CLI

База: main `369bf67f5559003e773ddd4ca17ae4018372f0bc`, PR #4 слит.
Оба post-merge workflow 39122052529 (Rust) и 39122052490 (legacy) завершились SUCCESS.
Legacy CI и исходные .NET/proto/SPA/AddIn не изменяются этим инкрементом.

Class: локальные источники команд Host (`icem-web::host_client`, `apps/{host,shell}`);
эмулятор вне класса. Inv: общий ensure, один существующий Host mutex до config I/O,
конечное ожидание, не более одного spawn, без retry мутаций; policy/process/reconnect tests.
N+1: новый клиент использует тот же ensure/generated RPC и lifecycle events.

Class: загрузка station INI (`icem-config::{ini,station}`), XML вне класса.
Inv: строгая схема, read-once active path, bounded read, публикация без overwrite,
повреждение не даёт defaults; filesystem/concurrency/fault tests.
N+1: схема остаётся в прежнем ini_fields; путь является данными, не новым parser.

## Поведение

1. Shell запускает ensure на существующем Bridge worker. UI thread не ждёт boot.
   GetStatus определяет присутствие; completed ready в boot replay — готовность.
   Отвечающий, но ещё не готовый Host не вызывает новый spawn.
2. Отсутствующий Host запускается из каталога Shell, независимо от cwd, с
   `--autostart`, без нового console window. Stdio отсоединены: фоновый Host не удерживает stdout/stderr
завершившегося CLI. Ошибка старта определяется по exit/pipe; подробный разбор config
Shell показывает чтением, ручной console launch выводит собственную диагностику.
Установленный MSI имеет плоский runtime.
   Custom `--pipe` не получает неявный station launch.
3. Deadline — 30 s, отдельный read probe — до 1 s. Запуск не повторяется. Процесс,
   проигравший существующий Host mutex, выходит с внутренним кодом 73 до config I/O;
   клиент ждёт победителя. Нового mutex/launch lock/process supervisor нет.
4. Shell сохраняет результат попытки вне transport Session. После disconnect он
   освобождает Session и переподключается чтением; нового spawn нет. Если запуск
   ранее не удался, появившийся вручную Host всё равно можно подключить.
5. Ошибка запуска/timeout/конфигурации видна в существующей панели ошибок. Неисправный
   Host автоматически не завершается. После timeout поздний старт возможен: новый
   запуск не следует выполнять автоматически. Синхронные OS create/file calls не
   имеют доказанного wall-clock cancellation; deadlines ограничивают polling/RPC.

## Конфигурация

Обычный запуск без аргументов и `--autostart` используют legacy-порядок:
canonical config.ini → station.ini там же → config.ini рядом с EXE → station.ini
рядом с EXE. Canonical root — `%ProgramData%\Halliburton\iCem\DataMonitoring`.
`--ini PATH` / development `--station-config PATH` задают явный путь без поиска.

Если ни одного файла нет, Host создаёт UTF-8 BOM config.ini из существующей схемы
defaults. Готовый временный файл публикуется hard-link операцией без overwrite.
На NTFS читатель не увидит частичный файл; отсутствие такой filesystem capability
даёт явный отказ создания. Временные файлы очищаются RAII; crash residue не удаляется
как пользовательский файл. Имена содержат PID/time/sequence, без бесконечных retries.

Валидный legacy файл копируется побайтово. Исходный файл остаётся. При неудаче копии
активным остаётся legacy path с диагностикой. Существующий canonical имеет приоритет,
включая повреждённый. Ошибка разбора выводит нарушения; defaults не подставляются,
повреждённый файл не переименовывается/не удаляется/не переписывается. Это сохраняет
legacy fail-fast закон и настройки оборудования. Чтение ограничено 1 MiB — явный
resource delta. Hot reload отсутствует. Layer persistence получает actual active path.
Data.dat не создаётся: единственный writer по-прежнему AddIn.

Shell без `--web-url` один раз выбирает web_port после ensure. Host-owned Open Panel
передаёт фактический URL явно. Launcher Start-Shell больше не навязывает 8090.

## CLI

Dispatch происходит до singleton/config/tray/listeners. Команды `snapshot`, `get
main|lan|raw`, `header` используют original generated gRPC, не MMF. `--start`
использует общий ensure. stdout — одна JSON строка, diagnostics — stderr;
0=success, 1=RPC/operation error после связи, 2=usage/absence/READY timeout.
Сохраняются header keys, CDAB words, timestamp bit reinterpretation и sanitization
float NaN/Infinity в 0. Timestamp после reinterpretation не sanitizes: как в legacy,
невозможность сериализации такого числа — ошибка команды.

`stop` сигналит существующий HostShutdown один раз; absent — notRunning, exit 0.
`restart` сигналит HostRestart один раз. CLI держит stream прежнего владельца и
ждёт его EOF/error до ожидания successor READY; фиксированный sleep не используется
как доказательство смены владельца. Потерянный ответ не вызывает retry mutation.
Отсутствующий Host стартует тем же ensure. Host снимает tray/IPC/listeners и
освобождает guard до spawn successor. Эмулятор не затрагивается.

## MSI и доступ обычного пользователя

Class: рабочий каталог MSI (`Product.wxs`, `Build-WindowsMsi`, общий Assert-Installed).
Inv: Users имеют наследуемый Modify, ACE других identities сохраняются; проверка после каждого
install/repair/upgrade. N+1: прежняя генерация GUID компонентов, без ACL-скрипта.

Добавлен CreateFolder component с util PermissionEx. Группа определяется
через WIX_ACCOUNT_USERS (SID, не язык ОС). Это соответствует legacy Users Modify
для общего каталога данных/приложения. Права ChangePermissions/TakeOwnership не выдаются.
WiX Util использует существующий pin 5.0.2 (MIT); явный installer-only dependency,
без изменения Rust unsafe boundary. WiX 5.0.2 использует SET_ACCESS для целевой Users ACE: права этой группы приводятся
к Modify; это не побайтовое сохранение старых Users ACE. ACE других identities
сохраняются. Runtime код не меняет DACL и не повышает права.
Проверены pinned UtilCompiler.cs и secureobj.cpp (wixtoolset/wix, tag v5.0.2);
актуальная онлайн-схема имеет атрибуты, которых нет в pin, и не заменяет компилятор.
Исходное совмещение runtime и данных в ProgramData сохраняется.

Repair/opt-out/force/Restart Manager/AddIn provenance и разные Legacy/Rust MSI
сохранены. No config/Data.dat ships in MSI. Start-Host.ps1 не требует существующего
StationConfig; прямой shortcut Shell выполняет ensure без PowerShell.

## Доказательства и оставшиеся gates

Локально проверяются pure config и ensure policy, Rust check и Clippy на этих
модулях. Windows CI обязателен для настоящего process/pipe/singleton, Shell reconnect,
CLI, installer/UI Automation/ACL. Результаты exact head фиксируются в PR.
Локальная неполная connector-копия не используется для baseline или asset parity.

Среди всех 133 workflow runs на проверенной базе нет rust-addin-build. Проверенного
SDK artifact нет. Runner inventory недоступен через используемый GitHub connector;
наличие/состояние icem-sdk runner не подтверждено. Реальный `version: next,
publish: false` НЕ выполнен без подтверждённого SDK пути. Fixtures не публикуются.

P11/P13/P15/P18/P28 остаются PARTIAL. Нужны реальный SDK AddIn load/first run с iCem,
обычный/elevated token, другие sessions, GPUI operator/DPI/visual, Npcap, reboot,
rollback/offline и аппаратная приёмка. Прямой portable first-run без MSI не обещает
настройку ProgramData ACL. Повреждённые пользовательские настройки требуют исправления,
повторный MSI Repair восстанавливает owned payload, а не подменяет эти настройки.
