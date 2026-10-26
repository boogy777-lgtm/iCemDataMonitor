# C11 — retirement dead legacy closure

Согласованный объём: 17 удалений + 5 минимальных integration/test/project edits.
Code source: remote `ba8b7a1faef43b44f34d490c7e601855f8f2c2d3`,
[PR44](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/44).
Локальный code checkpoint `8e69423387e2f516dfb085911e4abf37d2972887`,
tree `f2826f86d23f3be94fc6dffb54defe66457aeacd`. Не является документационным
final head; реальная provenance для последующих изменений фиксируется отдельно
после публикации. Frozen manifest и historical route inventory не переписаны.

Class: недостижимые legacy REST/admin и serial wrappers; active Host wiring,
sniffer, independent emulator, AddIn и Rust runtime вне класса.
Inv: четыре активных GET/OpenAPI и единственный AddIn writer сохранены; удалены
только эксклюзивные tests/dependencies и обновлён source guard.
N+1: новый dead closure требует нового use-site scan и точного списка; driver
com0com и capture internals не считаются автоматически принадлежащими closure.

## Удалено: 17 файлов

- `iCemDataMonitor.Host.Core/Endpoints/Com0ComEndpoints.cs`
- `iCemDataMonitor.Host.Core/Endpoints/ModbusPreviewEndpoints.cs`
- `iCemDataMonitor.Host.Core/Endpoints/RegistryEndpoints.cs`
- `iCemDataMonitor.Host.Core/Endpoints/ServiceFacades.cs`
- `iCemDataMonitor.Host.Core/Endpoints/SnifferEndpoints.cs`
- `iCemDataMonitor.Host.Core/Endpoints/SourceEndpoints.cs`
- `iCemDataMonitor.Host.Core/Endpoints/TemplatesEndpoints.cs`
- `iCemDataMonitor.Transport.Tests/Com0ComMonitorTests.cs`
- `iCemDataMonitor.Transport.Tests/SerialTeeServiceTests.cs`
- `iCemDataMonitor.Transport/Com0ComMonitor.cs`
- `iCemDataMonitor.Transport/ISerialPort.cs`
- `iCemDataMonitor.Transport/Models/Com0ComStatus.cs`
- `iCemDataMonitor.Transport/Models/ComTeeStatus.cs`
- `iCemDataMonitor.Transport/Models/TeeOptions.cs`
- `iCemDataMonitor.Transport/SerialPortAdapter.cs`
- `iCemDataMonitor.Transport/SerialTeeService.cs`
- `iCemDataMonitor.UI.Tests/B5ContractTests.cs`

## Минимальные правки: 5 файлов

- `iCemDataMonitor.Host.Tests/SnifferWitnessSurfaceGuardTests.cs`
- `iCemDataMonitor.Transport.Tests/ReplayAceFrameSource.cs`
- `iCemDataMonitor.Transport.Tests/iCemDataMonitor.Transport.Tests.csproj`
- `iCemDataMonitor.Transport/IAceFrameSource.cs`
- `iCemDataMonitor.Transport/iCemDataMonitor.Transport.csproj`

## Current-state override

21 dormant route из pinned `rust/contracts/http/route-inventory.json` имеет
текущий статус **RETIRED**, а не remaining migration. Четыре active REST GET
(`/api/status`, `/api/status/summary`, `/api/regs`, `/api/regs/history`) и OpenAPI
не менялись. Старый inventory описывает commit `96e2206` и сохраняется как evidence.

Удалены SerialTeeService/Com0ComMonitor и только их эксклюзивные dependencies/tests.
**Драйвер com0com KEEP**: независимый emulator использует внешнюю пару COM-портов
без этих wrappers. Npcap/ACE sniffer и standalone COM/LAN emulator сохранены.
AddIn остаётся единственным MMF writer. Legacy assemblies остаются reference/build
compatibility, Rust не объявлен production cutover.

Capture-to-file internals в AceSnifferService этим удалением автоматически не
снимались: удаление REST capture wrappers не доказывает удаление внутренних методов.
Frozen proto/SPA/assets/source-manifest/desktop inventory не менялись.

## Текущий статус бывших dormant routes

| Method | Route | Current status |
|---|---|---|
| GET | `/api/sniffer/status` | RETIRED C11 |
| GET | `/api/sniffer/service-vectors` | RETIRED C11 |
| GET | `/api/sniffer/signal-preview` | RETIRED C11 |
| POST | `/api/sniffer/control` | RETIRED C11 |
| POST | `/api/sniffer/capture` | RETIRED C11 |
| GET | `/api/sniffer/capture/status` | RETIRED C11 |
| GET | `/api/sniffer/capture/download` | RETIRED C11 |
| GET | `/api/tee/status` | RETIRED C11 |
| POST | `/api/tee/control` | RETIRED C11 |
| GET | `/api/sources` | RETIRED C11 |
| GET | `/api/data` | RETIRED C11 |
| GET | `/api/com0com/status` | RETIRED C11 |
| POST | `/api/com0com/open-config` | RETIRED C11 |
| POST | `/api/com0com/refresh` | RETIRED C11 |
| GET | `/api/templates/files` | RETIRED C11 |
| GET | `/api/templates/detail` | RETIRED C11 |
| POST | `/api/templates/save` | RETIRED C11 |
| POST | `/api/templates/delete` | RETIRED C11 |
| GET | `/api/modbus/preview` | RETIRED C11 |
| GET | `/api/registry` | RETIRED C11 |
| GET | `/api/registry/mapping` | RETIRED C11 |

## Проверки и границы evidence

Code-agent: 46 scoped CI tests PASS; это не full Windows/exact-final-tree result.
Документационный проход проверяет relative Markdown links, JSON parse и
`git diff --check`; повтор полного runtime suite для prose-only edits не заявляется.
Предыдущий N+1 run40253303600 SUCCESS относится к предыдущему head;
на момент локального docs checkpoint Windows CI финального C11 дерева ещё
не подтверждён. Итоговый verdict публикуется в PR44 после обязательных jobs.
Software implementation + N+1 v1.7 завершены; hardware/operator/UI/SDK/release
acceptance остаётся отдельной проверкой, а не выдуманным coding backlog.

## История

[C1](LEGACY_REFACTORING_C1.ru.md), [C2](validation/LEGACY_C2.md),
[C3](LEGACY_REFACTORING_C3.ru.md), [C4](LEGACY_REFACTORING_C4.ru.md),
[C5](LEGACY_REFACTORING_C5.ru.md), [C6](LEGACY_REFACTORING_C6.ru.md),
[C7](LEGACY_REFACTORING_C7.ru.md), [C8](LEGACY_REFACTORING_C8.ru.md),
[C9](LEGACY_REFACTORING_C9.ru.md), [C10](LEGACY_REFACTORING_C10.ru.md).
Факты прошлых failures и approvals сохраняются на своих checkpoints; прежние
упоминания будущих задач не отменяют текущий software status.
