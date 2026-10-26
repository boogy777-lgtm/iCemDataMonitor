# Safe Rust и размер

Цель: убрать .NET 10 и WinAppSDK из перенесённых host/shell/emulator при сохранении
функций. .NET Framework внутри legacy iCem и .NET48 AddIn остаются. Нельзя обещать
конкретное число мегабайт без baseline и полной функциональной release-сборки.

## Политика безопасности памяти

- Все прикладные/shared crates используют `#![forbid(unsafe_code)]` и workspace lint.
  Единственное исключение — `icem-windows-native`: [ADR 0001](adr/0001-win32-read-boundary.ru.md),
  локальные Win32 FFI blocks с SAFETY-обоснованиями и собственным deny lint table.
- Во внешних data paths нет unwrap/expect/todo/unimplemented. Input errors — Result.
- Decoder работает с owned copy, проверяет header/count/length до индексов;
  разрядность суммы адрес+количество расширена, CDAB не использует transmute.
- Windows MMF adapter реализован через owned handles и thread-bound mutex guard.
  Rust std/dependencies/Windows/Npcap могут содержать
  unsafe/native code: запрет в нашем crate не доказывает безопасность всего стека.
- MMF: внешний .NET writer может менять память. Создание `&[u8]` по raw mapping
  без доказанной синхронизации не становится safe от одной Rust-обёртки.
- Native API не создаёт Rust references в MMF: bounded ReadProcessMemory копирует в owned Vec.
  Guard !Send/!Sync, decode выполняется после release. Чтение файла используется только
  для исходной геометрии, не как замена MMF. Это не формальное доказательство или
  независимый аудит; real AddIn/DACL/stress/review остаются release gates.
- Fuzz/property tests нужны для MMF bytes, MBAP/PDU, ACE/FLECS и emulator command parser.
  Лимиты buffers/clients/queues выбираются после baseline и становятся явным контрактом.
- Новый listener по умолчанию сохраняет loopback; не включать remote exposure ради теста.

## Размер и сборка

Root Cargo profiles: release `opt-level=s`, thin LTO, 1 codegen unit, symbols stripped;
profile size — `z` + fat LTO для сравнения. `overflow-checks=true` сохраняет проверки.
`panic=abort` не включён: он прекращает процесс без cleanup, что влияет на tray/handles.
Это компромисс для измерения, не гарантия минимального размера.

| Метрика | Как считать | Gate |
|---|---|---|
| EXE/DLL bytes | Каждый release-файл, без PDB/debug | Отдельно host/shell/emulator/AddIn |
| Installed bytes | Весь payload, assets/templates/native DLL | Rust полный комплект < .NET полный комплект |
| MSI compressed bytes | Готовые MSI одинакового состава | Отдельно от installed size |
| Prerequisites | .NET48 legacy, WebView2, Npcap/com0com, VC runtime | Явно указать уже установлен/нужен и offline cost |
| Working set / startup | Одинаковая машина и сценарий | Не подменяет показатель размера |
| Throughput/latency | mirror, streams, COM/LAN cadence | Без деградации принятых временных контрактов |

`tools/Measure-Payload.ps1` получает две готовые папки payload, пишет JSON с bytes и
отношением. Не учитывает отсутствующие installers автоматически. Zero-dependency
M0 EXE нельзя сравнивать с production .NET приложением: это заведомо разный функционал.
План сокращения: один shared runtime на host, переиспользование SPA, минимальные crate
features, без Electron/встроенного Chromium, без дублирования таблиц в каждом EXE,
удаление .NET10/WinAppSDK payload после parity. Не использовать UPX как замену архитектуре.

## Первичные справочные источники (проверены 2026-10-16)

- [Cargo profiles](https://doc.rust-lang.org/cargo/reference/profiles.html): size, LTO, strip, panic.
- [tray-icon](https://docs.rs/tray-icon/latest/tray_icon/): кандидат native tray с message loop.
- [GPUI](https://gpui.rs/): обязательный desktop backend по решению пользователя; Windows acceptance впереди.
- [tokio named pipe](https://docs.rs/tokio/latest/tokio/net/windows/named_pipe/index.html): I/O pipe.
- [tonic server](https://docs.rs/tonic/latest/tonic/transport/server/struct.Server.html): incoming transport.
- [tonic-web](https://docs.rs/tonic-web/latest/tonic_web/): адаптация gRPC-Web.

Это shortlist для M1, не сведения о проверенной совместимости всех библиотек с текущим
MSRV 1.99.1. При подключении выбрать совместимые версии или явно поднять toolchain,
зафиксировать Cargo.lock, license audit и dependency safety review.
