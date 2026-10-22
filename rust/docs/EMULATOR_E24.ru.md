# E24 — launch modes, default UI и stdin CLI

Источники: pinned `Control/ControlClient.cs`, `Control/EmulatorAppContract.cs`,
`iCemEmulator/Program.cs`. Application binary — `icem-emulator-engine-dev`;
FLECS/DFB/LAN previews не менялись. Общий `cli_request` и wire grammar сохранены.

Class: first-token dispatch (`apps/emulator/src/launch.rs`) и CLI presentation
(`cli.rs`); engine state/transport exchange вне класса. Inv: bounded UTF-8 до
отправки, один request на строку, aggregate exit и cleanup при output failure;
injected tests и process tests. N+1: command names берутся из COMMAND_NAMES,
Mode match исчерпывающий, новый mode меняет parser и dispatch test.

Первый token после trim/ASCII case fold: `--cli`, `--selftest`, `--landiag`,
`--help`/`-h`/`/?`; пустой или неизвестный первый token запускает GPUI UI.
CLI/help/diagnostic paths выполняются до engine/settings/logger/singleton/UI.
Dev `--config`, `--templates`, `--duration-ms`, `--pipe`, `--desktop` сохранены;
`--pipe NAME --cli` остаётся client entry. Неполные dev paths дают usage exit2.
Default UI использует existing `SettingsPaths::station(PROGRAMDATA,LOCALAPPDATA)`:
canonical `Halliburton/iCem/DataMonitoring/emule_config.xml`, copy-only legacy
`Halliburton/iCem/iCemEmule/settings.xml`. Templates — `<EXE>/emulator/templates`,
icon — `<EXE>/assets/iCemEmule.ico`; cwd не участвует.

Explicit command args объединяются в один request. `--cli` без args с redirected
stdin обрабатывает строки до EOF; любой command/client error сохраняет exit1,
остальные строки продолжаются. Пустой EOF даёт0 без connect; terminal stdin
даёт JSON cmd=help/exit2 со всеми COMMAND_NAMES. CR/LF/CRLF и последняя строка без
newline принимаются; CR завершает строку сразу, без ожидания следующего байта.
Raw line bounded65536bytes; normalized command≤65534UTF-8bytes. Oversize/invalid
UTF-8 дают один errorJSON/exit1 до отправки и прекращают batch. Text `quit` после
trim/case останавливает stdin после reply даже при error; JSON quit сам по себе
не останавливает REPL. Watch использует общий streaming client. Output write/flush
fault немедленно прекращает batch; второй fallbackJSON не пишется. Unknown
mutation effects не ретраятся.

Portable tests покрывают framing/EOF/empty lines/terminal/byte bounds/invalidUTF8,
aggregate errors, text-vs-JSON quit, watch, no retry и output failure. Actual process
проверяет help aliases и empty CLI. Windows `engine_pipe.rs` добавляет stdin
status→invalidpreset→mutation→status/EOF, oversize/no-mutation, textquit с оставшейся
mutation. Используется existing bounded child helper. Default/unknown UI fixture
изолирует оба station roots, seed legacy с disabled streams, staging реальных
EXE-relative catalogs и corrupt cwd catalogs; отсутствующая иконка останавливает
до GPUI и проверяет discovery/copy-only settings и освобождение singleton.
Windows runtime tests здесь не исполнялись: они обязательны в Windows CI.

Текущий Linux Rust1.99.1: workspace fmt/check/build/test/Clippy с locked/offline
PASS. Focused native/platform all-targets MSVC check и Clippy — compile-only,
не runtime. Полный Windows application/GPUI build и новые Windows process/native
tests должны пройти CI на exact head до merge.

Оставшийся кодовый backlog: D22 — malformed/partial loader behavior, полный
File.ReadAllText encoding и Unicode OrdinalIgnoreCase; E23/E24 не меняют loaders.
Отдельные acceptance gates: Windows runtime/UI appearance/lifecycle, operator,
COM/Npcap/NIC hardware и настоящий SDK/MSI release. Эти gates не объявлены PASS
portable tests или compile-only проверкой. E22/E23/E24 modes реализованы; прежние
планы «selftest/landiag/stdin CLI далее» являются историей, не current backlog.
