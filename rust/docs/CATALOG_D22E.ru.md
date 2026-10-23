# D22e — типизированный вход COM-каталогов

COM JSON `{"name":"first","NAME":"last"}` теперь выбирает `last`;
обратный порядок выбирает `first`. Повторный nested object заменяет прежний
целиком, а не объединяет его поля. File/string loaders сохраняют порядок JSON
до присваивания, без промежуточного `serde_json::Value`.

Class: известные COM DTO properties, включая повторы и case variants;
точные reference properties `registry`/`index` — соседний отдельный случай.
Inv: каждый typed assignment проверяется при чтении; все выбранные variable
records проверены до проекции и диагностик отсутствующих ссылок. Схемы и tests
проверяют это отдельно от сравнения здоровых входов с исходными методами .NET.
N+1: новое DTO property добавляется в соответствующую локальную schema;
schema одновременно определяет decoder и recognized fields для Value guard.

## Rust-модель

Приватный `com_config` содержит конкретные Profile, Registry, RegistryVariable,
Simulation, Families, Family, Inline и enum VariableInput. Локальный `dto!`
задаёт имя, тип и default каждого поля один раз; visitor последовательно читает
assignments, пропуская неизвестные поля. Это не перенос иерархии или обходов C#.
Фиксированные ASCII имена полей сравниваются без учёта ASCII case; Unicode
идентичность domain names остаётся отдельным механизмом D22d.

`CheckedRegistry` владеет полностью проверенным registry, включая неиспользуемые
records. `ProfileDocument` владеет typed header и raw variables array. Последний
array assignment заменяет предыдущий. Содержимое отброшенного array не
интерпретируется как VariableInput; синтаксис JSON и тип самого array проверяются.
Directory discovery проверяет наличие name/deviceType до интерпретации records:
nameless registry override не становится template и не требует filename special case.
Перед построением Template весь выбранный array превращается в typed records.

В VariableInput exact lowercase `registry` выбирает Reference. Только exact
`index` задаёт его индекс; последние exact assignments выбираются до type check.
`Registry` не выбирает reference, а `INDEX` не переопределяет reference index.
Иначе record декодируется как Inline с обычными case-insensitive DTO properties.
RawValue ограничен этой JSON boundary и точными числовыми tokens; domain data
после проверки представлены конкретными Rust types.

`simulation::template_from_str` — общий parser/projection для explicit file load
и catalog. Прямой `template_from_json(&Value, &Value)` проверяет неоднозначные
recognized DTO keys и использует тот же parser. Value уже потерял exact duplicate
keys, source order и исходное написание чисел: adapter не обещает их восстановить.
Неизвестные поля и reference-mode fields не получают лишних DTO restrictions.

## Ошибочные входы и числа

Неправильный тип известного typed assignment отклоняется, даже если позднее
присвоено корректное значение. Registry failure даёт пустой registry и diagnostic;
ошибочный template изолируется от соседних файлов. Explicit load возвращает ошибку.
Null registry records отклоняются атомарно. Missing registry reference пропускается
с diagnostic, как раньше. Resolver, overrides, fallback, engine и formatter прежние.

Int32 принимает целый JSON token в диапазоне i32, включая lexical `-0`.
Дробные/exponent forms (`0.0`, `1e0`) и overflow отклоняются. Отдельный Int32
decoder нужен потому, что общий JSON numeric visitor классифицирует `-0` как float.
Float `-0.0` сохраняет знак в COM line. Неизвестное numeric поле `1e400` можно
пропустить, однако известный f64 overflow отклоняется. .NET может получить Infinity
и затем упасть при сериализации oracle output; это не объявляется healthy numeric
parity и не воспроизводится как желательное Rust-поведение.

## Зависимости

Добавлен прямой `serde` dependency для Deserialize API и feature `raw_value`
у уже locked `serde_json`. Новых packages или versions нет; `float_roundtrip`
сохранён, глобальный `preserve_order` не нужен. Обе лицензии MIT OR Apache-2.0.
Application `forbid(unsafe_code)` прежний. Safe RawValue Deserialize проверяет JSON
перед хранением; feature активирует upstream repr(transparent) conversions между
str/Box<str> и RawValue (три transmute в locked raw.rs). Приложение не использует
unsafe `from_string_unchecked`; trust boundary — locked serde_json implementation.

## Проверки

159 healthy raw fixtures сохраняют duplicates/order, escaped keys, все DTO scopes,
nested replacement, defaults/nulls, Unicode names, exact references, unknown fields
и lexical `-0`. Проверяются полный Template, metadata, variables, COM line, strict
load и production catalog; BOM/UTF-16 проходит через общий D22c decoder.
Malformed policies и Value ambiguity проверяются отдельными portable tests.

Независимый unchanged `ComCatalogOracle.ps1` вызывает исходные TemplateCatalog
и SimulationEngine methods. На Linux PowerShell 7.6.7/.NET 10.0.13 все 159
templates и COM lines совпали. Временная копия нового integration test с удалёнными
только `cfg(windows)` и исправленными support paths выполнила 8/8 bodies; tracked
test остаётся Windows-gated. Linux evidence не доказывает Windows runtime.

Проверено 2026-10-22 на Rust 1.99.1, locked/offline: workspace fmt/check/build,
test и Clippy с `-D warnings` PASS; 246 passed, 0 failed, 0 ignored в 86 группах.
Windows-gated tests в это число не входят. MSVC `check --all-targets` для
icem-config, icem-emulation, icem-platform-windows и icem-web PASS (compile-only).
Baseline guard: 513/513, 15 прежних exact approvals, frozen surfaces PASS.
Независимое ревью конечных production/test blobs не выявило блокеров.

Исходные .NET/AddIn/proto/SPA и CI не меняются. D22 и общая миграция не объявляются
полностью закрытыми: Windows provider/runtime, operator/UI и SDK/hardware gates
требуют отдельных фактических результатов.
