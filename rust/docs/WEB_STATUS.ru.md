# Web development-инкремент

Порядок пользователя: **web server → emulator → tray**. Desktop backend — GPUI.
Web frontend выбран прежний HTML/CSS/JS: 4 dashboard assets неизменны; в index.html
исправлены только инверсия темы D16 и разбор trailer D17. Assets embedded в Rust EXE. Ни controls, ни CSS, ни chart renderer, ни SVG иконки не перерисовываются.

## Реализовано

- Axum HTTP/1.1 + HTTP/2 listener, synchronous bind/readiness, stop/drop/rebind.
- Четыре frozen REST GET routes, system scalars, COM/LAN/RAW pairs, labels,
  timestamp ISO, metadata/liveness, registry names и nullable/stale fields.
- History: 600 samples на каждый even addr 4..598, immediate/1 Hz, FIFO,
  repeatable addr query, validation, 503 disabled до 400 validation, stale skip.
- gRPC-Web binary/text через tonic-web: GetStatus, GetRegisters, SubscribeMetrics,
  GetUiPrefs, SetUiTheme. Native TCP HTTP/2 gRPC для тех же реализованных методов.
  GetRegisters/metrics читают bus sensor-at-call через bounded channel к одному
  source owner thread, не пересылая native handles.
- Per-stream first full + deltas, int32/float oneof, min interval 200 ms,
  capacity-1 DropOldest как baseline, cancellation и UNAVAILABLE при закрытой bus.
- Theme: file truth at call time, best-effort write + re-read и Windows poke
  существующего shell event. Ошибка записи не подменяется desired-state ответом.
- web_on=0: нет SPA/history/gRPC-Web, остаются REST ops и native gRPC.
  Неизвестные API не превращаются в index.html; dormant APIs не активируются.

## Запуск development host

Из rust/, после cargo fetch --locked:

```powershell
cargo run -p icem-host-app --bin icem-host-dev -- --station-config "C:\path\config.ini" --bind 127.0.0.1:1502 --duration-ms 30000
cargo run -p icem-host-app --bin icem-host-dev -- --bus-live "C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat" --web-bind 127.0.0.1:8090 --registry "C:\path\templates\variable_registry.json" --duration-ms 30000
```

Station config создаёт HTTP listener на web_port даже при web_on=0; non-loopback
разрешён только при web_remote_on=1. Для fixture/live без config web listener включается
явным --web-bind; --bind управляет Modbus отдельно. --theme-file переопределяет
канонический %ProgramData%/Halliburton/iCem/DataMonitoring/ui-theme для тестов.
--registry читает existing файл; по умолчанию templates/variable_registry.json возле EXE,
при отсутствии имена null и diagnostics в stderr, как baseline degradation.

Codec fixture содержит NaN намеренно. Для JSON/UI теста harness заменяет только эту
пару на finite value в отдельном temp файле, не меняя frozen fixture. REST non-finite
float даёт 500, protobuf сохраняет IEEE bits; точная .NET failure-body parity ещё не доказана.

## Проверки и оставшиеся gates

HTTP/RPC tests: assets identity, web-off/native gRPC, projections/labels/timestamp,
FIFO/stale/history validation, unary oneof/theme truth, metrics full/delta/clamp/error,
non-finite behavior и bind/drop. Browser CI запускает оригинальную SPA против настоящего
EXE: 298 пар, selection/chart/history, 302 metrics, Status, light/dark/file/second-page theme.
Screenshots — CI artifact, не заявление о полном визуальном сравнении с .NET/GPUI.

Остались local Windows gRPC pipe и RPC lifecycle/layer/console/autostart/boot/navigation:
они возвращают UNIMPLEMENTED, а не fake success. Console/autostart status fields пока
не являются production truth probes. Sniffer отсутствует и показывается unavailable/off.
Нужны differential с оригинальным .NET server, реальная AddIn, нагрузка и UI review.
Host/emulator GPUI UI и оба tray не реализованы; внешний вид desktop требует отдельной
приёмки по исходным XAML/styles и screenshot/control-state matrix.

Явные development resource deltas: 64 queued source requests, 5 s request deadline,
64 metrics streams, 64 KiB protobuf request / 128 KiB response; source reads держат тот
же mutex ≤500 ms. UTC projection сейчас имеет millisecond clock granularity.
Полного parity PASS и полного size comparison этот этап не присваивает.

D16: первая точечная правка SPA — исправлена инверсия bool в inline SetUiTheme:
после bundle click визуальный light отправлял false. Теперь отправляется фактический
visual state. main.js/CSS/icons и внешний вид не меняются; original .NET index.html
не меняется. Verify-Baseline допускает ровно две замены D16/D17 и проверяет остальной HTML
целиком; Node VM regression и browser scenario проверяют обе темы.

D17: inline GetUiPrefs принимал только ASCII trailer с colon+space. Tonic отдаёт
валидный grpc-status:0. Parser теперь принимает colon с нулём/пробелами/tab, только
целый grpc-status=0; layout/styles не меняются. Node VM проверяет исходный failure
и исправленный разбор, browser — тему во второй независимой странице.

Для следующего GPUI этапа сохранён `migration/desktop-ui-inventory.json`:
5 XAML screens/dictionaries, 176 named controls и 624 scoped resource definitions.
Это inventory для сохранения Mac/Apple tokens, layout и control states, не готовый GPUI UI.

Проверенный code commit: db968757fe86f43951c4a8bd1536775e990b6565, Actions 38941562186
success (Windows/Linux + Chrome). Подробности и ограничения — VALIDATION.ru.md.

## E5: актуализация Host integration

Local named-pipe gRPC, generated client, SetConsoleVisible/SetAutostart с actual probes
и Modbus SetLayer owner toggle добавлены. Web/OPC/sniffer toggles, boot replay и
NavigateUi пока UNIMPLEMENTED. GPUI monitor/trays: [текущий статус](DESKTOP_STATUS.ru.md).
Предыдущие ограничения этого документа относятся к web-increment checkpoint.
