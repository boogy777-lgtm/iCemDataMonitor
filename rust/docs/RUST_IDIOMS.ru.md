# Rust 1.99: политика и применённые идиомы

Решение пользователя от 2026-10-16: использовать актуальную ветку 1.99 вместо 1.85.
Закреплён **1.99.1**, опубликованный 2026-10-15: патч исправляет в том числе
некорректную компиляцию FFI bool return на x86_64. Это важно учитывать для будущих Windows adapters.
Все Rust-пакеты: Edition 2024, rust-version 1.99.1; виртуальный workspace — resolver 3.
Edition 2024 уже была доступна в Rust 1.85, но начальный каркас использовал Edition 2021.

## Инварианты v3.1

Class: все Cargo workspace members (`crates/*`, `apps/*`); .NET AddIn и внешний wire API вне класса.
Inv: каждый member использует Edition 2024 и rust-version, равный точному pin из `rust-toolchain.toml`;
`tools/Verify-Scaffold.ps1` проверяет весь `cargo metadata` и затем выполняет fmt/check/build/test/clippy.
N+1: новый пакет наследует workspace package/lints; metadata loop включает его автоматически и отвергает расхождение. CI не содержит второй literal версии.

Class: фиксированные двухбайтовые слова Bus/Modbus; текстовые/переменные поля вне класса (`icem-bus`, `icem-protocols`).
Inv: элементы имеют тип `[u8; 2]`, неполный остаток отклоняется до записи; as_chunks + malformed/truncated fixtures.
N+1: следующий fixed-width decoder использует safe typed chunks, исходные wire/IEEE значения не меняются; регрессию обнаруживают wire tests.

## Применено в текущем коде

| Идиома | Появилась | Где и зачем |
|---|---|---|
| `as_chunks::<2>()`, `as_chunks_mut::<2>()` | 1.88 | Bus decode, FC16, fixture builder: массивы `[u8;2]` вместо slice с ручными индексами; остаток проверен |
| `if let ... && ...` | 1.88, Edition 2024 | TCP framing: одна проверка наличия длины и полноты ADU вместо вложенного if; порядок условий сохранён |
| `u32::cast_signed()` | 1.87 | Явная битовая переинтерпретация Unix int32; не saturating/checked numeric conversion |
| `u16::is_multiple_of(2)` | 1.87 | Парный адрес CDAB проверяется прямым предикатом |
| `std::iter::repeat_n` | 1.82 | Тест FC16: лаконичная генерация bytes по требованию нового Clippy; это не новинка после 1.85 |

Новый Clippy 1.99 сам обнаружил `chunks_exact_to_as_chunks` и `manual_repeat_n`.
Предупреждения исправлены в коде; CI по-прежнему выполняет `-D warnings`, без allow для обхода новых проверок.
Rustfmt выбирает стиль по Edition 2024; ручного параллельного formatting-механизма нет.
Resolver 3 учитывает rust-version при выборе dependencies; сторонних crates в текущем workspace ещё нет.

Изменение `cast_signed` дополнительно проверено независимыми wire cases:
0, i32::MAX, i32::MIN, -1. Существующие NaN payload/-0/CDAB, truncated writes,
mirror hold/overwrite, resize и реальные TCP fragmentation/coalescing tests сохранены.
Let-chain не содержит mutex guard в условии: время удержания register lock не расширяется.

## Правила для следующих модулей

- Новую стандартную API применять по подтверждённой задаче и checked invariant, а не ради даты релиза.
- Использовать safe stable API; `forbid(unsafe_code)` сохраняется во всех first-party crates.
- IEEE arithmetic и wire bytes остаются контрактом. Новые algebraic float APIs/fast math
  нельзя применять к DFB без отдельного доказательства числовой совместимости.
- Новые low-level/FFI возможности 1.99 не требуют внедрения в этот проект; MMF остаётся отдельным
  Windows integration gate, C variadics/raw-parts API не добавляются в обход запрета unsafe.
- `std::io::pipe` — анонимный pipe; он не заменяет gRPC HTTP/2 поверх именованного
  `iCemDataMonitor.Host` и emulator control pipe.
- Следующий protocol module не требует изменений core; не добавлять универсальный plugin runtime.
- Поднимать compiler pin и Cargo rust-version согласованно; metadata gate отвергнет пропущенный пакет.
  Старый 1.85 больше не является поддерживаемым toolchain.

## Первичные источники, проверено 2026-10-16

- [Rust 1.99.1 release notes](https://doc.rust-lang.org/1.99.1/releases.html#version-1991-2026-10-15).
- [Rust 1.99.0](https://blog.rust-lang.org/2026/10/01/Rust-1.99.0/).
- [Rust 1.88: let chains и typed chunks](https://blog.rust-lang.org/2025/06/26/Rust-1.88.0/).
- [Rust 1.87: cast_signed/is_multiple_of/pipe](https://blog.rust-lang.org/2025/05/15/Rust-1.87.0/).
- [Cargo resolver versions](https://doc.rust-lang.org/cargo/reference/resolver.html#resolver-versions).
- [Rustup toolchain file](https://rust-lang.github.io/rustup/overrides.html#the-toolchain-file).
