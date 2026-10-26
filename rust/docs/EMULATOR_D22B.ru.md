# D22b — COM catalog recovery и явная Rust validation

Исторический отчёт указанного инкремента. Текущее software-состояние:
[N+1 tracker](NPLUS1_TRACKER.ru.md), [implementation status](IMPLEMENTATION_STATUS.ru.md).
Прежние NEXT/PARTIAL описывают тот checkpoint; hardware/UI/operator/release gates
остаются отдельными. [C11](../../docs/LEGACY_REFACTORING_C11.ru.md) удалил dormant
REST/serial wrappers и их эксклюзивные tests; com0com driver сохранён.

Evidence: pinned `iCemEmule.Core/Core/TemplateCatalog.cs`: discovery, DTO, valid
resolution, fallback и внешние результаты. Изменены только rust/ implementation,
tests/docs. .NET, CI, deps/lints/unsafe policy и D22a implementation прежние.

Class: production COM file/template recovery в `simulation.rs`; explicit file load,
LAN, waveform/formatter, encoding/Unicode — ближайшие nonmembers.
Inv: malformed registry→один diagnostic+empty validated snapshot, inline profiles
сохраняются; malformed template изолирован; fallback только при нуле templates.
N+1: private ValidatedRegistry создаётся одной проверкой; shared fixtures проверяют
Rust error policy, original-method oracle — healthy внешний facade/results.

## Уточнение пользователя и выбранная политика

Уточнение от 2026-10-22 также внесено в AGENTS.md: совместимость означает внешний
фасад корректных configs, formats, traffic и результатов. Rust использует собственный
безопасный дизайн. Legacy incidental exceptions/control flow не являются контрактом.

load_catalog валидирует выбранный registry перед profiles. Syntax/read/DTO error
отбрасывает этот файл целиком, добавляет один registry diagnostic и создаёт пустой
ValidatedRegistry. LAN completed-prefix policy D22a сюда не переносится. Private
owned snapshot не допускает unchecked registry в builder; global validation не
повторяется для каждого template. Warnings получает существующий startup logger.

Любой null registry record считается malformed input независимо от позиции.
Это осознанно заменяет C# null dereference, зависевший от порядка reference lookup.
Inline variables сохраняются; references используют явную missing-ref policy:
пропустить variable и добавить warning. Reference-only profile может остаться пустым
и считается загруженным template. Malformed profiles отклоняются независимо от
соседних valid profiles. Built-in flecs fallback нужен только при нуле loaded profiles.

Missing/null registry variables и nullable/missing simulation/families/family entries
сохраняют поддерживаемые empty/default результаты. Scalar/array вместо DTO object
и неправильные typed fields отклоняют registry. Malformed emulator override не
вызывает скрытый retry main registry. Discovery/exclusions и caller-owned cache прежние.

Explicit load_template/template_from_json остаются строгими: malformed registry
возвращает Err, recovery доступен только production catalog. Valid reference grammar
(exact registry/index) отделён от case-insensitive inline DTO keys. Unrelated properties
игнорируются; unknown snake_case не переименовываются. Encoding/Unicode normalization
не добавлена. Exact exception wording/order legacy не является целью diagnostics.

## Проверки и открытые границы

Portable shared fixtures проверяют all-or-empty policy, одинаковый recovery при null
до/после match, malformed profile среди valid, empty reference-only profiles,
nullable/missing shapes, override, property case/snake и zero-profile fallback.
Отдельная проверка сохраняет strict explicit-file failure.

ComCatalogOracle.ps1 меняет только TemplatesDir/LogPath bindings оригинального
TemplateCatalog на fixture-owned paths. Обратная подстановка обязана восстановить
точный source text: method bodies и DTO остаются исходными. Wrapper сбрасывает cache
между cases и вызывает original Load; catch/deserialization/build/fallback в тесте
не реконструируются. Windows differential использует только healthy полностью
разрешимые configs: сравнивает весь catalog/variables и COM wire lines через original
ComputeValue/FormatLine. Malformed cases проверяют осознанную Rust policy отдельно,
а не требуют воспроизвести legacy bugs.

Rust1.99.1 Linux: focused tests/Clippy и полный workspace fmt/check/build/test/Clippy
locked/offline PASS в отдельном icem-d22b-target. MSVC emulation all-targets
check/Clippy PASS — compile-only. Новый C# oracle локально не запускался (pwsh
отсутствует); его verdict и Windows build/runtime требуют exact-head CI.
Operator/UI/SDK/hardware acceptance — отдельные gates.

D22 целиком не закрыт: File.ReadAllText encoding/invalid-byte policy и Unicode
OrdinalIgnoreCase вне инкремента. Existing DOM ambiguity case-colliding keys также
остаётся: serde_json Value теряет source order, helper выбирает первый case-insensitive
map key. Для Name/name duplicates нужна отдельная parser/validation policy; здесь
проверены одиночные case variants, без обещания полной duplicate-key parity.
