# Эмулятор — вычислительная основа и dev preview

Rust 1.99.1 / Edition 2024. Первый рабочий инкремент этапа emulator; не полный M5.
Порядок пользователя остаётся web → emulator → tray. Desktop UI должен быть GPUI
с исходными Mac/Apple custom controls, которые перечислены в desktop-ui-inventory.json.
Этот инкремент не подменяет их новым видом и не вводит UI-заглушку.

## Class / Inv / N+1

Class: template-driven COM waveform и FLECS formatter; LAN DFB/provider composition
вне класса (`icem-emulation/src/simulation.rs`). Inv: explicit clock, owned values,
manual overrides после waveform, исключённые поля не попадают на wire; unit/EXE tests
и comparison с original C# classes. N+1: новый template — данные, не ветка в core.

Class: шесть групп единиц и четыре preset (`icem-emulation/src/units.rs`); arbitrary
compound units вроде psi/mA вне класса. Inv: aliases, cross-group identity, affine
°F/°C и отдельный FirmwareDefault NeedsConversion gate; cross-product C# oracle.
N+1: новая группа требует exhaustive matches в модуле и golden cases; host/core не меняются.

Class: stateful lag/latch/totalizer (`icem-emulation/src/dfb.rs`); полная cement network
вне класса. Inv: deterministic dt/inputs, latch capture и reset order; C# trajectories.
N+1: следующий DFB — собственный блок и проверка, без универсального runtime/registry в core.

Class: ACE frame и native signal decode (`icem-protocols/src/ace.rs`); Npcap/передача
Ethernet вне класса. Inv: checked ranges, BE, fixed owned buffers, actual parser
predicate D19; byte vectors и original C# oracle. N+1: новый wire type — enum/codec/test,
exhaustive decode match проверяет полноту.

## Что работает

- Explicit template file + registry references/inline variables, family alias/index,
  sorted variables и variable_registry.emulator.json override при automatic registry path.
- COM SimulationEngine: elapsed с первого tick=1, sine/base/toggle, manual override,
  profile fallback selection, reset clock и owned last-values. Stream gates пока не подключены.
- FLECS формат: F1/F2, ties-to-even int, InvariantCulture shape, excludeFromTelemetry,
  missing values=0. Оригинальный профиль имеет 34 variables, на wire — 32 поля.
- Все unit aliases/coefficients, 4 presets, per-group overrides, target/wire conversion
  и separate NeedsConversion semantics, включая FirmwareDefault edge.
- FirstOrderLag, KickoutLatch и Totalizer. Math.Max NaN/zero semantics не заменяются
  обычным Rust max, который отличается для NaN. dt explicit, clock/RNG не скрыты.
- ACE build 152 bytes с padding/trim, parse по фактическим правилам source; native
  FF/UL/UI/HX/SI/SL decode с offset−4, factor и checked width/ranges.
- Отдельный EXE icem-emulator-dev генерирует реальные FLECS bytes в stdout с CRLF
  либо JSON trace. По умолчанию 1 Hz между заданными ticks; interval=0 ускоряет тест.
  COM port/Npcap/listener он не открывает. Host не запускает и не останавливает его.

## Запуск

Из rust/:

```powershell
cargo run -p icem-emulator-app --bin icem-emulator-dev -- --template ../iCemEmulator/templates/flecs_4.53.json --ticks 3 --set "PS Pump Press=1234.5"
cargo run -p icem-emulator-app --bin icem-emulator-dev -- --template ../iCemEmulator/templates/plc_basic.json --ticks 3 --interval-ms 0 --format json
```

--registry PATH выбирает explicit registry. --set принимает текущие Name/LegacyName
из template (например PS Pump Press), а не произвольное canonical registry name.
No args/usage → exit 2, input error → exit 1, success → 0. Diagnostics только stderr.
Это development preview API, не замена production --cli/21 commands/control pipe.
Физическое emit/start не имитируется успешным статусом.

## Сравнение с C#

Windows integration test запускает PowerShell Add-Type с неизменёнными source classes:
SimulationEngine, TemplateCatalog, SignalWireCodec, UnitConversion, AceFrameBuilder и
тремя DFB blocks. Reflection только подаёт existing registry/templates в private
catalog data seam; вычислительные алгоритмы не копируются в test oracle.

Проверяются оба исходных COM profiles, manual set/clear/reset, exact FLECS strings,
unit cross-product и settings, DFB trajectories/NaN/zero edges, formatter midpoint/
overflow cases, exact ACE bytes и numeric signal decodes. Numeric tolerance 1e-12
relative/absolute floor 1; zero sign/Infinity exact, NaN — classification. Wire equality
побайтовая. Runtime oracle должен быть .NET≥9: источник target net10 использует
saturating float→int conversions. Отчёт сохраняется как CI artifact emulator-differential.
Это test dependency; в Rust runtime нет CLR/C# или новых native dependencies.

