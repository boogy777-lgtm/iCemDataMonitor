# Эмулятор E2 — фиксированная DFB-сеть

Исторический отчёт указанного инкремента. Текущее software-состояние:
[N+1 tracker](NPLUS1_TRACKER.ru.md), [implementation status](IMPLEMENTATION_STATUS.ru.md).
Прежние NEXT/PARTIAL описывают тот checkpoint; hardware/UI/operator/release gates
остаются отдельными. [C11](../../docs/LEGACY_REFACTORING_C11.ru.md) удалил dormant
REST/serial wrappers и их эксклюзивные tests; com0com driver сохранён.

Rust 1.99.1 / Edition 2024. Перенесена CementUnitNetworkFactory из baseline
96e2206562c9389d1543095232115d81eae4b692. Это вычислительный инкремент,
а не полный emulator M5 и не произвольный IEC 61131-3 runtime.

Class: семь блоков цементировочной фабрики и их wire map в
`crates/icem-emulation/src/cement/`; динамическая сборка произвольного графа вне класса.
Inv: исходные типы/имена/проводка/порядок/RNG и state transitions проверяет C# oracle.
N+1: typed block + port declaration + map accessor; source inventory test выявляет пропуск.
Core/host не знают о блоках. Новых зависимостей и unsafe нет.

## Поведение

Порядок строго PumpA → PumpB → Mixer → F300Hpvt → RawIo → Temp → Sequencer.
Это фактический порядок factory Add (D20); топологическая пересортировка не добавлена.
Один Context владеет одним seeded RNG на tick. Seed смешивается с младшими 31 битами
signed tick с unchecked i32 arithmetic. SeededRandom воспроизводит seeded
System.Random.NextDouble; это не криптографический генератор.

Источники алгоритма RNG: исходный DfbContext.cs и
[dotnet/runtime v10.0.0 Random.CompatImpl.cs](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Random.CompatImpl.cs).
MIT notice для порта включён в cement/random.rs; общая лицензия проекта не изменена.

245 зарегистрированных портов, 18 параметров, 325 wire-имён:

- PumpSide: lag → шум → OPKO latch → totalizer. Kickout не выключает расход.
- Mixer: density/pot lag, valve clamp, flow/noise, stage/job totals и sacks.
- F300Hpvt: F300/HPVT, 12 water-test rates, observer/pump-life constants,
  6 blends и status flags; FlowRef читает текущий выход Mixer.
- RawIo: analog noise/counters; digital outputs обнуляются на tick, spare slots удерживаются.
- Temperature: исходные два lag с общей случайной поправкой.
- Sequencer: rising JobActive, reset, wrapping event, unit/track/control/mix states.

Network.JobActive/Network.Reset — shared storage фабрики, не registered port paths.
`signals()` сохраняет local storage и effective Read подключённого входа отдельно,
а также source path, тип и направление. Проводка фиксирована и следует одному hop.
Публичные typed inputs/parameters соответствуют изменениям `.Value` исходных объектов.
Замена C# object identity/произвольная перепроводка не являются Rust API этого инкремента.

Math.Max сохраняет NaN и signed-zero semantics. Clock, dt, tick и seed явные.
Owned state допускает независимое клонирование; I/O/threads/global state в модели нет.
Wire lookup поддерживает исходные ASCII keys без учёта ASCII case. Неизвестное имя:
try_wire_value → None, wire_value → 0. Произвольный Unicode OrdinalIgnoreCase остаётся
открытым, как и в E1; он не объявлен полным переносом .NET string comparer.

## Development EXE

```powershell
cargo run -p icem-emulator-app --bin icem-dfb-dev -- --ticks 3 --interval-ms 0
cargo run -p icem-emulator-app --bin icem-dfb-dev -- --ticks 2 --dt 0.5 --seed -2147483648 --rate-a 12 --opko-a 1000 --unit 4294967295
```

Каждая stdout строка — JSON snapshot после tick: tick/elapsed/dt/seed, все signals,
parameters и wire. Для double есть numeric value и lossless hex IEEE bits;
при NaN/Infinity numeric value=null, bits сохраняют значение. Диагностика → stderr.

Опции: --ticks 1..3600, --interval-ms 0..60000, --seed i32, --dt finite (0,3600],
--job-active true|false, --reset true|false, --unit u32, --rate-a/--rate-b,
--opko-a/--opko-b, --water-valve (конечные f64). Defaults: tick=1, interval=1000,
seed=7, dt=1, штатные уставки фабрики. Usage error=2, write failure=1, success=0.
Это диагностический контракт. Production --cli/21 commands/control pipe не подменены.
EXE не открывает serial/Npcap, не запускает host и не создаёт UI/tray.
E1 icem-emulator-dev/FLECS работает отдельно и сохраняет контракт.

## Проверки

`tests/network_dotnet.rs` на Windows компилирует **все неизменённые** исходные
Dfb/*.cs и Blocks/*.cs через PowerShell Add-Type. Oracle отражает порты и параметры,
внутренний key set DfbSignalSource и исполняет исходную фабрику/блоки/RNG.
Rust тест не генерирует expected values своей реализацией.

- 7 seeds × 512 NextDouble, 49 seed/tick contexts × 64 draws: 6 720 exact-bit draws.
- 7 trajectories × (initial state + 32 steps): 231 frames, 224 network ticks.
- Изменение rates/OPKO/valves/parameters/unit, reset и job edges, connected input
  shadow storage, retained spares, dt=0/negative/fractional и signed tick wrap.
- 40 fresh-network edge frames: dt/water/blend/elapsed/pump c ±0, negative,
  NaN, ±Infinity и f64::MAX; отдельный bitfield probe с ненулевыми digital/relay.
- В каждом frame: полный набор и типы портов/параметров/проводки + все wire values.
- Sentinel после execute доказывает число и порядок потреблённых RNG draws.

RNG и sentinel сравниваются побитово; model floats: 1e-12 relative/absolute floor1,
signed zero/Infinity exact, NaN classification. Windows report:
`target/dfb-differential-report.json`, artifact emulator-differential.
Cross-platform tests отдельно проверяют latch/totals/reset/seq, factory wiring,
case lookup, spare retention, clone/replay и реальный dev EXE/exit codes.

Проверен code commit `e0b320d29a8eda9fb3ea23d7378325efde9e3376`:
[Actions 38964177178](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/38964177178)
— **success**. Windows: 57 tests + compile-fail doctest; Linux: 47 tests + Chrome
regression. Original C# oracle .NET 10.0.12: все перечисленные случаи PASS.
Получен и прочитан artifact emulator-differential (12074601536), оба отчёта PASS.
Baseline 513/513 на обеих ОС, original .NET изменений 0. E1, web/Modbus/MMF regressions
сохранены. Машиночитаемые результаты: migration/validation-emulator-e2.json.

## Остаток emulator

Следующий инкремент: LAN base/DFB provider composition, firmware SV, freeze → manual
overrides → units → encoding. Потом hardware emitters/21 commands/XML/IPC и полная
GPUI-реализация исходного Mac/Apple UI. Трей идёт после emulator, как указал пользователь.
Full UI/hardware parity, production package и уменьшение полного размера пока не доказаны.
