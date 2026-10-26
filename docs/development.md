# Разработка

Текущий application workspace — `src/rust/`; [README](../README.md) содержит
проверенные entry points, [Rust README](../src/rust/README.md) — подробные ссылки.
Software implementation и N+1 v1.7 завершены; обязательные exact-head Windows CI,
реальные SDK/AddIn, hardware/operator/visual и production-release gates различаются.

## Rust

Windows x64, PowerShell 7.4+, Git, rustup, MSVC Build Tools + Windows SDK.
Toolchain 1.99.1/Edition 2024 задан в `src/rust/rust-toolchain.toml`.
Из `src/rust/`:

```powershell
cargo fetch --locked
cargo fmt --all --check
cargo check --workspace --all-targets --locked --offline
cargo test --workspace --locked --offline
cargo clippy --workspace --all-targets --locked --offline -- -D warnings
cargo build --release --locked --offline -p icem-host-app -p icem-shell-app -p icem-emulator-app -p icem-opc-da-sidecar
pwsh -NoProfile -File tools/Verify-Scaffold.ps1 -RequireCompleteBaseline
```

Первый fetch требует сети; последующие Rust checks работают offline.
Browser checks используют npm lockfile. Pure/Linux checks не доказывают Windows
MMF/COM/UI. Следовать [CI workflow/gates](../src/rust/docs/CI_CD.ru.md).

Для fixture smoke:

```powershell
cargo run -p icem-host-app --bin icem-host-dev -- --modbus-fixture tests/fixtures/bus-v5-minimal.bin --bind 127.0.0.1:1502 --duration-ms 10000
```

Для штатного UI запуска использовать установленный release layout с `assets/`
и role catalogs (`emulator/templates/`); `cargo build` сам их рядом с EXE не копирует.
Для штатного запуска использовать `icem-shell-dev.exe`; emulator запускается
отдельно (`icem-emulator-engine-dev.exe`). Host: `--ini PATH`, `snapshot`, `get main`,
`header`, `stop|restart`; `--start` обеспечивает отсутствующий Host для CLI.
См. [HOST_E11](../src/rust/docs/HOST_E11.ru.md) и [emulator CLI](../src/rust/docs/EMULATOR_E24.ru.md).

## Legacy reference/build compatibility

Корневая `iCemDataMonitor.sln`, исходники `src/net/` и `installer/` сохранены.
Их сборка не переключает приложение на production Rust. Для reference build
нужен Windows x64, .NET SDK из `global.json` (10.0.302), WinUI tooling;
net48 AddIn требует настоящие vendor references из установки iCem.

```powershell
dotnet build iCemDataMonitor.sln -p:Platform=x64
dotnet test CI.slnf --filter "Category!=Live" -p:Platform=x64
```

Полный legacy Windows gate задаётся `.github/workflows/ci.yml`, а не успехом
изолированного Linux-теста. Никаких SDK stubs для release AddIn.
[MSI build и prerequisites](../src/rust/installer/README.md).

## Стенд и обслуживание

Canonical station directory: `C:\ProgramData\Halliburton\iCem\DataMonitoring`.
`config.ini` read-once; после правки нужен restart. AddIn владеет `Data.dat`;
Host его не создаёт. Для COM emulator нужна внешняя пара портов драйвера com0com,
для LAN — Npcap/выбранный adapter. В iCem требуется явный запуск LAN acquisition.
Sniffer Host — пассивная диагностика. Tee/Com0ComMonitor/admin REST удалены;
тесты этих исключительно удалённых wrappers не входят в текущий suite.
[Точный retirement и проверки](LEGACY_REFACTORING_C11.ru.md).

[Managed logging](../src/rust/docs/HOST_E12.ru.md), [конфиг](config.md),
[аппаратная приёмка](../src/rust/docs/HARDWARE_ACCEPTANCE.ru.md).
Исторические C1–C10 и инкрементальные Rust отчёты сохраняют факты своих commits;
их прежние NEXT/TODO не являются новым согласованным coding backlog.