## Следующие работы

1. E2: DFB cement composition, signal map и seeded RNG реализованы; отдельный
   [контракт и проверки E2](EMULATOR_E2.ru.md). Windows/Linux CI 38964177178 PASS (см. VALIDATION.ru.md).
2. LAN payload providers: registry/firmware SV composition, freeze → overrides → units,
   endian/format scaling; no auto-stream default. COM freeze gate тоже отдельно.
3. Actual serial 9600/8N1 и Npcap raw ACE emitters, pacing/reconnect/counters.
4. Полные 21 commands, control pipe, XML/catalog discovery/save и graceful ACK.
5. GPUI UI: все existing controls/states/themes, scrolling/edit/units и Windows/DPI
   acceptance. Потом отдельные tray/lifecycle по порядку пользователя.

Ограничения E1: нет XML/full catalog discovery/fallback logging, произвольная Unicode
OrdinalIgnoreCase и malformed DTO cases ещё не differential-characterized; duplicate
index ordering не заявлен идентичным. Полная фиксированная DFB-фабрика добавлена в E2; нет полного engine, freeze/emitter/IPC/GPUI/tray.
Полных parity PASS и сравнения полного размера этот этап не присваивает.

Dependency review: добавлен только прямой доступ к уже зафиксированному serde_json
1.0.154 (MIT OR Apache-2.0, default std features) для template DTO и dev trace; новых
external versions/native bindings нет. Handwritten modules сохраняют forbid(unsafe_code).
External serde implementation остаётся частью dependency trust boundary, не formal audit.
D21 — checked reject malformed signal spans вместо возможного C# overflow exception.

## Проверенный результат E1

Code commit e43ff8314e7c7f48cfbdc9389d90655986049a05, Actions 38946224137 success:
Windows 51 tests + doctest, Linux 42 tests + Chrome regression. C# oracle .NET 10.0.12:
9 801 unit cases, 132 settings, 32 DFB steps/24 edges, 39 formatter cases, 5 ACE frames,
180 signal decodes и оба source templates. Подробности: VALIDATION.ru.md и
migration/validation-emulator-e1.json. Полных P19/P23/P24/P25/P26 PASS по-прежнему нет.

## Проверенный результат E2

Полная фиксированная cement factory, 245 ports / 18 parameters / 325 wire names:
[контракт E2](EMULATOR_E2.ru.md). Code e0b320d, Windows 57 tests + doctest,
Linux 47 tests + Chrome; original C# factory/RNG/map oracle .NET 10.0.12 PASS.
Подтверждённые данные: migration/validation-emulator-e2.json. Следующая работа —
LAN provider/freeze/override/units composition; M5 в целом ещё открыт.

## E3 — LAN providers

Добавлены base/catalog/codec и полная LAN provider composition с frozen cache,
manual native overrides и units; [контракт E3](EMULATOR_E3.ru.md).
Code 441de1a, Windows/Linux CI 38968834214 PASS: 72 Windows tests + doctest,
61 Linux tests + Chrome. Original C# LAN oracle .NET 10.0.12 сравнил полные payload
побайтово, все cases PASS. Artifact получен; данные migration/validation-emulator-e3.json.
Hardware/IPC/GPUI/tray остаются отдельными этапами.


E17: [типизированный application API emulator и общий watch client](EMULATOR_E17.ru.md),
[ADR 0014](adr/0014-emulator-application-api.ru.md). GPUI передаёт Command напрямую;
pipe v1 остаётся compatibility adapter, включая coercions и исторический save ACK.
Внутренний результат отделяет apply/save failure; CLI text/JSON watch получает поток,
а не только ACK. Software CI не закрывает operator/SDK/hardware gates.


E20: [типизированные ответы emulator](EMULATOR_E20.ru.md),
[ADR0017](adr/0017-typed-emulator-payloads.ru.md): 21 concrete backend payload families,
private ControlResult, typed Execution → derived GPUI View/Signal identity; JSON только
на v1 adapter/display formatting, без response roundtrip. Полные 43 E19 wire frames,
parser C# domain-shaped fixtures и mismatch/nonfinite/partial/save-failure checks.
D49: dashboard status отображает уже typed Model.host, сохраняя last-known при fault.
Dashboard REST/selftest/landiag и visual/operator/SDK/hardware gates остаются открытыми.
