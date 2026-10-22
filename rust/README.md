# iCem Data Monitor — Rust migration

Workspace в папке `rust/` существующего репозитория. .NET48 AddIn остаётся
в исходном проекте. Цель: идентичный host, трей, web, Modbus и самостоятельный emulator
при меньшем полном дистрибутиве. Исходная база — commit `96e2206562c9389d1543095232115d81eae4b692`.

Это начальная стадия переноса; production-функции отмечены в матрице приёмки.

## Реализовано

Рабочий Modbus codec FC03/04/06/16, динамическое зеркало, bounded TCP server
и диагностический host на синтетическом Bus fixture. [Статус, ограничения и запуск](docs/IMPLEMENTATION_STATUS.ru.md).
Добавлены Windows MMF reader, строгий INI parser и подключение живой шины к dev host.
[Границы M2-инкремента](docs/M2_STATUS.ru.md). Добавлен web backend:
сохранённая SPA (две логические правки D16/D17), REST/history, gRPC-Web status/registers/metrics/theme.
[Запуск и ограничения](docs/WEB_STATUS.ru.md). Порядок: web → emulator → tray.
Desktop UI — нативный GPUI без WebView с сохранением существующих custom controls. Начат emulator:
[генератор FLECS, единицы и ACE codec](docs/EMULATOR_STATUS.ru.md);
[полная фиксированная DFB-фабрика и dev trace](docs/EMULATOR_E2.ru.md);
[LAN payload/freeze/overrides/units и ACE dev trace](docs/EMULATOR_E3.ru.md).
[Headless engine, control pipe и COM/Npcap adapters (E4)](docs/EMULATOR_E4.ru.md)
подключены в рабочей ветке. Добавлены [GPUI renderer, tray и local Host gRPC (E5)](docs/DESKTOP_STATUS.ru.md). [Нативная панель и LAN worker (E6)](docs/adr/0005-native-dashboard-lan-worker.ru.md). Полные UI/Host gates и аппаратная приёмка остаются открыты.

## Документы

- [Архитектура и границы](docs/ARCHITECTURE.ru.md)
- [План миграции с критериями приёмки](docs/MIGRATION_PLAN.ru.md)
- [Спецификация API: DLL/MMF, Modbus, gRPC, REST, CLI, Windows IPC](docs/API_SPEC.ru.md)
- [Матрица функциональной совместимости](docs/PARITY_MATRIX.ru.md)
- [Обнаруженные расхождения исходников и документов](docs/SOURCE_AUDIT.ru.md)
- [Safe Rust и размер дистрибутива](docs/SAFETY_AND_SIZE.ru.md)
- [Rust 1.99 и применённые идиомы](docs/RUST_IDIOMS.ru.md)
- [Статус проверки этого этапа](docs/VALIDATION.ru.md)
- [CI-2a: canonical INI read/create race и границы проверки](docs/CI2_CONFIG_RACE.ru.md)
- [CI-2b: synthetic TestServer fixture и CI diagnostics](docs/CI2_TEST_INFRA.ru.md)
- [CI: маршрутизация, общий gate и bounded Cargo cache](docs/CI_CD.ru.md)
- [OpenAPI 3.1 для действующего REST](contracts/http/openapi.json)
- [Оригинальный Protobuf без изменений](../iCemDataMonitor.Contracts/Protos/telemetry.proto)

## Структура

| Путь | Назначение |
|---|---|
| `../` | Исходный .NET-проект и AddIn, сохранённые в корне репозитория |
| `apps/host/` | Будущий Rust host + CLI + GPUI Tray |
| `apps/shell/` | Будущая оболочка монитора, клиент host |
| `apps/emulator/` | Dev previews и headless engine/pipe/CLI; production UI/tray ещё переносятся |
| `crates/icem-bus/` | Побайтовый контракт шины и владеющий данными снимок |
| `crates/icem-core/` | Общие прикладные команды и порты чтения снимков |
| `crates/icem-config/` | Строгий read-once INI parser и emulator XML |
| `crates/icem-protocols/` | Чистые codecs Modbus / ACE / FLECS |
| `crates/icem-emulation/` | Движок DFB, генерация, ручные значения, единицы |
| `crates/icem-platform-windows/` | MMF reader, COM/Npcap emitter adapters; полный lifecycle/трей впереди |
| `crates/icem-windows-native/` | Минимальная Win32 FFI-граница, отдельное исключение ADR 0001 |
| `crates/icem-transport/` | gRPC / gRPC-Web / REST, клиенты; контракт отдельно от I/O |
| `web/dashboard/`, `assets/` | Побайтовая копия действующей SPA и иконок |
| `contracts/` | Внешние API и реестр действующих/неподключённых маршрутов |
| `tests/fixtures/` | Синтетические byte fixtures; не выдаются за живые измерения |
| `tools/` | PowerShell: проверка baseline/workspace и измерение размера |
| `installer/` | План Rust MSI; исходный WiX сохранён в ../installer |

## Сборка

Из папки `rust/`, Rust 1.99.1 (Edition 2024), C++ Build Tools на Windows:

