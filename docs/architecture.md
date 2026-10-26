# Архитектура программного проекта

Текущее software-состояние: Rust Host/Shell + независимый Rust emulator,
сохранённый vendor iCem и net48 AddIn. [Реализация N+1 v1.7](../src/rust/docs/NPLUS1_TRACKER.ru.md)
завершена; hardware/operator/visual acceptance и production cutover проверяются отдельно.

![Единственный AddIn writer, общий acquisition и независимые потребители](images/software-architecture.svg)

## Источник и владение

Физические COM/LAN станции или самостоятельный emulator подают поток в iCem.exe.
Emulator выдаёт COM FLECS через внешнюю пару com0com либо LAN ACE через Npcap.
Драйвер com0com остаётся зависимостью виртуального стенда; serial Tee и
Com0ComMonitor не требуются и удалены в [C11](LEGACY_REFACTORING_C11.ru.md).
Host никогда не порождает, не надзирает и не закрывает emulator.

AddIn `Burservis.iCem.DataMonitor.dll` (net48, in-process iCem) — единственный
writer `Global\iCemDataMonitorBus_v1` / `Data.dat`. Rust не загружается внутрь
vendor процесса. Host пассивно захватывает ACE для диагностики, decode/catalog,
tracking и counters; sniffer не является writer и не заменяет AddIn.

## Общая публикация

Один acquisition owner с периодом **200 мс** читает MMF под native mutex,
делает owned copy, освобождает mutex и декодирует layout 5 в typed snapshot.
Canonical register storage — `Box<[u16]>`; потребители разделяют immutable
`Arc<Snapshot>` и получают typed/lazy projections. Запрос Web/Modbus/OPC/MQTT
не вызывает дополнительное чтение MMF и не меняет период acquisition.
COM Time & Date (регистры 4–5), writer zone ticks и локальное acquisition time
имеют разные значения и не подменяют друг друга.

| Потребитель | Роль |
|---|---|
| Modbus TCP | FC03/04/06/16, отдельное writable зеркало; raw active/stale/hold semantics |
| Web | Сохранённая SPA, четыре REST GET, history, gRPC-Web; web-off сохраняет ops surface |
| GPUI Shell / CLI | Клиенты Host по local named pipe; Shell обеспечивает запуск Host |
| MQTT | Необязательный startup-configured state publisher QoS0/1/2 |
| OPC UA | Необязательный read-only server; ограниченный явный security profile |
| OPC DA sidecar | Отдельный Windows COM process, bounded versioned IPC и собственный lifecycle |

Протокольные частоты независимы; `modbus_tick_ms=500` не является частотой
acquisition. 200 мс не означает гарантированную end-to-end latency.

## MMF layout 5

Magic STNB, protocol 1, header 64 bytes, `N` BE u16 registers; размер `64+2N`,
по умолчанию N=10001, допустимо 650..65535. Header metadata LE, пары float32 CDAB.
`0..3` — state/heartbeat; `4..199` — COM (4–5 int32 Unix time);
`200..399` — LAN/cRIO; `400..599` — raw COM positional values;
`600..10000` — резерв ace_full, writer отсутствует. Старые layouts мигрирует AddIn.
[Точный byte/IPC/API контракт](../src/rust/docs/API_SPEC.ru.md).

## Границы текущего проекта

Runtime Rust: `icem-host-dev.exe`, `icem-shell-dev.exe`, `icem-emulator-engine-dev.exe`;
нативный desktop GPUI, browser SPA отдельно. Legacy .NET projects сохранены
как reference/build compatibility; net48 AddIn остаётся обязательным runtime.
Неизменность frozen proto/SPA/assets, pinned source manifest и desktop inventory
проверяется отдельно от согласованных legacy divergences.

[Модули Rust и безопасные границы](../src/rust/docs/ARCHITECTURE.ru.md),
[конфиг](config.md), [MSI](../src/rust/installer/README.md),
[активный OpenAPI](../src/rust/contracts/http/openapi.json).
Pinned [route inventory](../src/rust/contracts/http/route-inventory.json) описывает
базу `96e2206`, а не current source tree: 21 бывший dormant route retired C11;
четыре активных GET не изменены. Capture-to-file internals legacy
AceSnifferService не удалялись автоматически вместе с REST wrappers.
