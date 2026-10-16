# ADR 0002 — сохранение UI, web backend и порядок переноса

Решение пользователя: web server → независимый emulator → tray. Desktop UI — GPUI;
Apple-style custom controls и вся функциональность должны сохраниться. Web frontend
остаётся исходной HTML/CSS/JS SPA с единственной логической правкой D16:
сохранение вида не требует React/Vue.
Backend — Axum 0.8.9 + Tokio + tonic/tonic-web/tonic-prost 0.14.6, prost 0.14.4,
serde_json/serde_urlencoded и chrono 0.4.45 (std only, без timezone DB).
Generated server строится из неизменённого telemetry.proto; build-only
protoc-bin-vendored 3.3.0 и tonic-prost-build не входят в runtime payload.

Class: четыре REST routes + snapshot/status/metrics/theme RPC, не emulator control
(`crates/icem-web`). Inv: URL/JSON/oneof и assets сохранены; HTTP/RPC tests и browser
CI. N+1: новый route/projection живёт в web module; core не узнаёт HTTP/JSON.
Class: синхронные запросы к одному владельцу SnapshotSource; native handles нельзя
пересылать между потоками. Inv: requests bounded, owned replies, cancel-aware queue;
main-thread read → kernel copy → unlock → reply. N+1: новый клиент использует тот же канал.
Class: existing shell-owned theme event; создание окон/нового event вне класса.
Inv: OpenEventW только EVENT_MODIFY_STATE, missing event допустим, SetEvent и RAII close;
raw handle не выходит из native boundary. N+1: новый сигнал требует отдельного review.

Feature selection: axum без form/ws/multipart/macros, только http1/http2/json/tokio;
tonic без client-channel/TLS/compression, server/router/codegen; chrono без clock/wasm.
Runtime lock включает сеть/OS/dependency unsafe; forbid в handwritten Rust не означает
безопасность всех dependencies. Tokio/Hyper используют OS I/O; tonic-web/tower — framing,
prost — protobuf. Роль/версии/licenses фиксируются dependency inventory и Cargo.lock.
MIT/Apache/BSD/Unicode inventory — сведения из manifest, не независимый legal/security audit.
Единственный handwritten native exception остаётся ADR 0001, дополнен existing-event signal.
Generator-only Clippy allow ограничен include_proto!, build.rs и handlers forbid unsafe.

Browser harness использует playwright-core 1.62.1 (Apache-2.0), pinned npm lock,
установленный CI Chrome. Это dev dependency, не поставляемый browser/runtime.
Локальный browser archive в этой среде недоступен; UI проверяется отдельным CI шагом.
GPUI будет подключён при desktop этапе с отдельным lock/dependency/Windows acceptance;
библиотека не добавляется в headless host заранее. Стандартные GPUI components не
считаются заменой исходных custom controls. Число мегабайт полного приложения пока не измерено.

D16: единственное изменение SPA — исправлена инверсия bool в inline SetUiTheme:
после bundle click визуальный light отправлял false. Теперь отправляется фактический
visual state. main.js/CSS/icons и внешний вид не меняются; original .NET index.html
не меняется. Verify-Baseline допускает ровно эту замену и проверяет остальной HTML
целиком; Node VM regression и browser scenario проверяют обе темы.
