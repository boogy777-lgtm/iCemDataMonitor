# ADR 0017 — типизированные ответы emulator и derived presentation

Class: payload всех 21 операторских команд `control::{payload,application}`; Host
RPC и legacy grammar не входят в класс. Inv: `EmuControl` возвращает конкретный
`ControlResult<T>` каждой операции; private fields/mandatory success data запрещают
success+error и failure+data, compile-fail tests проверяют границу. N+1: тип операции,
единственный execute match, v1 projection и acceptance case; Worker не знает команд.

Class: derived operator state `icem-desktop::emulator`, не transport JSON. Inv:
request name → Execution → Payload family проверяется до публикации; exhaustive
presentation match и 21-case completeness test. N+1: новая семья получает явное
отображение либо явный ACK без оптимистичного изменения snapshot; второго Engine нет.

## Решение

`Data = Map<String,Value>` удалён. Status/COM/LAN/units, overrides, profiles,
variables/endpoints и operation data представлены owned structs и закрытым Payload.
COM и LAN Signal/Get/Start варианты не допускают LAN без SV или COM с LAN-полями.
Крупный Status хранится в Box внутри Payload, чтобы bounded reply queue не несла
472-byte largest variant в каждом элементе. Новых зависимостей/features/pins нет.

Единственный application execute вызывает прежний owner и отображает конкретный
backend результат через map_data. ControlResult имеет private поля и accessor API;
ошибки сохраняют FailureEffect. Execution содержит typed payload, outcome и error;
`ui_value` удалён. Service/Engine сохраняют прежний gate/settings_revision/save path.
Сохраняются Rejected/Applied/AppliedNotPersisted/AppliedWithError/Unknown, partial
COM effects до LAN failure и закрытие старого endpoint до failed replacement.
Периодический read не повторяет save; потерянная mutation не повторяется.

GPUI Bridge передаёт Execution по прежнему Worker 16 requests/16 replies + in-flight.
Модель проверяет команду и payload family, затем строит display strings из typed
facts; mismatched reply не подтверждает connected/status/список endpoint. VariableRow
хранит Signal identity, выбор/override не разбирают обратно display hex SV. Refresh
сохраняет выбранную identity или снимает selection. Layout/palette/named controls,
176+6 mapping и E14 Drop/WorkerCompletion/same HWND lifecycle не менялись.

Status retains raw f64, включая nonfinite; finite-only NativeValue input и legacy
NaN/Infinity/fractional/bool coercions не меняются. В `control/v1.rs` прежний serializer
проецирует nonfinite в null. Display policy null/nonfinite → «—» применяется только
при построении UI; исходные IEEE bits остаются в typed data. Scalar Number formatting
и seven-scalar units summary используют прежний serializer display policy
с явным E19 Windows порядком полей (исправление D50),
без чтения или roundtrip JSON ответа. `selfTest:null` остаётся adapter constant;
нового selftest result/status предположения нет.

## Совместимость и доказательства

Pipe text/JSON v1 grammar/envelope/operation data/proto=1/watch ACK/status не меняются.
Исторический ok=true при save failure подтверждает in-memory apply, не disk durability.
Отсутствующий optional data в публичном compatibility serializer и error fallback
проверяются отдельно. CLI dispatch до engine/config/tray, JSON stdout/exits,
connect/ACK/idle/cancel bounds, singleton/exclusive pipe и Quit drain прежние.

Rust и C# synthetic Fake теперь возвращают согласованные domain-shaped fixtures,
а не {method,args} property bag. Original C# CommandProcessor/source неизменён;
сравниваются весь parsed envelope/data, ACK flags, backend method и exact IEEE args.
Это parser/adapter differential, а не доказательство legacy Engine parity. Отчёт
control-differential-report.json включён в существующий Windows CI artifact.

Независимый frozen actual Rust E19 wire golden содержит 43 frames всех 21 commands,
already-start/stop, empty LAN variables, malformed scope/missing variable,
NaN/±Infinity/−0 и status/vars; отдельно захвачен direct-backend unknown-vars data:{}. Захват: unchanged production tree E19
8b70979f512a5e0daeac3025a6d4a97d3d4199c9, PR head 400cdc6c6ef77602cae6b48584b5b95afb850486;
временный portable harness в 2ac68f53cb0f1bd4362a10cbd86e13794e0e7076 (последующий
test-only metrics fix) удалён после capture. Полное сравнение envelope/data/ACK;
единственная нормализация status.data.pid→0, explicit clock=123.25 s.

D49: удаление бывшего Model.snapshot обнаружило оставшиеся Host dashboard reads
старого Value, который apply_host больше не обновлял. Эти отображения используют
уже проверенный Model.host; unknown → «—»/false, disconnected → last-known facts
с connected=false. Поля и freshness проверяются portable regression. Dashboard REST
Value adapter остаётся отдельной E21 границей; новые REST endpoints не включаются.

Windows/MSVC/original C#/native process/pipe/mapping/MSI результаты фиксируются по
точному PR head. Portable Linux не доказывает Windows/visual/hardware acceptance.
Physical UI drafts/scroll/keyboard/focus/DPI/session, iCem/AddIn/COM/Npcap, true SDK
release/install остаются NOT_RUN. Selftest/landiag, dormant capture-to-file, D22 и
external OPC DA no-op не расширяются этим решением.


D50 / PR17: Windows run 39265963972 / job 118105477073 на c4bbc1fa16a8d341c6d8f30684375959fd519c86
обнаружил sorted BTreeMap display regression: GPUI включает serde_json/preserve_order,
поэтому прежний Value сохранял insertion order, тогда как BTreeMap сортировал keys.
Первый новый expected fixture также ошибочно ставил density перед temperature;
канон — E19 runtime.rs:324–327 на 400cdc6c6ef77602cae6b48584b5b95afb850486:
preset, flow, temperature, density, volume, level, pressure. Private Serialize
UnitsSummary задаёт этот порядок declaration order независимо от map features;
UnitStatus destructuring exhaustive, новый field требует review formatter.
Regression сравнивает literal full string (nulls, escaping, DEL/C1/Unicode) и все
семь distinct values; expected не зависит от Value/map feature unification.

Добавлен только desktop → уже locked serde 1.0.229 (MIT OR Apache-2.0, std/derive);
serde_derive и features уже были в Windows graph/lockfile. Cargo.lock меняет только
существующий desktop dependency edge, packages/versions/unsafe policy прежние.
Это derived scalar display serialization; emulator wire adapter и его golden не меняются.
