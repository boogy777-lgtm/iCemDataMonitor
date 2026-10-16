# iCem Data Monitor — Rust migration

Workspace в папке `rust/` существующего приватного репозитория. .NET48 AddIn остаётся
в исходном проекте. Цель: идентичный host, трей, web, Modbus и самостоятельный emulator
при меньшем полном дистрибутиве. Исходная база — commit `96e2206562c9389d1543095232115d81eae4b692`.

Это начальная стадия переноса; production-функции отмечены в матрице приёмки.

## Документы

- [Архитектура и границы](docs/ARCHITECTURE.ru.md)
- [План миграции с критериями приёмки](docs/MIGRATION_PLAN.ru.md)
- [Спецификация API: DLL/MMF, Modbus, gRPC, REST, CLI, Windows IPC](docs/API_SPEC.ru.md)
- [Матрица функциональной совместимости](docs/PARITY_MATRIX.ru.md)
- [Обнаруженные расхождения исходников и документов](docs/SOURCE_AUDIT.ru.md)
- [Safe Rust и размер дистрибутива](docs/SAFETY_AND_SIZE.ru.md)
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
| `crates/icem-config/` | Канон config.ini / XML; парсеры запланированы |
| `crates/icem-protocols/` | Чистые codecs Modbus / ACE / FLECS |
| `crates/icem-emulation/` | Движок DFB, генерация, ручные значения, единицы |
| `crates/icem-platform-windows/` | MMF, mutex, events, трей, COM, Npcap, запуск процессов |
| `crates/icem-transport/` | gRPC / gRPC-Web / REST, клиенты; контракт отдельно от I/O |
| `web/dashboard/`, `assets/` | Побайтовая копия действующей SPA и иконок |
| `contracts/` | Внешние API и реестр действующих/неподключённых маршрутов |
| `tests/fixtures/` | Синтетические byte fixtures; не выдаются за живые измерения |
| `tools/` | PowerShell: проверка, восстановление baseline, приватная публикация, размер |
| `installer/` | План Rust MSI; исходный WiX сохранён внутри legacy |

## Сборка

Из папки `rust/`, Rust 1.85.1, C++ Build Tools на Windows:

```powershell
cargo fmt --all --check
cargo check --workspace --all-targets --locked --offline
cargo test --workspace --locked --offline
cargo clippy --workspace --all-targets --locked --offline -- -D warnings
```

Или `pwsh -File tools/Verify-Scaffold.ps1 -RequireCompleteBaseline`.
Никаких сторонних Rust dependencies в начальном workspace нет.
EXE имеют имена skeleton/dev до полной приёмки; они не заменяют production.
Исходная лицензия — TBD; новая лицензия не назначена.
