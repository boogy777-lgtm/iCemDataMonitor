# E4 — headless integration эмулятора

Рабочая ветка: `rust/autonomous-completion`. Это development-инкремент;
production EXE не подменяется, исходные .NET/AddIn и frozen SPA/proto не меняются.

## Восстановление checkpoint

В `8989936` runtime/runtime_ports существовали на диске, но не экспортировались из
`icem-emulation`. `icem-platform-windows` ссылался на отсутствующий `transport.rs`;
`icem-desktop` — на отсутствующий `windows.rs`. Cargo.lock не соответствовал уже
добавленным в checkpoint зависимостям GPUI/WRY/XML.

Runtime подключён к компиляции. Transport реализован. У desktop удалён dangling
export отсутствующего renderer: сохранены model, bridge, input source и палитра,
но публичного фиктивного `run()` нет. GPUI остаётся выбранным UI framework;
рендеринг/176 controls/визуальная приёмка всё ещё открыты. Требуется восстановить
полный view, изучив inventory и оригинальные XAML/themes до его реализации.
Исходники checkpoint отформатированы rustfmt, lockfile пересобран.

## Границы и инварианты v3.1

Class: COM/LAN emitter adapters в `crates/icem-platform-windows/src/transport.rs`;
UI, команды и Modbus вне класса. Inv: закрытый канал не пишет, счётчик растёт только
на успешной отправке, reset только на successful open; fake-device tests. N+1:
следующее устройство реализует SerialDevice/PacketDevice; runtime не меняется.

Class: v1 operator sessions в `crates/icem-transport/src/emulator_control.rs`;
HTTP/gRPC вне класса. Inv: одна строка → один JSON reply; bounded framing, watch
владеет соединением, quit callback вызывается после flush; duplex + Windows tests.
N+1: новая команда остаётся в domain parser; pipe framing и lifecycle не меняются.

Class: команда над реальным Engine в `crates/icem-emulation/tests/runtime.rs`;
проверка физического устройства вне класса. Inv: all-off defaults, независимость
COM/LAN и сохранность потока после malformed command; runtime tests. N+1:
каждое имя COMMANDS обязано иметь acceptance case, полнота проверяется BTreeSet.

## Что подключено

- `Engine` + все 21 команды доступны через прежний `process_line`.
- COM: Windows 9600/8N1, exclusive handle, CRLF, successful-send counter.
- LAN: Npcap injection, исходные 152-byte ACE frames, MAC выбранного адаптера,
  порядок SV, 938 ms / 885 ms initial pause для 6/20 кадров и 5 ms между кадрами.
  Ошибка отдельного кадра не прерывает попытки отправить остальные.
- Safe application adapters используют native serial/pcap checkpoint; raw handles
  и unsafe остаются в `icem-windows-native`. См. ADR 0003.
- Windows singleton `Global\iCemEmulator_v1`: guard остаётся на owning thread.
- Local byte pipe `\\.\pipe\iCemEmule.Control`, UTF-8 JSON-lines, text/JSON parser,
  `watch` ACK + status каждую секунду, quit ACK до shutdown notification; disconnected quit завершает процесс
  после ограниченного ACK drain, даже если ответ уже нельзя доставить.
- First-instance bind отклоняет второй server с тем же pipe name. Remote pipe
  clients запрещены; signal/pipe не создаются клиентом для проверки живости.
- Bounded sessions (32), command line (65536 bytes), response (8 MiB), write (3 s),
  graceful drain (1.5 s). Stop освобождает listener и joins/aborts client tasks.
- `icem-emulator-engine-dev`: независимый headless process, CLI, 1 Hz tick,
  XML load/save, bind до hardware start, dispose перед освобождением singleton.
  `--cli` выполняется до singleton/config/emitter construction.

## Запуск на Windows

Из `rust/` (пути к templates/config должны соответствовать вашей копии):

```powershell
cargo run -p icem-emulator-app --bin icem-emulator-engine-dev -- --config .\emule-dev.xml --templates ..\iCemEmulator\templates
```

Во втором терминале:

```powershell
.\target\debug\icem-emulator-engine-dev.exe --cli status
.\target\debug\icem-emulator-engine-dev.exe --cli setport COM12
.\target\debug\icem-emulator-engine-dev.exe --cli start com
.\target\debug\icem-emulator-engine-dev.exe --cli stop com
.\target\debug\icem-emulator-engine-dev.exe --cli quit
```

Отсутствующий XML даёт all-off defaults. Запуск потоков требует явной команды
или enabled settings. `--duration-ms 1..3600000` ограничивает тестовый запуск;
без него процесс работает до `quit`. `--pipe NAME` расположен до `--cli` и нужен
для изолированных проверок. stdout CLI — JSON; diagnostics/READY — stderr.
На Linux EXE возвращает 78, поскольку Windows hardware/pipe не подменяются TCP.

## Проверки и границы доказательства

Локально: Rust 1.99.1, workspace check/build/test/clippy; GNU Windows cross-check
и Clippy emulator/platform/transport/native с all-targets. Это не запуск Windows.

Windows CI дополнительно проверяет реальные named pipes: exclusive bind,
roundtrip/rebind, отдельный engine process → CLI → тот же engine state → XML
save → quit ACK → graceful exit → successor singleton/pipe acquisition → quit с обрывом клиента.
Существующие C# differential tests остаются частью workspace suite.

Полная приёмка остаётся открыта:

- физический COM/Npcap, ACL обычного/elevated token, timing/jitter на стенде;
- GPUI view, встроенный web, исходные custom controls и tray;
- native capture/sniffer/Host control/lifecycle и production CLI/MSI;
- E4 runtime пока отправляет LAN burst синхронно под engine gate, тогда как
  исходный EmuEngineHost отправляет его отдельной Task. При активном LAN команда
  может ждать burst; это D24, production timing parity не заявляется;
- dev CLI `watch` получает первый ACK и завершает roundtrip; полный watch stream
  доступен pipe-клиентам, CLI streaming остаётся отдельным acceptance item;
- selfTest в runtime status пока null; полный app selftest ещё не подключён;
- legacy default pipe ACL не равен доказанной многопользовательской политике.

D23: line/session/response caps и remote-client rejection — явные resource/security
ограничения Rust IPC, которые baseline не задаёт такими же числами. Valid local
commands сохраняются; malformed/oversized и remote behavior не объявлены parity.