```powershell
cargo fetch --locked
cargo fmt --all --check
cargo check --workspace --all-targets --locked --offline
cargo test --workspace --locked --offline
cargo clippy --workspace --all-targets --locked --offline -- -D warnings
```

Или `pwsh -File tools/Verify-Scaffold.ps1 -RequireCompleteBaseline`.
Версия задаётся в `rust-toolchain.toml`; Cargo rust-version = 1.99.1, resolver = 3.
CI читает тот же pin и проверяет edition/MSRV всех участников workspace.
14 workspace packages; Windows-only native bindings: windows-sys 0.61.2 и windows-link,
зафиксированные Cargo.lock. Web dependencies и build-only protoc описаны в ADR 0002.
После fetch проверки Rust работают offline; browser checks используют npm lockfile.
Все прикладные crates запрещают unsafe; явное исключение —
[Win32-граница](docs/adr/0001-win32-read-boundary.ru.md).
EXE имеют имена skeleton/dev до полной приёмки; они не заменяют production.
Исходная лицензия — TBD; новая лицензия не назначена.

D17: inline GetUiPrefs принимал только ASCII trailer с colon+space. Tonic отдаёт
валидный grpc-status:0. Parser теперь принимает colon с нулём/пробелами/tab, только
целый grpc-status=0; layout/styles не меняются. Node VM проверяет исходный failure
и исправленный разбор, browser — тему во второй независимой странице.

Legacy maintenance после pinned baseline учитывается отдельным exact-hash ledger:
[migration/legacy-divergences.json](migration/legacy-divergences.json). Исходный
source-manifest не обновляется при принятых исправлениях legacy. Guard и его 25
регрессионных сценариев входят в Scaffold; CI запускается и для legacy-only изменений.

[Host E8](docs/HOST_E8.ru.md): точный план пробелов, Web persist-only owner integration, OPC external no-op и отделение startup-effective web от session plan. Полная production/Windows приёмка остаётся открытой.

[Host E9](docs/HOST_E9.ru.md): boot replay после reconnect, отмена при backpressure,
освобождение Shell transport session и explicit at-most-one navigation attempt per demand.
Потерянный consume-ответ не ретраится автоматически. Production/MSI и visual gates открыты.

## Windows installer and N+1 releases

Windows x64, PowerShell 7.4+ and MSVC Build Tools:

```powershell
pwsh -NoProfile -File .\rust\tools\Build-WindowsRelease.ps1 -Version 0.1.0
```

Run that command from the repository root. It creates a Windows MSI with Host, Shell, independent emulator, the mandatory original
net48 AddIn and original Burservis installer branding, plus a SHA256 file in `rust/dist/`. The verified ZIP is a build intermediate.
[Prerequisites, contents, launch and CI artifact](installer/README.md).
Both Rust CI and the release workflow target Windows only. Release packaging keeps
the current `*-dev.exe` names. Manual Actions runs can choose the next `rust-vX.Y.Z`
version and create a draft prerelease after install/upgrade/uninstall checks.
Production cutover and hardware/UI acceptance remain open.

The complete installer requires the real iCem SDK or a verified SDK-built AddIn
bundle. Missing DLL is fatal. Original Burservis assets/bitmap generator and payload harvester are reused; existing
.NET MSI cutover remains blocked. See installer/README.md for SDK runner/artifacts
and the distinction between synthetic installer contracts and real release checks.

[Host E10](docs/HOST_E10.ru.md): настоящий read-only Npcap capture, bounded poll,
pause/resume, counters и actual availability через Host owner/RPC/GPUI.
P17 остаётся PARTIAL: decode/file capture и аппаратная приёмка не закрыты.

E11: [первый запуск/ensure, CLI, config discovery и MSI ACL](docs/HOST_E11.ru.md).
Shell автоматически запускает отсутствующий Host один раз; reconnect не повторяет
spawn. Строгая повреждённая конфигурация сохраняется и блокирует запуск; отсутствующая
создаётся из схемы. CLI/partial software evidence не закрывают SDK/UI/hardware gates.

## E12: управляемые логи (актуальное дополнение)

[HOST_E12](docs/HOST_E12.ru.md) и [ADR 0008](docs/adr/0008-managed-file-logs.ru.md):
общий файловый owner, native ON/OFF/clear для Host/Shell и независимого emulator,
сохранённая настройка, bounded rotation и безопасная частичная очистка. Frozen
proto/web/emulator contracts неизменны; internal JSON HTTP/2 control v1 доступен
только на существующем Host pipe. Mapping: 176 legacy + 6 explicit extensions,
visual parity NOT_PROVEN. P27 остаётся PARTIAL до Windows/operator/hardware gates.
Console-close/window lifecycle — следующий самостоятельный инкремент.

## E13: консоль Host

[Общий owner Show/Hide и graceful Ctrl+C](docs/HOST_E13.ru.md) подключён к tray,
startup и прежнему RPC. Console X → hide остаётся открытым: Windows не позволяет
отменить CTRL_CLOSE возвратом TRUE. Windows runtime/visual acceptance не доказана.

## E14: lifecycle окон

[Emulator X → Hide/Open и завершение UI](docs/HOST_E14.ru.md): тот же HWND, actual
visibility и завершение in-flight после UI loop. Actions пропущены из-за лимита владельца.


