# Проверка миграции Rust

План Б от 2026-10-16: workspace находится в `rust/` исходного приватного репозитория.
Исходные .NET-файлы сохраняются в корне, второй копии в `rust/` нет.
Baseline привязан к `96e2206562c9389d1543095232115d81eae4b692`; manifest содержит все 513 исходных файлов.
Локально получены и сверены 512 файлов; большой PCAP остаётся в исходном Git-дереве,
его загрузка в эту среду не выполнена. Публикация строится поверх исходного дерева и сохраняет его blob без изменений.

Исторический M0/Modbus этап проверялся на Rust 1.85.1; текущая версия — 1.99.1 (см. ниже).
Windows/.NET integration, UI, MSI и измерение полного payload ещё не выполнены.

Локальный M0 Linux: `cargo fmt --all`, `cargo check --workspace --all-targets --locked --offline`,
`cargo test --workspace --locked --offline` (5 tests PASS),
`cargo clippy --workspace --all-targets --locked --offline -- -D warnings` — PASS.

## Modbus-инкремент

Linux / Rust 1.85.1: fmt --check, check --all-targets, test (17 tests PASS),
clippy --all-targets -- -D warnings — PASS; все команды с --locked --offline где применимо.
`cargo build --workspace` и `cargo build --profile size -p icem-host-app` — PASS.
Реальный icem-host-dev: fixture → TCP FC03/06/04, удержание external write inactive zone
через tick, завершение по сроку, отсутствие stdout, exit 2/1 на usage/input errors — PASS.

Первый GitHub Actions run `38925436458` выявил дефекты baseline script на Windows/Linux.
Они исправлены в этом инкременте; результат повторного CI фиксируется по его фактическому run.
Полный parity gate остаётся открытым: Windows MMF/AddIn, tray/web/emulator, MSI/size не проверены.

## Обновление до Rust 1.99.1 / Edition 2024

Toolchain установлен из официального дистрибутива; rustc 1.99.1
(58cf8f9c3 2026-10-13), LLVM 23.1.1. Все 10 packages имеют MSRV 1.99.1.
Локально прошли: fmt --check, check --all-targets, build --workspace,
test --workspace (18 tests PASS), clippy --all-targets -- -D warnings;
Cargo check/build/test/clippy выполнялись с --locked --offline.
Сборка `--profile size -p icem-host-app` также прошла.
Добавлены signed timestamp boundary cases; wire/inactive/stale/TCP tests сохранены.

CI устанавливает toolchain/components из rust-toolchain.toml; Verify-Scaffold.ps1
проверяет metadata каждого member на соответствие pin/Edition 2024 и собирает EXE.
Детали применения новых APIs: [Rust idioms](RUST_IDIOMS.ru.md).
Результат нового Windows/Linux CI подтверждается по exact commit/run, без переноса старого PASS на новый compiler.
Это обновление toolchain и идиом; полная функциональная миграция остаётся незавершённой.
