# Проверка миграции Rust

План Б от 2026-10-16: workspace находится в `rust/` исходного приватного репозитория.
Исходные .NET-файлы сохраняются в корне, второй копии в `rust/` нет.
Baseline привязан к `96e2206562c9389d1543095232115d81eae4b692`; manifest содержит все 513 исходных файлов.
Локально получены и сверены 512 файлов; большой PCAP остаётся в исходном Git-дереве,
его загрузка в эту среду не выполнена. Публикация строится поверх исходного дерева и сохраняет его blob без изменений.

Исторический M0/Modbus этап проверялся на Rust 1.85.1; текущая версия — 1.99.1 (см. ниже).
На начальном этапе Windows/.NET integration ещё не выполнялась; результат M2 приведён ниже.
UI, MSI и измерение полного payload пока не выполнены.

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

## M2 development-инкремент: локальная проверка

Добавлены 6 INI unit tests (24 pure/TCP tests суммарно на Linux), 7 Windows/.NET MMF
integration tests, 1 Windows live host → Modbus e2e и compile-fail !Send guard doctest.
Windows x86_64-pc-windows-msvc: cargo check и clippy --all-targets --locked --offline
с -D warnings проходят cross-compilation. Это не запуск Windows tests.
Точный результат Windows/Linux CI фиксируется по опубликованному commit/run отдельно.
Native exception и остающиеся release gates описаны в ADR 0001 и M2_STATUS.ru.md.

### Подтверждённый Windows/Linux CI M2

Код commit `8f27b0718a3321980b4167e277f0980d6e7e11ac`:
[Actions run 38936849475](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/38936849475) — **success**.
Windows job 117094387940: 32 tests + 1 compile-fail guard doctest PASS;
Linux job 117094387493: 24 tests PASS. На обеих ОС прошли fmt/check/build/test/clippy,
metadata pin/edition gate и сравнение 513/513 исходных blobs; frozen proto/assets не менялись.
Все 7 MMF tests и live EXE e2e выполнялись на Windows, не были skipped.

Дополнительно локальный Linux EXE прошёл fixture → FC03/06/04, inactive hold после tick,
завершение по сроку, отсутствие stdout, usage/input/unsupported live mode exit codes.
Реальная legacy AddIn, UI и полный payload этим результатом не проверены.
Машиночитаемый результат: `migration/validation-m2.json`.
