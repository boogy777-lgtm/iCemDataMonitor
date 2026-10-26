# E22 — встроенный emulator --selftest

Исторический отчёт указанного инкремента. Текущее software-состояние:
[N+1 tracker](NPLUS1_TRACKER.ru.md), [implementation status](IMPLEMENTATION_STATUS.ru.md).
Прежние NEXT/PARTIAL описывают тот checkpoint; hardware/UI/operator/release gates
остаются отдельными. [C11](../../docs/LEGACY_REFACTORING_C11.ru.md) удалил dormant
REST/serial wrappers и их эксклюзивные tests; com0com driver сохранён.

Источники: pinned `iCemEmule.Core/SelfTest/FrameRoundTrip.cs` (T1–T20),
`CommandChannelTests.cs` (T21–T26), dispatch `iCemEmulator/Program.cs` и
`Control/EmulatorAppContract.cs`. .NET sources/source-manifest/ledger не менялись.
Launcher `Start-Emulator.ps1` запускает `icem-emulator-engine-dev.exe`; именно
`apps/emulator/src/engine.rs`, а не FLECS preview `main.rs`, получает этот режим.

Class: standalone first-argument selftest в `apps/emulator/src/selftest.rs`;
обычный UI/CLI startup вне класса. Inv: все T1–T26 по порядку, сумма отдельных
неуспешных assertions, read-only EXE-relative assets, отдельный TestCtl pipe;
проверяют unit и actual child-process tests. N+1: assertion добавляется в свой
numbered case и mapping ниже; ordered-ID/process/fixture checks выявляют пропуск.

Class: конкретный same-session native probe в `icem-transport/src/emulator_control.rs`;
обычный CLI request и application state вне класса. Inv: status → malformed →
status → watch ACK → tick на одном connection, bounded I/O и stop/join при ошибке.
N+1: новый diagnostic step меняет этот probe и mapping T26, без нового trait/manager.

## Dispatch и ресурсы

Первый argv после trim/ASCII case fold равен `--selftest`: выполнение **до** парного
parser, config requirement, singleton, logger, settings load/save, UI, COM startup
и Npcap. Остальные argv игнорируются как в legacy, включая `--pipe`/`--config`.
Обычные CLI/default-UI branches не переделаны. Новый режим не исправляет другие
legacy/default-UI различия. `--landiag` и stdin CLI — отдельный следующий E23.

Каталоги берутся только из `<EXE>/emulator/templates`, совпадающего с ролью emulator
в `installer/windows-payload.json`/release manifest. Host `<EXE>/templates` и cwd
не используются. Обязательны настоящие `lantable_registry.json`,
`firmware_registry.json`, `variable_registry.json`, `firmware_variables.json`,
`flecs_4.53.json`, `plc_basic.json`; отсутствие даёт честный FAIL, несмотря на
permissive missing-file поведение Registry::load. Existing `.emulator` override
loader semantics сохранены. Файлы только читаются; settings и logs не создаются.

Единственное обращение к настоящему serial adapter — T4 с заведомо недопустимым
`COM_NONEXISTENT_999`; native validation COM+digits отклоняет имя до CreateFile.
T10/T21–T26 используют конкретные memory endpoints с production Engine, без
SystemPacket/Npcap/сети. T5/T20 используют base-only LanPipeline: network=None,
frozen=false, LanNative. T26 bind first_pipe_instance/reject_remote_clients на
`iCemEmule.TestCtl.<PID>.<time nonce>.<counter>`; production pipe не открывается.

stdout: исходный banner, одна result-строка `Tn PASS` или `Tn FAIL` для каждого
ID, byte evidence T5/T20, детали неуспешных assertions и финальная summary.
`ALL TESTS PASSED` только при нулевой сумме; иначе `N TEST(S) FAILED`.
После явного flush передаётся полный i32 failCount в Windows process::exit;
через u8 он не проходит. Ошибка write/flush возвращает ненулевой exit.
Ошибка prerequisite данного case не останавливает следующие ID. T6/T16 при
отсутствии отдельной firmware table продолжают остальные таблицы. Linux запуск
честно сообщает unsupported FAIL для T4/T26 и не доказывает Windows runtime.

## Полная mapping actual assertions → Rust

