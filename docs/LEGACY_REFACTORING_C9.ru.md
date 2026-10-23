# C9: emulator и минимальные AddIn seams

Статус: восстановленный **pre-code план**, production implementation ещё не начата. Основание — фактический main после merge C8: `0b0597b6d9cecd4e8f666e36a40eb6926708345e`, tree `9f1fdda9b0c1ff251cc4ed838f4dae703cb87a97`. Ранее существовавший локальный документ утрачен при восстановлении workspace; этот документ реконструирован из прочитанного плана и read-only preflight, а не объявлен восстановлением прежнего commit. Build/test/restore при составлении не выполнялись.

Цель L12/L13: небольшие concrete helpers в существующих проектах, без новых assemblies/packages, generic framework, второго command bus и механического переноса UI.

## C9a: единственный native decoder

Class: numeric decode native ACE payload; units/base/manual encoding и monitor full-frame decoding — nonmembers. Inv: bytes + SignalDef дают прежнее значение/отказ, endian/width/signedness/factor/bounds неизменны. N+1: numeric case добавляется в один decoder.

Оставить `SignalWireCodec.TryDecodeNative`, заменить private duplicate decoder в `UnitConvertingPayloadProvider`. Не объединять encoders: base/manual integer — четыре байта, factor multiplication и отдельная HX policy; units — factor division, round/clamp и реальные widths 1/2/4/8. Сохранить offset ByteOffset−4, FF width4 без factor, unsigned UL/UI/HX и signed SI/SL, UC/unknown false. Adversarial int overflow bounds — не исправлять incidental. Проверить widths, factors, NaN/−0, границы и неподдерживаемые types.

## C9b: структурный parser Lantable

Class: structural JSON definition parsing двух sources; merge precedence — nonmember. Inv: definitions/order/defaults, malformed-row stopping point, partial results и next-file recovery прежние. N+1: новый source той же schema вызывает общий parser с собственной merge policy.

Private ParseServiceVector/ParseSignal в `LantableRegistry`; file open, catches, insertion и firmware counters/logs остаются в loaders. Parse до duplicate check, first occurrence, legacy precedence, DasFactor=1, empty defaults неизменны. Не вводить whole-file atomic validation/skip-malformed-row. Сохранить private LoadFile и `_bySv`, используемые loading oracle.

## C9c: deterministic step и runtime owner

Class: logical simulation advancement + COM/LAN preparation; scheduling/emission/ports/pipes/persistence — nonmembers. Inv: одинаковые initial state и command/step sequence дают одинаковые prepared outputs. N+1: другой runtime trigger вызывает те же preparations.

Небольшой `EmulatorStep` владеет simulation/provider chain, emulation/unit state и LAN elapsed/active-edge state. Staged `PrepareCom` → host COM send → `PrepareLan` сохраняет emission/exception ordering; не готовить LAN до COM send. Возврат COM line и concrete LAN vectors/equipment. Без I/O, timers, Task.Run, Save, Npcap probe и clock framework. SimulationEngine сохраняет accumulated elapsed +1 и прежние reflection fields.

COM: frozen seed Tick ровно один раз; fresh Tick array alias изменяется units loop (pinned oracle), frozen LastValues clone semantics прежние. COM prepare/send остаются в одном catch. LAN: reset inactive→active даже busy, busy skip без advancement, provider failure advances once, DFB только emulating и вне provider catch; порядок DFB→freeze→manual→units. Native get/vars используют override provider до units.

`EmuEngineHost` сохраняет IEmuControl + CommandProcessor как существующий command adapter; state операции обращаются к step, resource operations и persistence остаются host. Не добавлять forwarding classes ради labels. Сохранить strings/partial command behavior/Save points. Constructor не открывает ports/timer/pipe и не emits; registry/template reads/logging допустимы.

Host владеет tracked LAN task, busy и timer. Stop имеет одного owner для concurrent calls; закрывает activation, drains control pipe с quit ACK, drains timer callbacks, joins LAN task до emitter close/dispose. Все waits вне `_gate`, необходимого callbacks/commands/send completion. Не менять C8 handler-registration/drain contracts и не делать unrelated retries/rebind policy.

## C9d: Shell bootstrap seam

Class: probe/connect/spawn/wait/outcome одного bootstrap; visual rendering/session contents — nonmembers. Inv: uniqueness/viewer/retry/failure/replacement semantics прежние. N+1: новый UI trigger вызывает один coordinator.

