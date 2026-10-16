# Эмулятор E3 — LAN payload и цепочка провайдеров

Rust 1.99.1 / Edition 2024. Перенесена вычислительная LAN-цепочка из pinned
96e2206562c9389d1543095232115d81eae4b692. Ethernet/Npcap, полный production engine,
XML/IPC/21 commands, GPUI и tray этим инкрементом не объявляются готовыми.

Class: фиксированный ACE/LAN каталог и provider composition в `icem-emulation/src/lan.rs`
и `lan_pipeline.rs`; hardware emit/scheduling вне класса. Inv: checked spans, owned
frames, source precedence/order, byte-exact C# oracle и inventory completeness.
N+1: новая SV и waveform — JSON данные; core/runner не получают ветки по SV.

## Перенесённое поведение

- Registry: 208 уникальных SV и 4196 полей после merge legacy + firmware.
  Первый SV побеждает collision. 6 legacy SV в исходном порядке, затем 14 firmware;
  в активных 20 таблицах 363 поля (96 legacy + 267 firmware).
- Base: wireName/alias/name/display lookup с исходным приоритетом записей,
  `.emulator` override, затем firmware_variables; elapsed → toggle → sine → base.
  ActiveSvNumbers и equipment-independent payload сохраняют исходный контракт.
- Численные base/DFB/manual кодировщики: всегда четыре BE bytes, затем prefix-copy
  до объявленной Size. FF не использует factor, integer использует round(value×factor),
  HX — round(value). UC — invariant F0 или stringValue, ASCII/NUL padding.
- Полная цепочка: base → DFB → freeze → manual overrides → units.
  Неизвестные DFB wire names сохраняют base bytes. Проекция native_superframe
  соответствует чтению через override provider до единиц и может заполнить кеш.
- Freeze сохраняет весь исходный superframe до overrides/units. Первый frozen call
  заполняет кеш. Ручные значения и единицы применяются каждый вызов к owned copies.
  Смена firmware сразу меняет ActiveSvNumbers; frozen payload list сохраняет старую
  форму до следующего unfrozen capture. Кеш не ключуется equipment или временем.
- Override storage регистрозависим; CanTransmit ищет без учёта ASCII case. Операторский
  слой сначала разрешает каноническое имя. CanTransmit проверяет весь merged catalog,
  а не только активные SV. SetOverride хранит также неизвестные имена, но они не патчатся.
- Units: decode raw×factor → conversion → round/clamp(value÷factor), поддержка integer
  1/2/4/8 bytes, FF только 4. Это отличается от four-byte-prefix base codec.
  UC/unknown/unsupported spans остаются без изменений. FirmwareDefault gate сохранён.

LanPipeline не двигает DFB clock. Вызывающий код явно управляет Context и execute.
Owned frame/cache/snapshot не содержит ссылок в изменяемые внешние буферы.
Новых external dependencies и unsafe нет; EXE получил прямую зависимость на уже
существующий workspace icem-protocols для окончательного ACE frame codec.

## Development EXE

```powershell
cargo run -p icem-emulator-app --bin icem-lan-dev -- --templates ../iCemEmulator/templates --ticks 3 --interval-ms 0
cargo run -p icem-emulator-app --bin icem-lan-dev -- --templates ../iCemEmulator/templates --ticks 3 --interval-ms 0 --emulate true --freeze-at 1 --firmware true --equipment 0x12345678 --preset 2 --set "0x13120206:Pump A Pressure=1000"
```

--templates обязателен: существующий каталог с lantable_registry.json и
variable_registry.json. firmware_registry.json и adjacent firmware_variables.json
читаются при наличии; variable_registry.emulator.json имеет исходный приоритет.

Default: 1 tick, interval 1000 ms, equipment 0x00A86F46, firmware=false,
emulate=false (frozen), preset=1 LanNative. Это диагностическое чтение, без
открытия adapter/COM/pipe, без запуска host/UI/tray. Первый LAN tick elapsed=0,
tick=0, seed=7, dt=1. Каждый preview tick увеличивает elapsed на 1; DFB исполняется
только когда freeze выключен. --freeze-at принимает zero-based tick < ticks.

Опции: --ticks 1..3600, --interval-ms 0..60000, --equipment decimal/0x uint32,
--firmware true|false, --emulate true|false, --freeze-at tick, --preset 0..3,
--unit GROUP=UNIT (flow/temperature/density/volume/level/pressure),
--set SV:NAME=finite-value (можно несколько), --format json|hex.
Setpoint хранится в native units, потом применяется preset/group conversion.
Имена разрешаются по выбранному SV без учёта ASCII case; неизвестный signal — ошибка.

stdout JSON: tick/elapsed/equipment/frozen/activeSvNumbers/frames; каждый frame имеет
SV/name, 248 hex chars payload и 304 hex chars полного 152-byte ACE Ethernet frame.
MAC для offline preview фиксирован 02:00:00:00:00:01. --format hex печатает один полный
frame на строку. Diagnostics только stderr; usage=2, input/write failure=1, success=0.
Это отдельный dev контракт, не замена production --cli.

## Проверки и границы доказательства

- 4 base/catalog, 7 pipeline и 3 реальных EXE tests: merge/aliases/override,
  overlap order, checked bounds, prefix codec, retained cache/case/manual/units,
  active-vs-frozen firmware list, caller-owned DFB, реальные ACE headers/bytes.
- Windows `tests/lan_dotnet.rs` компилирует неизменённые C# providers/codecs/DFB.
  Reflection подаёт DTO и private state вместо install-directory constructors.
  C# registry loader/best-effort error handling и production discovery при этом
  **не исполняются**; каталог сопоставляется по всем metadata полям.
- 10 base superframes, 16 provider transitions; полный CanTransmit catalog,
  3600 cases трёх original codecs, 35 ASCII/UTF-16/truncation cases,
  4800 arbitrary-width/raw-payload unit conversions. Сравнение полных bytes exact.
- Oracle deliberately advances DFB независимо от freeze, проверяя чистый provider
  contract. Production host clock/stream pacing/async send lifecycle не доказаны.
  Реальный dev EXE отдельно проверяет emulation gate и первый tick=0.
- Новые данные не ослабляют E1/E2, Modbus/MMF/web regression tests.

Exact Windows/Linux CI и artifact `lan-differential-report.json` фиксируются по
фактическому результату. До выполнения original C# это подготовленная проверка.

Открытые границы: Rust читает UTF-8 (BOM разрешён), C# File.ReadAllText также умеет
UTF-16/32; Rust возвращает Err для повреждённого JSON, исходник подавляет ошибки и
может оставить частичный каталог; ASCII ignore-case покрывает текущие имена, полный
Unicode OrdinalIgnoreCase не заявлен. Negative/overflow spans безопасно отклоняются.
Оригинальные .NET файлы не меняются. Полная production compatibility остаётся открытой.

## Следующий этап

COM freeze/projection, XML/catalog/discovery и command/control services, затем
физические COM/Npcap emitters и их pacing/reconnect, полный GPUI UI с исходными
Mac/Apple controls. Tray/lifecycle идёт после emulator. Полный размер дистрибутива
сравнивается после равного функционального покрытия и включения всех assets/runtime.