Все строки относятся к `selftest.rs::execute` с тем же номером. Числа/допуски
взяты из executable checks, а не из комментариев. `near` — absolute tolerance;
`numeric` — `relative × max(1,abs(expected))` только для T11/T12;
`numeric_abs` — absolute для T9/T13/T18. NaN/Infinity не проходят numeric checks.
Подготовленные vectors T8/T17/T19 прогоняются через existing `convert_payload`,
без нового provider trait; owned Vec сохраняет count/presence assertions.

| ID / legacy method | Actual assertions | Rust path / fixture |
|---|---|---|
| T1 RoundTrip | parse valid ACE; eq=00A86F46, SV=13120206, все 124 index bytes равны | ace::build/parse; source MAC 00:80:2F:23:F4:44 |
| T2 RejectInvalid | отдельно rejected byte17=FF, byte0=FF, 151-byte frame | три независимых assertions над cloned ACE |
| T3 HeaderConformance | literal 28 bytes `03 00 01 00 00 20 00 80 2F 23 F4 44 00 8A 03 03 08 00 D0 0D 00 A8 6F 46 13 12 02 06`; bytes28..151 zero | independent literal header и 124-byte tail |
| T4 ComGracefulError | Open nonexistent даёт непустой error, IsOpen=false | настоящий ComSerialEmitter<SystemSerial>, Windows-only |
| T5 ManualEditReachesWire | CanTransmit pressure; pump presence/124 bytes;123.45f at0 tol1e-3; HX3 BE at16; CanTransmit UC/presence/124; ASCII1+NUL; clear pressure=clean baseline tol1e-6 | real base-only pipeline; pressure O4, HX O20; UC OPS O8size120; byte evidence для всех четырёх действий; NUL проверяется на всём tail |
| T6 FirmwareRegistryLoads | все14 SV существуют/непусты, каждый O>=4 и (O−4)+size<=124; legacy6 retained | independent literal SV arrays, Registry actual signals; checked span также отвергает negative size |
| T7 UnitConversion | FW flow1→42 tol1e-9,temp212→100 tol1e-9;Metric ppg1→.119826 tol1e-6,ft1→.3048 tol1e-9,psi1→.0689476 tol1e-6;FW inverse42→1;gpm3 identity; groups bbl/min Flow,°F Temperature,psi Pressure,char None | Settings::to_target/to_wire/group_of |
| T8 SuperframeFirmwareSvs | default count6/order/all124;full count20=legacy6+firmware14/order/all124;prepared single mix vector flow1 at4→FW42 tol1e-4 /Metric.158987 tol1e-6; count1 each | BaseProvider superframe, independent expected SV arrays; convert_payload prepared bytes |
| T9 UnitSettingsOverrides | Metric Flow m³/min/Pressure bar;Flow override gpm;convert1bbl/min→42 and212°F→100 tol1e-9;LanNative3 identity tol1e-9;Metric1→.158987 tol1e-6;psi5 identity tol1e-9; mA/bpm/char None,m Level,psi Pressure | Settings + numeric_abs + groups |
| T10 ComConversion | synthetic one-variable Flow Test/gpm/base1;Metric .00378541 tol1e-8;FW1 tol1e-12 | production runtime Engine tick, memory COM emitted line and actual status value; stronger path than legacy local RunTick loop |
| T11 ConvertMatrix | все24 numeric rows: Flow6,Volume4,Density4,Level2,Pressure4,Temperature4; source relative/absolute-floor/exact tolerances | NUMERIC_MATRIX, полный состав ниже |
| T12 AliasAndCase | GPM Flow,PPg Density,PSI/KPA Pressure,` degf ` Temperature;7 numeric checks gpm inverse,lb/gal→g/cc,g/cm3→ppg,kg/m3→ppg,m3→gal,psia→bar,degf=f | groups + numeric, исходные tolerances |
| T13 NonConvertible | None для mA/none/char/%/Hz/bpm/sacks/f3/sack/gal/sack/psi/ma/ft/min/empty/mA; m Level,m3 Volume,m3/min Flow; convert5 mA→gpm,bpm→gpm,null→°c identity tol1e-12 | groups + numeric_abs; Rust nonnullable unit API проецирует legacy null fixture в empty string, без новой nullable domain API |
| T14 RoundTrip | 6 native values (bbl/min3,°f98.6,ppg15.8,bbl120,ft6.5,psi2500) ×4preset;relative error<1e-9; −40 exact F→C→F;98.6 tol1e-9 | Settings::to_target/to_wire и convert |
| T15 PresetMatrix | все4×6 expected labels; каждый UnitsOf относится к своей группе;Metric Flow override gpm,Pressure bar | display_unit — единственный Rust API для двух эквивалентных .NET PresetUnit/DisplayUnit; таблица ниже; без искусственного alias |
| T16 FirmwareOneToOne | каждый signal всех14 actual firmware tables NeedsConversion=false FW;legacy bbl/min иf true;lb/gal иg/cc false | Settings::needs_conversion; все267 текущих signals, count evidence, без устаревшего~259 и без запрета нового signal |
| T17 ProviderByteLevel | mix FW flow42 tol1e-4/density15.8f exact;Metric flow.158987/density15.8f*.119826f tol.001;OPS120 UC bytes AB не тронуты | prepared payload + convert_payload; UC проверяется для обоих presets |
| T18 CrossGroupIdentity | (1°F→gpm),(212°F→bbl),(1bbl/min→psi),(1ppg→ft),(1ft→kpa),(1psi→°c) identity tol1e-12 | numeric_abs все6 исходных cases |
| T19 FirmwareNativeGccRegression | g/cc NeedsConversion=false;actual SV1312021B и named MB_DensHPVT присутствуют;готовый payload с1.19f at actual O−4;count1/output exact1.19f | actual catalog definition/span + convert_payload; дополнительно actual uom g/cc |
| T20 OffsetLaw | pressure123.45f at0 tol1e-3/pump124;neighbor4..7=base;CanTransmit UC O8size120 span4..124;UnitNumber11038534 at0 BE | base-only real pipeline, bytes evidence; не DFB-backed runtime |
| T21 Parse | text status/whitespace/JSON;quoted set5000 applied tol1e-9;bare multi-token123.5 applied tol1e-9;8malformed inputs error/no throw;lan.prefix routes LAN | production process_line + memory Engine; explicit com./lan. gets observe applied values and COM isolation вместо legacy LastChannel fake counter |
| T22 ErrorHandling |9cases: frobnicate,start,start foo,stop udp,get unknown,set unknown,profile unknown,JSON value abc,JSON sv zz;!ok и error key | production parser + memory Engine |
| T23 SetGetRoundtrip | set5000/get/clear/get-base100;отдельный настоящий SimulationEngine current actual template nonelapsed/nontoggle variable override777.25 tick1 tol1e-9;clear tick2=compute(var,2) tol1e-9;LastValues length | production memory Engine вместо FakeEmuControl; get явно com. потому что настоящий catalog имеет одноимённый LAN signal; simulation::load_catalog/Engine/compute отдельно, не synthetic substitute |
| T24 StartStopIdempotence |COM start2→1effective transition/stop2→1;LANstart/stop1/1;все replies ok | production Engine + MemoryCom/MemoryLan counts only closed↔open transitions, не каждый close() call |
| T25 StatusJsonShape | ok true;required uptimeSec/com/lan/units/overrides/selfTest;COM streaming/portOpen/port/baud/linesSent/linesPerSec/template/values;LAN streaming/adapterOpen/framesSent/framesPerSec/equipment/svCount/targets | parsed production status wire JSON; required selfTest присутствует и null; status contract не меняется |
| T26 PipeEndToEnd |unique native TestCtl;**same connection** status ok→`{{{broken` !ok+error→status alive→watch ACK ok→1Hz status tick с uptimeSec | transport::selftest_probe + production ControlServer/Executor/process_line/line framing;connect/read<=5s,tick<=4s,write<=3s,stop/join во всех Result paths; source вопреки comment не посылает quit, Rust тоже |

