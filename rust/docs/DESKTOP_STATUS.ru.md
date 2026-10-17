# Desktop / Host — development increment E5

Реализован GPUI renderer COM/LAN/Units с исходной light/dark палитрой, editable inputs,
списками/выбором переменных, numeric overrides, profiles/ports/adapters, equipment,
firmware и unit commands. Статусы отражают ответы engine. Selection после refresh
следует имени+SV, а не индексу. Полная визуальная эквивалентность не утверждается.

Добавлены независимые emulator tray и Host tray через **gpui-tray 0.1.4**,
исходные icons/menu texts, native backend библиотеки и auto-reset Host lifecycle
и shell events. X у эмулятора закрывает окно, engine/pipe остаются; Open возвращает view;
Exit/CLI quit завершают engine. Background bridge joins перед освобождением state. Profiles сохраняют deviceType;
COM/LAN имеют отдельные drafts и list/combo selection по имени+SV.

Monitor теперь GPUI client: «Состояние» через original Host gRPC и «Панель» через
WRY/WebView2 с сохранённой SPA. Добавлены local HTTP/2 listener/client, exclusive pipe,
Modbus SetLayer v1 toggle через владельца listener, optional active INI persistence,
SetConsoleVisible, SetAutostart и actual console/Run probes. Остальные control RPC пока UNIMPLEMENTED.
При отсутствии Host приложение показывает ошибку соединения, не запускает фиктивный Host.

## Запуск из rust/ на Windows

```powershell
cargo build --workspace --locked
# Синтетический dev host: не подключается к станции и не создаёт AddIn bus.
.\target\debug\icem-host-dev.exe --modbus-fixture .\tests\fixtures\bus-v5-minimal.bin --bind 127.0.0.1:1502 --web-bind 127.0.0.1:8090 --rpc-pipe iCemDataMonitor.Host --tray-icon .\assets\iCemDataMonitor.ico --duration-ms 0
.\target\debug\icem-shell-dev.exe --pipe iCemDataMonitor.Host --web-url http://127.0.0.1:8090
# Независимый emulator. Отсутствующий новый XML даёт all-off defaults.
.\target\debug\icem-emulator-engine-dev.exe --config .\emule-dev.xml --templates ..\iCemEmulator\templates --desktop
.\target\debug\icem-emulator-engine-dev.exe --cli status
.\target\debug\icem-emulator-engine-dev.exe --cli quit
```

`--desktop` является opt-in к существующему dev engine. Не заменяет production EXE/MSI.
Emulator icon сейчас берётся из source assets (development path); installer layout
будет отдельным этапом. Host без `--duration-ms 0` сохраняет прежний default 10 s.
`--tray-icon` автоматически включает original Host pipe, если имя не задано явно.
Host с local pipe берёт original Host singleton; не запускать одновременно с legacy Host.

## Честные границы

- `desktop-gpui-mapping.json` перечисляет все frozen named controls; partial/pending
  являются открытой приёмкой. Palette или файл renderer не означают visual PASS.
- Selected/focus/hover,
  checkbox/segmented geometry, titlebar icon, splash и keyboard/DPI требуют доработки/сравнения.
- Host sniffer/Web+OPC persistence/boot/navigation/CLI/ensure ещё не завершены.
- Autostart сохраняет exact original Run data и очищает StartupApproved; --autostart
  читает station config из ProgramData и работает до lifecycle stop. Installer/VM acceptance открыта.
- Host console probe не означает сохранение всех console close/attach политик legacy.
- D24 сохраняется: LAN burst идёт под engine gate. UI не блокирует message loop,
  но backend command latency пока зависит от burst.
- COM/Npcap, ACL/elevation, Explorer restart, UI screenshots и полный payload size
  требуют стенда и не объявляются выполненными на Linux.

Проверки этого increment записываются по результатам exact commit CI. См.
[аппаратную приёмку](HARDWARE_ACCEPTANCE.ru.md), [ADR](adr/0004-desktop-tray-host-pipe.ru.md).

Изменение 2026-10-17: собственный native tray удалён по указанию пользователя; оба
tray используют GPUI Tray. Общий picker сохраняет deviceType/identity, добавлены
COM/LAN variable combos и отдельные drafts. Caption console обновляется каждые 50 ms.
