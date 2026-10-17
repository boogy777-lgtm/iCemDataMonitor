# ADR 0005 — нативная панель и владелец LAN burst

Class: monitor registers/history/metrics, `icem-desktop::{dashboard,dashboard_view,monitor}`;
emulator editable controls вне класса. Inv: адресная selection, bounded 600-point histories,
last-known/error separation, metric delta update age; model + HTTP tests. N+1: следующий
раздел использует rows/plot renderer; Host projection и core остаются независимыми от GPUI.

WRY удалён; его роль из ADR 0004 отменена решением пользователя. Web SPA сохранена отдельно.
GPUI native renderer не исполняет HTML/CSS/JS. Общие внешние projections остаются у Host;
новый универсальный UI framework не создаётся. Полная visual acceptance остаётся NOT PROVEN.

Повторно используются locked dependencies: chrono 0.4.45 std (MIT/Apache-2.0) для RFC3339
history timestamp; Hyper 1.12.0 (MIT), hyper-util TokioIo (MIT), http-body-util 0.1.5 (MIT)
для существующего read-only HTTP, tonic 0.14.6 (MIT) для named-pipe metrics. Новых версий
в dependency graph нет. Application crates forbid unsafe; transport OS I/O остаётся в
upstream Tokio/Hyper. HTTP только loopback, без redirect/TLS/DNS, 3 s deadline и 16 MiB body.
Streaming protobuf values форматируются без потери NaN/Infinity в JSON null.

Class: отложенный LAN send, `icem-platform-windows::lan_worker`; COM вне класса.
Inv: одна ownership-нить PacketDevice, один pending burst, bounded control queue,
close отменяет pacing/остаток и ACK после device.close; Drop disconnect + join.
N+1: новый PacketDevice использует тот же worker; wire codec/burst_start_ms общие с sync adapter.
Тестовый pacing — seam того же цикла, не отдельная реализация отправки.

D24 устраняется в wiring dev-engine: sleep перед первым и последующими packet больше
не держит engine gate. Driver send/open остаются синхронными на своём worker; аппаратный
worst-case latency не доказан. D26 resource delta: busy superframe отклоняется, не накапливается;
cap 4096 vectors. Tests показывают interruptible waits, successful-send counters,
wire bytes, close/reopen и отсутствие backlog; не доказывают COM/Npcap timing.

Class: один source protobuf (`icem-web/build.rs`, `icem-transport`, baseline guard).
Inv: исходный `.NET Contracts/Protos/telemetry.proto` неизменён, canonical Git SHA проверяется
source manifest, ровно один *.proto; legacy ExactlyOne_ProtoFile guard не ослабляется.
N+1: следующий generator использует тот же путь. Rust snapshot duplicate удалён; protocol
bytes/tags и оригинальные C# файлы не меняются. Старые ссылки на duplicate в E4 — исторические.