### T11 — полный numeric matrix

Каждая строка: value/from/to/expected/tolerance-policy. `r` означает relative ×
max(1,abs(expected)); `0` означает точное равенство.

| value | from | to | expected | tolerance |
|---|---|---|---|---|
|1|bbl/min|gpm|42|r1e-9|
|42|gpm|bbl/min|1|r1e-9|
|1|bbl/min|m3/min|.158987|r1e-5|
|1|gpm|m3/min|.00378541|r1e-5|
|1|m3/min|gpm|264.172|r1e-5|
|1|m3/min|bbl/min|6.28981|r1e-5|
|1|bbl|gal|42|r1e-9|
|1|gal|bbl|1/42|r1e-9|
|1|bbl|m3|.158987|r1e-5|
|1|gal|m3|.00378541|r1e-5|
|1|ppg|g/cc|.119826|r1e-5|
|1|g/cc|ppg|8.3454|r1e-5|
|1|ppg|kg/m3|119.826|r1e-5|
|1|g/cc|kg/m3|1000|r1e-9|
|1|ft|m|.3048|r1e-9|
|1|m|ft|3.28084|r1e-5|
|1|psi|bar|.0689476|r1e-5|
|1|psi|kpa|6.89476|r1e-5|
|1|psi|mpa|.00689476|r1e-5|
|1|bar|kpa|100|r1e-9|
|212|°f|°c|100|r1e-9|
|100|°c|°f|212|r1e-9|
|−40|°f|°c|−40|0|
|32|°f|°c|0|0|

