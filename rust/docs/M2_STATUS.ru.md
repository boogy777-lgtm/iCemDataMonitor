# M2: Windows MMF, INI и живой development host

Инкремент от 2026-10-16, Rust 1.99.1 / Edition 2024. M2 выполнен частично.
Исходные .NET проекты и AddIn не меняются. Контракты взяты из pinned baseline:
BusMapping/BusReader, IniSchema/IniOptions/IniConfig и ModbusTcpPublisher.

## Class / Inv / N+1

Class: read-only Windows bus reader, named section/mutex и unnamed fallback;
producer writes/ACL migration вне класса (`icem-platform-windows/src/bus_reader.rs`).
Inv: только existing valid v5, bounded owned copy под mutex 500 ms, decode после release;
Windows/.NET IPC tests проверяют named/fallback/timeout/abandoned/orphan/capacity.
N+1: следующий источник реализует SnapshotSource; raw handles не выходят из native crate.

Class: 16 активных INI keys и 8 retired keys; XML и поиск/migration файла вне класса
(`icem-config/src/ini.rs`). Inv: unknown/duplicate/missing/numeric/semantic errors,
range/overlap и BOM encodings проверены tests. N+1: поле/default/key/render добавляются
одной macro row; key iteration и roundtrip test проверяют полноту набора.

## Реализовано

- Read-only attach к точному имени существующей MMF. Если producer section отсутствует,
  unnamed read-only mapping existing файла, чтобы не захватить имя producer.
- Геометрия STNB/proto 1/layout 5/N≥650/64+2N проверяется до создания mutex.
  Reader не создаёт bus, не переписывает header и не меняет ACL.
- Kernel copy в owned Vec; mutex guard привязан к потоку. Декодирование после unlock.
  Нет Rust references в externally mutable mapping; Win32 FFI — отдельный
  [native exception ADR 0001](adr/0001-win32-read-boundary.ru.md).
- Timeout/abandoned mutex, sticky access/capacity failure, last-good и исчезновение
  backing pathname до проверки orphan section. SnapshotOrigin явно отличает новую копию
  от cache. Legacy D09 сохраняется: некоторые ошибки возвращают cache со Stale=false.
- INI: обязательные/optional/retired keys, регистронезависимость, duplicate/unknown errors,
  флаги/ports/tick/zone bounds/overlap, UTF-8/16/32 BOM, read-once existing file.
- Dev host: live MMF → существующее writable Modbus mirror; INI modbus_on/port/tick/total,
  mmf_path/mmf_name. Управляемая длительность, loopback, typed source diagnostics в stderr.

## Запуск на Windows

Из rust/; C++ Build Tools и pinned Rust установлены:

```powershell
cargo fetch --locked
cargo run -p icem-host-app --bin icem-host-dev -- --bus-live "C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat" --bind 127.0.0.1:1502 --duration-ms 30000
cargo run -p icem-host-app --bin icem-host-dev -- --station-config "C:\path\config.ini" --bind 127.0.0.1:1502 --duration-ms 30000
```

Опционально --bus-map/--bus-mutex для изолированного теста. Production names сохраняются
по умолчанию. На Linux live mode завершается exit 1 до создания listener.
Ошибки аргументов/INI дают exit 2. Fixture-режим остаётся доступен.
Это development CLI, не реализация legacy CLI dispatch.

## Приёмка и ограничения

Windows tests используют отдельный .NET writer через PowerShell Add-Type, без SDK iCem.
Он держит mapping и пишет BE words под тем же mutex без Flush. Это IPC integration test,
не differential запуск оригинального BusReader/AddIn. Отдельный test запускает реальный
Rust EXE и проверяет FC03/04/06 и удержание внешних записей при live ticks.

До полного M2 gate нужны реальная AddIn x86/x64, обычный/elevated token и DACL,
нагрузка и producer growth/restart, независимый review native boundary.
INI auto-discovery/default creation/station.ini migration/save/ACL и XML ещё не перенесены.
Тексты diagnostics не объявлены побайтово равными C#; нужен differential corpus.
Флаги web/sniffer/OPC проходят validation, но эти подсистемы dev host ещё не запускает.
Tray/lifecycle/web/gRPC/history/shell/emulator/MSI и полный size comparison впереди.
Полных PASS в parity matrix этот инкремент не присваивает.
