# Проверка миграции Rust

План Б от 2026-10-16: workspace находится в `rust/` исходного приватного репозитория.
Исходные .NET-файлы сохраняются в корне, второй копии в `rust/` нет.
Baseline привязан к `96e2206562c9389d1543095232115d81eae4b692`; manifest содержит все 513 исходных файлов.
Локально получены и сверены 512 файлов; большой PCAP остаётся в исходном Git-дереве,
его загрузка в эту среду не выполнена. Публикация строится поверх исходного дерева и сохраняет его blob без изменений.

Rust 1.85.1 установлен из официального дистрибутива. Результаты cargo gates записываются ниже только после выполнения.
Windows/.NET integration, UI, MSI и измерение полного payload ещё не выполнены.

Локальный M0 Linux: `cargo fmt --all`, `cargo check --workspace --all-targets --locked --offline`,
`cargo test --workspace --locked --offline` (5 tests PASS),
`cargo clippy --workspace --all-targets --locked --offline -- -D warnings` — PASS.