### T15 — все24 labels

| Preset | Flow | Temperature | Density | Volume | Level | Pressure |
|---|---|---|---|---|---|---|
|FirmwareDefault|gpm|°C|ppg|bbl|ft|psi|
|LanNative|bbl/min|°F|lb/gal|bbl|ft|psi|
|Metric|m³/min|°C|g/cc|m³|m|bar|
|Imperial|bbl/min|°F|ppg|bbl|ft|psi|

## Regression и границы приёмки

`apps/emulator/tests/selftest.rs` запускается обычным Windows cargo test:
relocated executable + копия всех реальных emulator assets, Unicode/space path,
unrelated cwd, read-only assets, sandbox LOCALAPPDATA, ignored suffix flags;
ID1..26 ровно по одному/order/banner/evidence/summary/exit; snapshot files до/после.
Missing firmware с копией только в Host templates обязан FAIL. Изменение всех267
firmware uom на bbl/min в disposable fixture даёт настоящий process exit>255,
равный summary; T26 после T16 всё ещё выполняется. Empty actual templates дают
late T23 FAIL с последующими T24/T26 PASS. Unit checks проверяют broken output
и count267 без byte truncation. `engine_pipe.rs` выполняет selftest при уже живом
engine и удерживаемом singleton, сравнивает settings bytes, files, стабильные
endpoint fields/counters и проверяет, что production pipe остаётся живым.
`Test-WindowsRelease.ps1` запускает extracted EXE --selftest до settings startup.

Локальный Linux proof не является Windows runtime evidence. Exact-head Windows CI
должен выполнить новые process/native tests и прежние baseline/source/desktop guards.
Настоящий SDK release/MSI, operator, COM/Npcap и hardware gates остаются OPEN;
D22 malformed-loader/encoding/Unicode parity остаётся OPEN. Тесты не активируют
LAN diagnostics и не закрывают E23 `--landiag`/stdin CLI.

Локальная проверка реализации (Rust 1.99.1, Linux): `cargo fmt --all --check`,
`cargo check --workspace --all-targets --locked --offline`,
`cargo build --workspace --locked --offline`,
`cargo test --workspace --locked --offline` — 198 tests PASS (включая 4 новых
portable tests; Windows-only tests здесь не выполнялись),
`cargo clippy --workspace --all-targets --locked --offline -- -D warnings` PASS.
`icem-transport --all-targets` check/clippy с target x86_64-pc-windows-msvc PASS;
supplemental harness компилирует точные selftest module и Windows process-test
sources с настоящими deps, исключая только desktop GPUI — check/clippy PASS.
Это compile-only evidence, не full Windows app build и не native runtime PASS.
Actual child closed-stdout regression использует disposable cmd stdin handshake:
parent закрывает stdout reader **до** отправки `go`, и только затем script запускает
настоящий EXE. Проверяются exact exit1 и Rust diagnostic
`selftest output/setup failed:`, поэтому ошибка самого cmd не подменяет результат.
Timeout/error cleanup завершает только owned shell PID tree через bounded taskkill,
затем join stderr reader; production code/test flags не добавлены.
