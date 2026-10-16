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

## Web development-инкремент

Локально Rust 1.99.1: 32 tests PASS, fmt/check/build/clippy PASS.
Windows cross-target check PASS; runtime integration и browser scenario проверяются
новым exact-commit CI. Исходная SPA сохраняется, ограниченные control RPC — UNIMPLEMENTED.
Полная visual/GPUI acceptance этим этапом не объявляется.

D16 regression: Node VM воспроизводит исходную инверсию light→false,
перенесённый inline script проверяется для light→true и dark→false.

### Подтверждённый web CI

Код commit `db968757fe86f43951c4a8bd1536775e990b6565`:
[Actions run 38941562186](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/38941562186)
— **success**. Windows job 117110206717: 40 tests + 1 compile-fail doctest PASS;
Linux job 117110206337: 32 tests PASS, Node D16/D17 regression PASS и Chrome scenario PASS.
На обеих ОС fmt/check/build/test/clippy и baseline 513/513 PASS.

Chrome: unchanged CSS/JS/icons + reviewed HTML predicates, 298 pairs, selection/chart/history,
302 metrics, Status page, dark/light persistence и theme truth второй независимой страницы.
[Снимки dashboard-dark/status-light](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/38941562186/artifacts/12062734952)
получены из CI и просмотрены. Это functional/render smoke test, не полное pixel-diff
сравнение всех состояний исходного WinUI/будущего GPUI.

Первый browser run выявил исходную инверсию bool (D16), повторный — несовместимость
strict trailer whitespace (D17). Обе исправлены в Rust-копии HTML с regression tests;
остальные UI bytes и все original .NET files сохранены. Полных parity PASS нет.
Точные результаты и remaining gates: migration/validation-web.json.

## Emulator E1

Добавлены domain, ACE и реальные EXE preview tests; Windows original-C# differential
test включён в workspace. Локальные/cross-target результаты и exact CI фиксируются по
фактическому run; Linux не считается выполнением C# oracle или Windows/GPU проверки.
Границы описаны в EMULATOR_STATUS.ru.md.

### Подтверждённый emulator E1 CI

Код commit `e43ff8314e7c7f48cfbdc9389d90655986049a05`:
[Actions run 38946224137](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/38946224137)
— **success**. Windows job 117125894186: 51 tests + 1 compile-fail doctest PASS;
Linux job 117125893854: 42 tests и Chrome regression PASS. fmt/check/build/test/clippy,
pin/edition gate и baseline 513/513 проходят на обеих ОС.

Original C# oracle реально выполнен на .NET 10.0.12: 2 templates, 9 801 unit cases,
132 settings cases, 32 DFB steps + 24 edges, 39 formatter cases, 5 exact ACE frames
и 180 signal decodes — PASS. FLECS strings/ACE bytes exact; numerical tolerance
1e-12 relative/absolute floor1, zero sign/Infinity exact, NaN classification.
[CI artifact emulator-differential](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/38946224137/artifacts/12067450576)
получен и проверен. Машиночитаемый отчёт: migration/validation-emulator-e1.json.

Проверены реальные EXE FLECS/CRLF/override и usage/input exits. Физические COM/LAN,
full DFB composition и GPUI/tray не проверялись и не объявляются реализованными.

## Emulator E2 — подтверждённый результат

Полная фиксированная цементировочная фабрика: [контракт E2](EMULATOR_E2.ru.md).
Добавлены full-network C# oracle, inventory/RNG/sentinel checks и dev EXE tests.
Code commit `e0b320d29a8eda9fb3ea23d7378325efde9e3376`,
[Actions 38964177178](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/38964177178)
— **success**. Windows job 117186408548: 57 tests + compile-fail doctest;
Linux job 117186408910: 47 tests + preserved dashboard Chrome regression.
fmt/check/build/test/clippy и baseline 513/513 PASS на обеих ОС.

Original C# oracle .NET 10.0.12: 245 registered ports, 18 parameters, 325 wire names;
6 720 exact RNG draws, 231 trajectory frames (224 ticks), 40 edge frames, bitfield
probe и post-network RNG sentinels — PASS. E1 oracle также PASS без уменьшения
его покрытия. Artifact emulator-differential 12074601536 получен и прочитан:
оба JSON report имеют result=PASS. Данные: migration/validation-emulator-e2.json.

Local Linux: 47 tests, workspace fmt/build/test/clippy; Windows cross-target
check/clippy всех emulator targets PASS. C# и Windows MMF integration выполнены
именно в Windows CI, не приписаны локальному Linux. GPUI/hardware/tray и full-size
comparison этим инкрементом не подтверждаются.
