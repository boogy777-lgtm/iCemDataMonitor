# D22d — Unicode-идентичность имён emulator

Исторический отчёт указанного инкремента. Текущее software-состояние:
[N+1 tracker](NPLUS1_TRACKER.ru.md), [implementation status](IMPLEMENTATION_STATUS.ru.md).
Прежние NEXT/PARTIAL описывают тот checkpoint; hardware/UI/operator/release gates
остаются отдельными. [C11](../../docs/LEGACY_REFACTORING_C11.ru.md) удалил dormant
REST/serial wrappers и их эксклюзивные tests; com0com driver сохранён.

Ссылка `ДАВЛЕНИЕ` теперь находит registry variable `Давление`, а команды
выбора профиля, чтения и изменения переменной используют ту же идентичность.
Исходное написание имени сохраняется в ответах и каталоге. Исправлен также
LAN preview: после проверки он получает каноническое имя сигнала из общего
resolver, без повторного ASCII-only поиска.

Class: named identities исходного emulator `OrdinalIgnoreCase`; JSON properties,
фиксированные ASCII command/type tokens и exact LAN override keys вне класса.
Inv: `identity::eq_ignore_case` и закрытый `identity::Key` задают одно равенство;
исчерпывающий BCL oracle проверяет таблицу, domain tests — результаты и команды.
N+1: новый потребитель использует тот же механизм; сырой `String` не подходит
для `BTreeMap<Key, SimEntry>`, пропущенная нормализация ключа не компилируется.

## Механизм

В production нет CLR/ICU dependency и нет вызовов общего Unicode lower/full fold.
Приватная таблица содержит 1470 nonidentity mappings. Представитель класса —
минимальный Unicode scalar, равный исходному по настоящему
`StringComparer.OrdinalIgnoreCase`. ASCII использует отдельный быстрый путь,
проверенный против той же таблицы. Вне ASCII выполняется binary search.

`Generate-OrdinalIdentity.ps1` перечисляет все 1 112 064 допустимых scalars
по возрастанию и вставляет их в Dictionary с настоящим BCL comparer.
Dictionary сам разрешает hash collisions через равенство: совпадение hash
не считается совпадением имён. Генерация ограничена конечным Unicode domain.
Проверяются одиночные символы и их окружение с NUL, ASCII, supplementary
character и combining mark. Ordinal algorithm сравнивает BMP characters или
valid surrogate pairs без expansions/context; UTF-16 width сохраняется.

`identity::Key` имеет приватное поле и единственную checked factory. Он
используется только для внутреннего LAN simulation lookup. COM overrides
сохраняют первый внешний ключ и обновляют его значение при эквивалентном имени.
Lexical ordering .NET и malformed UTF-16 strings не входят в этот контракт.

## Потребители и сохранённые различия

| Исходное доказательство | Rust-потребитель | Поведение |
|---|---|---|
| `TemplateCatalog.FindRegistryVariable` | `simulation::build_template` | name/display reference, первый match |
| `SimulationEngine.SetTemplate`, `FindVariable`, `_overrides` | `simulation::Engine` | profile/device, variable/legacy alias, set/update/clear и frozen projection |
| `AcePayloadProvider.LanSimLookup` | `lan::BaseProvider` | wire/alias заменяют; name/display добавляются только при отсутствии; firmware order прежний |
| `EmuEngineHost.SetProfile`, `FindLanSignals`, COM override snapshots | `runtime::Engine` | команды, ambiguity, overridden flags и reported state |
| `ManualOverridePayloadProvider.CanTransmit` | `LanPipeline::resolve_signal`, `can_transmit` | первый подходящий сигнал и прежняя span validation |
| LAN preview adapter | `apps/emulator/src/lan.rs` | checked resolution возвращает фактическое имя для exact override key |

LAN manual override dictionary остаётся **case-sensitive**, как исходный tuple-key
dictionary. Проверка возможности передачи сравнивает имя без учёта регистра;
command/preview adapter затем сохраняет фактическое имя сигнала. Прямые
`set_override`/`clear_override` не получают скрытой нормализации или validation.
Это проверяется отдельно от Unicode simulation lookup.

Не меняются JSON property grammar, filename discovery, fixed ASCII tokens,
port/adapter equality, full-fold/normalization policy и byte codecs. В частности,
`ß` не равно `SS`, составное `é` не равно `e` + combining acute, Turkish I,
long-s и Kelvin sign не подменяются приблизительным lower/upper преобразованием.

## Baseline и границы

`tools/ordinal-identity-baseline.json` записывает measured .NET 10.0.13,
PowerShell 7.6.7, Ubuntu/ICU 74, table hash и scalar/class counts. Это фиксированный
reference snapshot. Разные версии Windows ICU/NLS могут иметь другие таблицы;
Linux measurement не доказывает их совместимость.

`Generate-OrdinalIdentity.ps1 -Check` и независимый Windows-gated BCL test
сравнивают весь scalar domain с фактическим provider. Mapping mismatch завершает
проверку ошибкой; таблица не перегенерируется автоматически. Метаданные provider
выводятся отдельно, различие OS label само по себе не считается несовместимостью.
До Windows verdict production provider gate остаётся открытым.

Проверки включают кириллицу, accented Latin, supplementary letters, sigma,
Turkish I, long-s, ß, Kelvin и composed/decomposed names. Проверяются registry
references и COM lines, profile/alias selection, set/update/get/clear и flags,
полные 124-byte LAN payloads, firmware precedence, exact override keys и реальный
preview EXE. Итоговые результаты фиксируются после проверки конечного дерева.

Проверено 2026-10-22:

- Rust 1.99.1, locked/offline: полный workspace fmt/check/build/test/Clippy PASS;
  239 passed, 0 failed, 0 ignored в 85 группах. Windows-gated tests сюда не входят.
- `icem-config`, `icem-emulation`, `icem-platform-windows`, `icem-web`:
  MSVC `check --all-targets` PASS, compile-only.
- Generator `-Check`: 1 112 064 scalars, 1 110 594 classes, 1470 mappings и все
  contextual witnesses PASS. Независимый BCL oracle дал ту же полную таблицу.
- Linux PowerShell 7.6.7/.NET 10.0.13: 3 private test bodies, включая полный
  BCL scalar/key comparison, и 8 domain/original-method test bodies PASS.
  Для выполнения Windows-gated bodies использованы временные копии Rust tests
  с удалёнными только `cfg(windows)` и исправленными путями support files.
  Tracked source/cfg и production не менялись ради Linux-проверки.
- Unmodified original SimulationEngine: 13 name pairs и override lifecycle PASS;
  original TemplateCatalog: 26 name/display cases PASS; original LAN loaders:
  15 catalogs с побайтовым сравнением payloads PASS. Fixture state для
  SimulationEngine подаётся reflection, constructor discovery не запускается;
  comparison/lookup/override method bodies не подменяются.
- Baseline 513/513, 15 прежних exact approvals и frozen surfaces PASS.

Локальные результаты не объявляются выполнением Windows runtime/provider,
operator/UI или COM/Npcap/iCem/SDK acceptance.

Исходные .NET/AddIn/proto/SPA, CI, dependencies, pins, lints и unsafe policy
не меняются. Следующий кодовый пробел D22 — source-order last assignment для
case-colliding COM DTO properties; он не объявляется исправленным Unicode lookup.