Framework-independent `ShellBootstrapCoordinator.cs` рядом с MainWindow, exact source-linked в `iCemDataMonitor.UI.Tests` (без assembly). Concrete delegate seams probe/resolve/spawn/wait/delay и splash observation. Coordinator владеет cross-session single-flight, возвращает viewer/existing/spawned/failure/busy outcome. MainWindow сохраняет splash/fatal dialog/bindings/navigation/theme/WebView/window lifecycle, строит new VM до dispose old VM/session; ShellSession остаётся resource owner. Не переносить giant BuildViewModel.

Viewer bypass без probe/spawn; pipe authoritative. Initial probe2s, wait probe400ms/delay500ms/60 iterations (30s budget), boot replay и continuation без observed ready неизменны. Cold failure fatal, button failure retryable. Theme callback проверяет session identity; Shell close не останавливает Host. Existing EnsureHostSpawnLawTests/ShellExitPathGuardTests находятся в `E3UniquenessTests.cs`.

## C9e: AddIn preparation

Class: resolved COM/LAN pair preparation; vendor traversal и RAW arrival — nonmembers. Inv: ordered pair bytes/timestamp dispatch/diagnostics/queue counters/per-pair atomicity прежние. N+1: источник подаёт candidates существующему PollResolution, не vendor objects helper-у.

C#7.3 `PreparedPairWrite` (zone/address/low/high) + `PollPairPreparation.PrepareCom(entry)` / `PrepareLan(entry)`. COM address4 int32 UnixTimestamp raw bits CDAB; другие COM и весь LAN float32 CDAB. BusWriter.WritePreparedPair обращается к существующему WritePairWords, без нового bounds engine. Module сохраняет GatherCandidates (Value только matched names, source order/freshness), outcomes/diagnostics, WinsBySource и per-pair posts; counters increments внутри queued actions. Resolver сохраняет first convertible/badtype no claim/fresh-first/first fresh tie/all-frozen fallback. Explicit Compile Include в old csproj.

Данные пишутся всегда; COM/LAN metadata только при pair change, reopening seeds from file. RAW nonempty arrival metadata независима, один mutex/line, malformed/tail stale hold, empty no-op. Не whole-frame transaction. Existing poll liveness/edge tests должны вызывать production preparation, не её копию.

## Проверка и oracle closure

Emulator tests: default/stream/freeze/payload/diagnostics/FormatLine/command parity/in-proc/control pipe + actual step sequences, decoder/parser vectors и tracked send Stop. DFB-only determinism не заменяет actual step tests.

UI.Tests: existing E3/exit/theme + coordinator viewer/live/spawn/timeout/retry/busy/ready outcomes. Windows WinUI build/live acceptance — отдельная проверка.

AddIn: PollResolution/liveness/edge/CDAB/writer/layout/tracker/latch. Net10 linked sources не доказывают net48: через existing SDK.Tests exact helper closure компилируется Roslyn C#7.3 против Framework refs и выполняется Windows CLR4, по принципу Net48LoggingContractTests. Closure: helper, PollResolution, DataMonitorAddressMap, NameNormalizer, UnixTimestamp, BusLayout (ZoneKind/CdabCodec внутри). Vendor AddIn build/load требует proprietary Halliburton и не заявляется portable proof.

ConfigComOracle.ps1 extracts FrozenComValues + COM units loop по host text anchors: адаптировать только current source path/anchors к step, сохранить wrapper/fixtures/assertions. ComCatalog/Emulator/LanPayload closures audit, сохранить LoadFile/`_bySv` reflection. UnicodeIdentity reflection SimulationEngine/TemplateCatalog не менять incidental. Все пять C7 logger closures включают exact OwnedFileLog + StationFileLog; выполнить helpers реально. Rust production/pin/fixtures не менять, разрешены только необходимые legacy harness adaptations.

## Frozen contracts

MMF layout5/names/endian/addresses; AddIn only producer, Host readonly; int32 CDAB time4–5; header49–56 reserve; LAN/RAW independent. Modbus every-tick overwrite/same snapshot/resize/header/stale gates прежние. C2 commands/requested-persisted-observed, C6 single acquisition + expiry200+500=700ms/join-before-reader-dispose, C7 logging и C8 lifecycles неизменны. Proto/routes/CLI/pipe/SPA/assets/Rust production и immutable pin `96e2206562c9389d1543095232115d81eae4b692` не менять. Никаких incidental behavioral deltas/baseline hash updates.

Оригинальный dirty C3 `/workspace/scratch/972115899d93/iCemDataMonitor` не трогать/reset/stash/clean. SDK `/workspace/scratch/972115899d93/dotnet-sdk/dotnet`, pwsh `/workspace/scratch/972115899d93/powershell/pwsh`, cache `/workspace/scratch/972115899d93/nuget-packages`. Последовательные restore/build/test; no API/push/PR/ledger/sourcepin changes этим исполнителем.
