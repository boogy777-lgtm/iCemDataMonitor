# iCem Data Monitor — Rust migration

Workspace в папке `rust/` существующего приватного репозитория. .NET48 AddIn остаётся
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
Desktop UI — GPUI с сохранением существующих custom controls; emulator/tray ещё не перенесены.

## Документы

- [Архитектура и границы](docs/ARCHITECTURE.ru.md)
- [План миграции с критериями приёмки](docs/MIGRATION_PLAN.ru.md)
- [Спецификация API: DLL/MMF, Modbus, gRPC, REST, CLI, Windows IPC](docs/API_SPEC.ru.md)
- [Матрица функциональной совместимости](docs/PARITY_MATRIX.ru.md)
- [Обнаруженные расхождения исходников и документов](docs/SOURCE_AUDIT.ru.md)
- [Safe Rust и размер дистрибутива](docs/SAFETY_AND_SIZE.ru.md)
- [Rust 1.99 и применённые идиомы](docs/RUST_IDIOMS.ru.md)
- [Статус проверки этого этапа](docs/VALIDATION.ru.md)
- [OpenAPI 3.1 для действующего REST](contracts/http/openapi.json)
- [Оригинальный Protobuf без изменений](contracts/proto/telemetry.proto)

## Структура

| Путь | Назначение |
|---|---|
| `../` | Исходный .NET-проект и AddIn, сохранённые в корне репозитория |
| `apps/host/` | Будущий Rust host + CLI + собственный трей |
| `apps/shell/` | Будущая оболочка монитора, клиент host |
| `apps/emulator/` | Будущий самостоятельный эмулятор + UI/CLI + собственный трей |
| `crates/icem-bus/` | Побайтовый контракт шины и владеющий данными снимок |
| `crates/icem-core/` | Общие прикладные команды и порты чтения снимков |
| `crates/icem-config/` | Строгий read-once INI parser; XML запланирован |
| `crates/icem-protocols/` | Чистые codecs Modbus / ACE / FLECS |
| `crates/icem-emulation/` | Движок DFB, генерация, ручные значения, единицы |
| `crates/icem-platform-windows/` | Реализованный MMF reader; events/трей/COM/Npcap запланированы |
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
12 workspace packages; Windows-only native bindings: windows-sys 0.61.2 и windows-link,
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
