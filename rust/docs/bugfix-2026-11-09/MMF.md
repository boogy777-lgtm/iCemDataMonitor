# MMF: диагностика 2026-11-09

## Факты

- Живой `iCem` работал; `C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat` имел 20066 байт. Первые 10 байт: `53 54 4E 42 01 00 05 00 11 27` — STNB, protocol 1, layout 5, N=10001.
- Read-only `MemoryMappedFile.OpenExisting` секции `Global\iCemDataMonitorBus_v1` и чтение 80 байт успешны. Значение heartbeat в отображении изменилось относительно предыдущего чтения файла. Эти два чтения не были единой mutex-транзакцией; это не end-to-end тест Rust Host.
- `Logs\iCemDataMonitor.Host\startup.log` содержит четыре отказа запуска на загрузке `config.ini`: Win32 error 32 (sharing violation). Последний записан в 08:26:53Z. На момент проверки `config.ini` отсутствовал, Host-процесс не наблюдался. Поэтому эти запуски не достигли чтения MMF.
- Причина воспроизведена на отдельном временном каталоге: `LogDirectory::open` удерживает read-only handles всех предков с `FILE_SHARE_READ` без write/delete sharing. `station::publish` создавал и синхронизировал временный файл, затем `CreateHardLinkW` через `fs::hard_link` возвращал **error 32** при таком pin родителя. Инициируемый path-based `MoveFileW` тоже даёт 32. До исправления новый Windows regression `publication_with_read_only_pinned_parent` упал с тем же сообщением `StationPaths::load`, что записано в startup.log. Лог не содержит имя Win32 операции, поэтому точную операцию исторического запуска нельзя наблюдать непосредственно, но конфликт кода и OS воспроизведён детерминированно.
- `rust/crates/icem-platform-windows/src/bus_reader.rs` читает shape с FileShare ReadWrite, открывает существующую секцию read-only, копирует под именованным mutex; `Burservis.iCem.DataMonitor/BusWriter.cs` использует те же имя/геометрию и FileShare ReadWrite. Ошибка самого reader по этим наблюдениям не доказана.

## Изменения и ограничения

`station::publish` на Windows теперь публикует синхронизированный временный файл через handle-based `NtSetInformationFile` с `FileRenameInformation`, без замены существующего файла; существующая проверенная реализация logger использует общий механизм rename с флагом замены. Для доступа к native boundary добавлена только Windows-only внутренняя зависимость `icem-config` → `icem-windows-native`; новых внешних пакетов нет. Защитные directory pins/ACL не ослаблены, повторы не добавлены. Другие платформы сохраняют hard-link публикацию. При collision существующий победитель считывается и проверяется, а не перезаписывается.

Windows Release regression до исправления воспроизвёл error 32; после — `icem-config`: 27/27 PASS, включая creation, migration, concurrent winner, сохранение данных и hard-link error 32 под pin; `icem-windows-native --lib`: 5/5 PASS, включая logger rename; `icem-platform-windows --test bus_reader`: 7/7 PASS с явным PATH для `pwsh`. Исходные запуски bus_reader и полного icem-config имели failures из-за отсутствующего `pwsh` в PATH, не из-за проверки MMF; оба повторены с корректным PATH. Код MMF не менялся: причинную ошибку reader не удалось подтвердить. Продакшен-процессы и данные не изменялись. End-to-end acquisition с реальной AddIn и операторская приёмка остаются открыты.

Focused `cargo clippy --release --locked --offline -p icem-config -p icem-windows-native --all-targets -- -D warnings`: PASS.

## Дополнение: live AccessDenied после исправления конфигурации

Родительская проверка fresh Release Host `c77d4e6` с собственным временным config и запущенным elevated iCem дошла до Web/tray, но показала `Bus state: AccessDenied` (`icem-live-bugfix-6777edb105844d0ba36cfc27962daabd/stderr.log`). Ранее описанная успешная публикация config **не означала**, что live MMF исправлена.

Read-only probe существующего `Global\iCemDataMonitorBusMutex_v1` в текущем medium-контексте: `OpenMutexW(SYNCHRONIZE | MUTEX_MODIFY_STATE)` — успех; `CreateMutexW` — Win32 5; `CreateMutexExW` с теми же минимальными правами — успех. Ранее в том же контексте read-only файл и секция открывались успешно. `CreateMutexW` запрашивает ALL_ACCESS; `WindowsBusReader::open` вызывал его через `NamedMutex::open_or_create`, преобразуя ошибку в sticky `AccessDenied`. Это непосредственно объясняет отказ при данном DACL. Исходный .NET `BusReader.cs:386–387` открывает read-only mapping и именованный Mutex; `BusMapping.cs:60,139` открывает файл с Read и FileShare.ReadWrite.

`windows.rs::NamedMutex` теперь использует `CreateMutexExW` с `SYNCHRONIZE | MUTEX_MODIFY_STATE`, без изменения DACL, имени, владения или timeout. Синтетический mutex с DACL только на эти права: старый `CreateMutexW` получает 5, новый конструктор lock/release успешно; DACL без права release по-прежнему даёт 5. Windows Release `icem-windows-native --test mutex_acl`: 2/2 PASS; `icem-platform-windows --test bus_reader`: 8/8 PASS, включая capture под ограниченным DACL, timeout и abandoned. Это изолированные тесты; повторный end-to-end Host с живым iCem после сборки исправления ещё необходим. Реальные объекты и их ACL не изменялись.

Focused Release clippy `-p icem-windows-native -p icem-platform-windows --all-targets -- -D warnings` — PASS.
