# Desktop / Host — native GPUI increment E6

Решение: desktop — **нативный GPUI 0.2.2**, tray — **gpui-tray 0.1.4**.
WRY/WebView2 удалён из Rust desktop и Cargo.lock. Браузерная SPA остаётся отдельным
web-интерфейсом, её HTML/CSS/JS не изменялись в E6. Исходные .NET проекты и AddIn сохранены.
Rust 1.99.1, Edition 2024. Это development EXE, не production/MSI replacement.

## Нативная панель

`icem-desktop::{monitor,dashboard,dashboard_view}` отображает System/COM/LAN/RAW,
сворачиваемые разделы, таблицу выбранных переменных под графиком, history plot
с исходной палитрой рядов, легендой, временной/числовой шкалой и состояниями ошибок.
Регистры/история читаются через существующие `/api/regs` и `/api/regs/history`:
Unix ISO value, enum labels, имена, liveness и stale projection сохраняются у Host.
Web-off не препятствует чтению ops-регистров; история честно показывает HTTP 503.
Выбор хранится по адресу в per-user `LOCALAPPDATA/iCemDataMonitor/native-dashboard-selection.json`;
при загрузке принимаются только чётные адреса 4..598, файл ограничен 4096 bytes.
История ограничена последними 600 точками на ряд; ошибки удерживают last-known данные.

Внутренний Status панели содержит карточки и реальный `SubscribeMetrics` по Host pipe:
полный первый sample, затем deltas; сохранён возраст обновления каждого адреса,
int32/float distinction, heartbeat и interval_clamped. После ошибки поток создаётся заново,
старые данные не выдаются за свежие. Длинное отсутствие просмотра может дать buffered
sample с прежним timestamp — возраст виден, backdated sample не маскируется.

I/O выполняет существующий Bridge worker, очередь bounded. Poll status/prefs/dashboard/metrics
имеет максимум один outstanding запрос каждого вида. Read-only HTTP ограничен loopback,
3 s deadline на запрос и 16 MiB body. Для истории это максимум два последовательных запроса.
Управление проходит по original local gRPC. Мутации не ретраятся.
Навигация/кнопки/checkbox имеют focus, Tab и Enter/Space; переключатели сохраняют pill geometry.
Добавлены независимые scroll areas и масштаб текста/графика 80–200%. GPUI использует logical pixels.
Это реализация, **не** доказанная UI/DPI/keyboard/visual parity.

COM/LAN/Units эмулятора сохраняют E5 controls, отдельные drafts, selection по name+SV,
профиль с deviceType, оборудования/firmware/ports/adapters/units. Непроверенные состояния
этих контролов не объявляются завершёнными за счёт готовности мониторной панели.

## D24: LAN worker

Production dev-engine использует `QueuedAceLanEmitter`: один owning worker, один in-flight
superframe, bounded control queue. Engine::tick только передаёт owned vectors, не ждёт
938/885 ms pacing под engine mutex. Worker обслуживает запросы во время ожидания;
close/reopen отменяют остаток burst, ACK close предшествует возврату в engine.
Drop disconnect будит worker и joins до освобождения. Считаются только успешные send.

Новый явный resource delta D26: максимум 4096 vectors (по 124 bytes payload); очередной tick
при незавершённом burst возвращает busy вместо накопления backlog. Обычный pinned
superframe помещается в 1 s; перегрузка/длительный Npcap send требует аппаратного измерения.
Блокирующий системный driver call не прерывается Rust queue; тайминги на стенде открыты.
Синхронный AceLanEmitter сохранён для deterministic codec/pacing tests.

## Запуск на Windows из rust/

```powershell
cargo build --workspace --locked
.\target\debug\icem-host-dev.exe --modbus-fixture .\tests\fixtures\bus-v5-minimal.bin --bind 127.0.0.1:1502 --web-bind 127.0.0.1:8090 --rpc-pipe iCemDataMonitor.Host --tray-icon .\assets\iCemDataMonitor.ico --duration-ms 0
.\target\debug\icem-shell-dev.exe --pipe iCemDataMonitor.Host --web-url http://127.0.0.1:8090
.\target\debug\icem-emulator-engine-dev.exe --config .\emule-dev.xml --templates ..\iCemEmulator\templates --desktop
```

Fixture содержит codec NaN; для REST/browser demo используйте finite fixture как в browser
harness (обнулить bytes 84..87). Реальная шина/данные никогда не переписываются для UI.
`--web-url` у native shell задаёт endpoint данных; страницы в нём не открываются.
`--desktop` opt-in, отсутствующий XML — all-off defaults. Host default duration остаётся 10 s.

## Проверки и открытая приёмка

Локально: Linux workspace tests **94**, check/Clippy; Windows GNU all-targets Clippy.
Добавлены модельные тесты selection/history/metrics/error retention, actual HTTP adapter
и LAN worker cancellation/counters. Exact-head Windows MSVC/CI результаты смотрите в PR/Actions;
локальный cross-Clippy не является Windows runtime acceptance.
Mapping содержит все **176** frozen named controls, visual_parity **NOT_PROVEN**.
Guard запрещает browser dependency/renderer в desktop и сохраняет inventory completeness.
Rust/.NET генерируют protobuf из одного неизменённого original source; второй .proto удалён.

Остаются: Host sniffer, Web/OPC owner integration с решением по baseline persist-only,
startup replay/boot/splash/NavigateUi/production CLI/ensure; полные control states,
console close policy, installer/payload size и аппаратная приёмка. Shell закрывается по X,
Host tray запускает новый shell; emulator X сохраняет engine, tray Open возвращает view.
COM/Npcap, MMF/ACL/elevation, Explorer restart, MSVC UI, DPI и latency/jitter **NOT RUN** локально.
См. [приёмку](HARDWARE_ACCEPTANCE.ru.md) и [ADR E6](adr/0005-native-dashboard-lan-worker.ru.md).
