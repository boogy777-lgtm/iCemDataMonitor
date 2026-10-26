# iCem Data Monitor — Rust workspace

Текущий application workspace: Rust Host, GPUI Shell и независимый Rust emulator.
Согласованный software scope и N+1 v1.7 реализованы; Windows final-head CI,
real SDK/AddIn, hardware/operator/visual acceptance и production cutover —
отдельные gates. Имена `icem-host-dev.exe`, `icem-shell-dev.exe`,
`icem-emulator-engine-dev.exe` сохранены.
`icem-emulator-dev` — только template-based FLECS preview без COM/LAN/UI;
он не является штатным standalone приложением. [Общий README](../README.md).

## Архитектура

Vendor iCem.exe + net48 AddIn остаются: AddIn — единственный writer MMF layout5.
Физическая станция либо самостоятельный emulator подают COM FLECS/LAN ACE в iCem.
Для COM emulator используется внешняя пара драйвера com0com; для LAN — Npcap.
Serial Tee/Com0ComMonitor и 21 dormant REST route retired в
[C11](../docs/LEGACY_REFACTORING_C11.ru.md); драйвер com0com KEEP.
Пассивный Host sniffer обслуживает диагностику и MMF не пишет.

Единственный acquisition 200 мс публикует immutable typed `Arc<Snapshot>` для
Modbus, Web, MQTT, OPC UA и OPC DA sidecar. Запросы потребителей не читают MMF.
Host не запускает и не завершает emulator. Legacy .NET assemblies сохранены для
reference/build compatibility. [Подробная архитектура](../docs/architecture.md).

## Сборка

Windows x64, PowerShell7.4+, Rust1.99.1 (rust-toolchain.toml), MSVC/Windows SDK.
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

Default UI launch использует release layout с `assets/` и
`emulator/templates/`; `cargo build` сам не раскладывает эти sidecars.
MSI role `emulator` и `Start-Emulator.ps1` запускают `icem-emulator-engine-dev.exe`.

Toolchain pin един для workspace/CI; Edition2024, resolver3.
Application/shared crates запрещают unsafe; единственная собственная native
граница — [ADR0001](docs/adr/0001-win32-read-boundary.ru.md).
После fetch Rust checks работают offline; browser checks используют npm lockfile.
Linux tests не являются Windows runtime/UI acceptance.

## Windows MSI

Из корня репозитория:

```powershell
pwsh -NoProfile -File .\rust\tools\Build-WindowsRelease.ps1 -Version 0.1.0
```

Дополнительно нужны .NET SDK, WiX5.0.2 и реальный iCem SDK либо verified SDK-built
AddIn bundle. Полный пакет обязательно содержит исходный net48 AddIn; отсутствие
DLL фатально. MSI/checksum: `dist/rust/`. ZIP — build intermediate.
[Runbook, SDK runner и отличие synthetic installer contracts](installer/README.md).

## Документы и статус

- [Текущий software status](docs/IMPLEMENTATION_STATUS.ru.md)
- [N+1 v1.7 tracker/evidence](docs/NPLUS1_TRACKER.ru.md)
- [Архитектура модулей](docs/ARCHITECTURE.ru.md) и [критерии миграции](docs/MIGRATION_PLAN.ru.md)
- [Parity matrix / acceptance](docs/PARITY_MATRIX.ru.md)
- [MMF, Modbus, gRPC, REST, CLI и Windows IPC](docs/API_SPEC.ru.md)
- [MQTT](docs/MQTT_P6.ru.md), [OPC UA](docs/OPCUA_P7A.ru.md), [OPC DA](docs/OPCDA_P7B.ru.md)
- [GPUI desktop history](docs/DESKTOP_STATUS.ru.md), [hardware acceptance](docs/HARDWARE_ACCEPTANCE.ru.md)
- [Конфиг](../docs/config.md), [разработка](../docs/development.md), [релиз](../docs/release.md)
- [CI routing/gates](docs/CI_CD.ru.md), [validation history](docs/VALIDATION.ru.md)
- [Source audit history](docs/SOURCE_AUDIT.ru.md), [manifest](migration/source-manifest.json), [legacy ledger](migration/legacy-divergences.json)
- [Current OpenAPI](contracts/http/openapi.json), [pinned route inventory](contracts/http/route-inventory.json)

Этапные E1–E24/D22/M0–M8 отчёты сохраняют факты на своих commits. Их старые
NEXT/PARTIAL/future формулировки не определяют текущий coding backlog.
N+1 software завершён. Слияние C11 требует полного Windows CI на exact head;
актуальный verdict — в [PR44](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/44).
Предыдущий successful run40253303600 не является проверкой C11.
Frozen proto/SPA/assets/source-manifest/desktop inventory не переписываются.
Лицензия TBD; distribution terms — отдельное release решение.
