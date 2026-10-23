# D22c — кодировки файлов каталогов Host и emulator

Корректные operator-edited каталоги в UTF-16/32 с BOM теперь читаются теми
путями Rust, для которых исходный .NET использует `File.ReadAllText`.
Вместо отдельного декодера в каждом загрузчике используется прежний механизм
INI, выделенный в `icem-config::text_file`.

Class: текстовые файлы `File.ReadAllText` в config/catalog loaders; XML reader
и потоковый JSON `iCemEmule.Core/LantableRegistry` — ближайшие nonmembers.
Inv: один декодер сохраняет Unicode и результаты каталогов, BOM снимается
один раз на файловой границе; file fixtures проверяют COM lines, LAN bytes,
Host names/definitions и существующие ограничения размера.
N+1: следующий текстовый каталог вызывает тот же декодер; новые кодировки
добавляются в общую таблицу fixtures, а не в loaders или domain core.

## Файловые границы

| Потребитель Rust | Исходное доказательство | Декодирование |
|---|---|---|
| `simulation::load_catalog`, `load_template`: COM profile, main/override registry | `TemplateCatalog.cs`: `Load`, `LoadRegistryVariables` | UTF-8 по умолчанию; UTF-8/16LE/16BE/32LE/32BE по начальному BOM |
| `lan::BaseProvider`: main/override и firmware variables | `AcePayloadProvider.cs`: `LoadSimFile` | Тот же текстовый декодер |
| Host sniffer `sniffer_catalog::Catalog::load` | `Plugins.Icem/LantableRegistry.cs`: `Load` | Тот же декодер после существующего лимита raw bytes |
| Host Web `registry::load_names` | `UnifiedVariableRegistry.cs`: `Load` | Тот же текстовый декодер |
| Emulator LAN legacy/firmware service-vector registry | `iCemEmule.Core/LantableRegistry.cs`: `JsonDocument.Parse(Stream)` | Строгий UTF-8, допускается один начальный UTF-8 BOM |

UTF-32LE определяется раньше перекрывающегося UTF-16LE BOM. Дополнительный
U+FEFF остаётся содержимым: JSON с двумя начальными BOM отклоняется; BOM внутри
строки сохраняется. Файлы UTF-16/32 без BOM не угадываются. Поведение INI parser
и отдельный строгий XML decoder не меняются.

`Registry::from_json` — внутренний parser уже декодированного JSON без BOM.
Выбор кодировки и снятие BOM принадлежат файловым адаптерам. Это не изменение
wire/API. Emulator stream registry и Host text registry используют один domain
parser, сохраняя разные правила чтения файла.

## Ошибки и ограничения

Для повреждённого текста явно сохранена прежняя Rust INI policy: lossy UTF-8,
U+FFFD для unpaired UTF-16 surrogate и нечётного хвоста, invalid UTF-32 scalar
и неполной последней группы. Это не обещание полной идентичности всех
malformed-byte случаев `.NET StreamReader` с произвольным разбиением на chunks.
Потоковый LAN JSON отклоняет invalid UTF-8 целиком, включая unused strings.

D22a per-file recovery и D22b registry validation/template isolation сохраняются.
Ошибка COM profile изолирована; malformed COM registry даёт пустой checked
snapshot; ошибка emulator LAN registry не мешает попытке прочитать firmware.
Host sniffer сохраняет 2 MiB raw-byte limit и vector/signal/text budgets.
Файлы не перезаписываются и не перекодируются при чтении.

Добавлены только прямые внутренние зависимости `icem-web` и
`icem-platform-windows` от существующего `icem-config` для общего декодера.
Новых внешних crates, versions/features, unsafe или lint exceptions нет.
Исходные .NET/AddIn/proto/SPA и CI не менялись.

## Проверка

Portable fixtures независимо кодируют Unicode, включая кириллицу и supplementary
characters. Шесть вариантов кодировки проверяются для каждого COM/LAN text source
и обоих Host consumers; проверяются override precedence, COM output и полные
124-byte LAN payloads, strict stream boundary и recovery, repeated/absent BOM,
malformed scalars и прежний лимит Host.

`FileEncodingOracle.ps1` вызывает настоящие BCL `File.ReadAllText` и
`JsonDocument.Parse(Stream)`. Healthy encoded catalogs сравниваются с original
`TemplateCatalog` через существующий oracle: меняются только два fixture path
bindings; parser/build/resolve/formatter bodies остаются исходными.

Проверено 2026-10-22:

- Rust 1.99.1: полный Linux workspace `fmt --check`, `check --all-targets`,
  `build`, `test`, `clippy --all-targets -- -D warnings` PASS, Cargo locked/offline.
  Suite: 231 passed, 0 failed, 0 ignored в 84 группах; Windows-only tests не входят.
- Четыре затронутых crates (`icem-config`, `icem-emulation`,
  `icem-platform-windows`, `icem-web`): MSVC `check --all-targets` PASS.
  Это compile-only проверка, без выполнения Windows binary.
- Linux PowerShell 7.6.7 / .NET 10.0.13: все 60 byte cases совпали с
  `File.ReadAllText`, включая malformed и границы 1020/1022/1024 bytes.
  Шесть stream cases и 12 healthy original COM catalogs main/override PASS.
  Выполнен тот же Rust test body с удалёнными только `cfg(windows)` в отдельной
  временной копии; production и tracked test source не менялись.
- Baseline: 513/513, 15 прежних exact approvals, frozen proto/assets PASS.
- Независимое ревью итоговых production changes и Host extension: замечаний нет.

Локальные Linux/.NET и cross-target результаты не доказывают Windows runtime,
operator/UI, COM/Npcap/iCem и real-SDK/MSI acceptance; эти gates остаются открытыми.

## Следующая функциональная работа

D22 остаётся открытым для Unicode `OrdinalIgnoreCase` в identity lookup и
порядка colliding JSON DTO properties. Конкретный healthy mismatch:
`Давление` и `ДАВЛЕНИЕ` должны находить одну registry variable, profile или
override, тогда как существующие ASCII-only comparisons могут её пропустить.
Затронуты `simulation.rs`, `lan.rs`, `runtime.rs` и `lan_pipeline.rs`.
Нельзя подменять ordinal semantics обычным `to_lowercase`/full case folding.

Для COM DTO `{"Name":"A","name":"B"}` требуется отдельный parser, сохраняющий
source-order last assignment; `serde_json::Value` уже теряет нужный порядок.
LAN exact property access и reference keys `registry`/`index` остаются отдельным
контрактом. Эти пробелы не объявляются закрытыми кодировками файлов.
