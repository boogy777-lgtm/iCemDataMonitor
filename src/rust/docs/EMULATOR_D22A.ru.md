# D22a — recovery production LAN catalog files

Исторический отчёт указанного инкремента. Текущее software-состояние:
[N+1 tracker](NPLUS1_TRACKER.ru.md), [implementation status](IMPLEMENTATION_STATUS.ru.md).
Прежние NEXT/PARTIAL описывают тот checkpoint; hardware/UI/operator/release gates
остаются отдельными. [C11](../../docs/LEGACY_REFACTORING_C11.ru.md) удалил dormant
REST/serial wrappers и их эксклюзивные tests; com0com driver сохранён.

Источники: pinned `iCemEmule.Core/Core/LantableRegistry.cs` (`LoadFile`,
`LoadFirmwareFile`) и `AcePayloadProvider.cs` (`LoadSimFile`, `LanSimLookup`).
.NET sources, proto, SPA, CI, dependencies и native policy не менялись.

Class: `icem-emulation/src/lan.rs` production `Registry::load`/`BaseProvider::load`
и per-file boundaries; strict `Registry::from_json`, COM catalog, encoding/Unicode
вне класса. Inv: completed prefix сохраняется, остаток ошибочного файла не читается,
следующий firmware source пробуется; inventory/payload fixtures и original methods.
N+1: новый malformed shape добавляется в shared fixture table и точное чтение
соответствующего C# свойства; обе portable/oracle проверки потребляют всю таблицу.

## Поведение

Registry пытается загрузить legacy, затем optional firmware. Read/parse/DTO error
прекращает только текущий файл; полностью завершённые SV остаются. Malformed signal
не добавляет частичный SV. Duplicate SV всё равно полностью разбирается перед
first-wins suppression; malformed duplicate прекращает остаток файла. Syntax error
возникает до обхода DOM и не оставляет prefix этого файла. Existing legacy/firmware
priority не меняется.

Registry сохраняет до двух owned diagnostics (один на source file); missing file
по-прежнему не прекращает startup, но даёт warning как original LogLoad.
Engine направляет diagnostics в existing startup logger/stderr; LAN preview — в
stderr. Нового logger или записи в installation directory нет. Точный legacy log
format/успешные merge-count строки не объявлены перенесёнными этим инкрементом.

BaseProvider аналогично сохраняет завершённые lookup entries и пробует
`firmware_variables.json` после ошибки выбранного variable registry. Эти ошибки
silent, как original LoadSimFile. Entries добавляются только после проверки всей
записи, включая неиспользуемые для payload `uom` и `excludeFromTelemetry`.
Present `simulation`, `families`, `lan` должны быть objects; null/array/scalar дают
ошибку файла, как C# property access. Optional отсутствующие свойства сохраняют
defaults; nullable strings сохраняют null→empty/None. WireName/alias overwrite,
name/display AddIfAbsent и порядок источников остаются прежними.

Strict `Registry::from_json` продолжает возвращать malformed DTO/JSON error.
Selftest T1–T26 mapping, asset checks и assertions не менялись; production loading
внутри них использует новый file behavior. Preview retains its explicit required-file
validation. Codec/offset safety и valid-catalog wire bytes не менялись.

## Проверки и границы

Shared fixture table: valid→malformed→valid, syntax errors, invalid signal после
валидного signal, malformed duplicate, legacy priority, firmware prefix, malformed
uom/exclude/simulation/families/lan, допустимые absent/null strings и lookup overwrite
versus AddIfAbsent. Portable tests проверяют retained inventory, полные124-byte
payloads, переход к firmware, per-file diagnostics и отдельный strict parser.

Windows `lan_dotnet.rs` вызывает существующий `LanPayloadOracle.ps1` с fixtures.
Новый oracle mode вызывает **original private LoadFile** для двух абсолютных fixture
paths и **original LoadSimFile** через existing Base seam. Reconstructed Registry()
helper прежнего valid-catalog oracle в этой проверке не используется. Отдельный
`LoadFirmwareFile` с hardcoded AppDomain path не вызывается: его source loop/catch
сопоставлен с LoadFile; тест сравнивает оригинальный parser для обоих источников.
Original LogLoad выполняется только внутри test oracle. Inventory и все payload
bytes сравниваются с результатом оригинальных методов; все cases должны присутствовать.

Linux: focused portable tests и affected-crate Clippy PASS; MSVC icem-emulation
all-targets check/Clippy PASS (compile-only). Новый C# oracle требует Windows/pwsh;
локально pwsh отсутствует, поэтому execution не объявлен PASS. Exact-head Windows CI
обязателен до merge. Полный Linux workspace fmt/check/build/test/Clippy с
locked/offline PASS на Rust1.99.1 в отдельном чистом target directory. Первый запуск
на shared target остановился с E0463 missing icem_web artifact; чистый target прошёл
без изменений source/deps/CI и без подавления проверок.

Открытый кодовый backlog D22: COM TemplateCatalog malformed-registry all-or-empty
fallback, File.ReadAllText BOM/invalid-byte semantics и Unicode OrdinalIgnoreCase.
Этот инкремент не объединяет stream JSON и text-file encoding policies и не заменяет
ASCII lookup на приблизительный Unicode folding. Отдельно открыты Windows/UI/operator,
COM/Npcap/NIC hardware и настоящий SDK/MSI acceptance; D22 целиком не объявлен закрытым.