E15: [Host console X → Hide](docs/HOST_E15.ru.md), [ADR 0011](docs/adr/0011-host-diagnostic-window.ru.md).
Собственное диагностическое окно; frozen API и E14 сохранены. Ctrl+C относится к
окну Host, после detach родительский терминал не адресует Host. Windows/operator
результаты фиксируются по exact head; полная parity не объявляется.


E16: [Host passive decode/catalog](docs/HOST_E16.ru.md). Capture owner выполняет
Host-specific decode и bounded cache/tracking; periodic witness идёт в общий logger.
Dormant REST/file capture не активированы; hardware/visual parity не объявляется.


E17: [типизированный application API emulator и общий watch client](docs/EMULATOR_E17.ru.md),
[ADR 0014](docs/adr/0014-emulator-application-api.ru.md). GPUI передаёт Command напрямую;
pipe v1 остаётся compatibility adapter, включая coercions и исторический save ACK.
Внутренний результат отделяет apply/save failure; CLI text/JSON watch получает поток,
а не только ACK. Software CI не закрывает operator/SDK/hardware gates.


E18: [типизированный Shell → Host](docs/HOST_E18.ru.md). Проверенные параметры/status,
generated replies, общий bounded worker. Logging/dashboard JSON — следующие
границы; visual/SDK/hardware gates остаются открытыми.


E19: [типизированное управление логами](docs/HOST_E19.ru.md): Command/Status/Reply,
Incomplete effects и Unknown без retry, строгий неизменённый internal v1 adapter.
Локальные clear counts не выводятся из v1 текста; GPUI получает typed facts.
176+6 mapping прежний; Windows exact-head и operator/SDK/hardware gates отдельно.


E20: [типизированные ответы emulator](docs/EMULATOR_E20.ru.md),
[ADR0017](docs/adr/0017-typed-emulator-payloads.ru.md): 21 concrete backend payload families,
private ControlResult, typed Execution → derived GPUI View/Signal identity; JSON только
на v1 adapter/display formatting, без response roundtrip. Полные 43 E19 wire frames,
parser C# domain-shaped fixtures и mismatch/nonfinite/partial/save-failure checks.
D49: dashboard status отображает уже typed Model.host, сохраняя last-known при fault.
Dashboard REST/selftest/landiag и visual/operator/SDK/hardware gates остаются открытыми.


E21: [проверенные dashboard snapshots](docs/DASHBOARD_E21.ru.md),
[ADR0018](docs/adr/0018-typed-dashboard.ru.md): owned Registers/History через Shell и GPUI,
полная geometry/value/UTC/liveness validation, independent history failure и
current-selection filtering. REST/SPA/proto, appearance и 176+6 mapping прежние.
Software/exact-head Windows evidence фиксируется отдельно; physical/SDK/hardware
acceptance остаётся OPEN. Далее selftest/landiag и stdin CLI отдельными increments.


E22: [встроенный emulator --selftest](docs/EMULATOR_E22.ru.md): полный mapping T1–T26, first-argument dispatch до startup, read-only EXE-relative emulator catalogs, isolated native TestCtl и полный i32 failCount. Windows process/release smoke tests добавлены; local Linux не доказывает Windows runtime. D22/operator/SDK/hardware gates открыты; E23 — explicit --landiag и stdin CLI.

E23/E24 current: [standalone LAN diagnostic](docs/EMULATOR_E23.ru.md) и
[launch/default UI/stdin CLI](docs/EMULATOR_E24.ru.md) реализованы; Linux checks
PASS, Windows exact-head CI/runtime и UI/operator/SDK/hardware acceptance отдельно.
D22 loader/encoding/Unicode остаётся кодовым backlog; прежние записи о будущих
selftest/landiag/stdin CLI — исторический статус.

[D22a](docs/EMULATOR_D22A.ru.md): production LAN loaders сохраняют completed prefix
при ошибке файла и переходят к firmware; Registry diagnostics направлены в startup
logger. Strict parser/COM fallback/encoding/Unicode — отдельные границы.

[D22b](docs/EMULATOR_D22B.ru.md): COM catalog восстанавливает пустой registry при
DTO failure, сохраняет независимые templates и явно отклоняет null records без
зависимости от traversal order. Explicit file load строгий; encoding/Unicode и Windows acceptance отдельно.

[D22c](docs/CATALOG_D22C.ru.md): общий текстовый декодер читает BOM UTF-8/16/32
в COM/LAN simulation catalogs и двух Host catalog consumers. Emulator LAN registry
сохраняет отдельный UTF-8 stream contract; Host byte budgets и file recovery прежние.
Unicode identity matching и порядок colliding DTO properties остаются следующим кодовым этапом.

[D22d](docs/CATALOG_D22D.ru.md): emulator registry/profile/variable/override lookup
использует общий Unicode identity mechanism и типизированные LAN keys. Canonical
LAN signal resolution общий для validation и preview. Таблица исчерпывающе сверяется
с BCL provider; actual Windows mapping/runtime и JSON property collisions остаются
отдельными открытыми границами.
