# Реализованный инкремент: Modbus

2026-10-16. Разработка ведётся в `rust/` существующего приватного репозитория.
Первый коммит каркаса: `a76e81f054a18809ef8e466071bcc0f908efe0ac`.

## Границы и инварианты v3.1

Class: Modbus FC03/04/06/16 над динамическим зеркалом; ACE и изменение producer bus вне класса (`crates/icem-protocols/src/modbus.rs`).
Inv: диапазоны и полное тело проверены до записи; ошибочная FC16 не меняет ни одного слова; checked/widened bounds и wire tests.
N+1: следующий FC меняет `Function`, exhaustive matches и его vectors; политика зеркала и transport не меняются.

Class: сессии Modbus TCP с MBAP и unit/PDU; HTTP/gRPC вне класса (`crates/icem-transport/src/modbus_tcp.rs`).
Inv: один ответ на ADU, порядок TID/PID сохранён, partial I/O не теряет байты; фиксированная граница frame/queue и socket tests.
N+1: новый клиент не добавляет механизм; новый FC проходит тот же framing, пределы подтверждаются повторно.

Class: проверка неизменности Git baseline на Windows/Linux; UI acceptance вне класса (`tools/Verify-Baseline.ps1`).
Inv: сравниваются канонические Git bytes; checkout CRLF не считается правкой; проверка 513 исходных blob в CI.
N+1: следующий source-файл проверяется тем же manifest loop; тот же canonical hash применяется к SPA.

## Что работает

- Bus v5 owned decoder: exact length, LE/BE/CDAB, int32 timestamp, NaN/-0 bit preservation.
- FC03/04: 1..125 words, big-endian wire responses.
- FC06/16: echo, 1..123 words для FC16, независимое writable зеркало, exception 01/02/03.
- Динамический resize: overlap сохранён, grow zero fill, shrink truncate.
- Каждый tick: слова 0..3 зеркалируются; raw flag=1 перезаписывает зону;
  flag=0 или другое значение сохраняет внешние записи; резерв тоже сохраняется.
- LastGood обновляет header без zone overwrite; Unavailable обнуляет header и удерживает зоны.
- Реальный TCP: fragmentation/coalescing, persistent clients, partial writes, bounded backpressure,
  synchronous bind failure, session isolation, stop/join/RAII и повторный bind.
- Диагностический `icem-host-dev`: immutable Bus fixture → Modbus loopback, mirror tick 500 ms,
  заданная длительность, корректное освобождение listener. Все сообщения — stderr.

## Запуск диагностического host

Из `rust/`:

```powershell
cargo run -p icem-host-app --bin icem-host-dev -- --modbus-fixture tests/fixtures/bus-v5-minimal.bin --bind 127.0.0.1:1502 --duration-ms 30000
```

Это синтетический стенд. Он не читает живую MMF, не запускает tray/web/shell/emulator.
Без `--modbus-fixture` возвращается usage exit 2; ошибка bind/decode — exit 1.
Полный legacy CLI остаётся задачей M3/M4. Приложение не заменяет production host.

## Намеренные отклонения и нерешённые вопросы

- D05: пятибайтное тело FC06 возвращает exception 03 вместо выхода за границы.
- D10: resize выполняется **до** stale-return, как фактический C# код; комментарий о запрете resize
  не принят за исполняемый контракт. Artificial stale + different count закреплён отдельным тестом.
- D14: transport закрывает соединение при MBAP length вне 2..254; legacy допускает выделение
  до 65535 и обрабатывает length 0/1 иначе. Это safety delta для malformed traffic.
- D15: std-only dev transport использует max 64 connections (configurable 1..1024),
  одну pending response на клиента и poll 2 ms (configurable 1..100 ms). Исходник не имеет такого cap.
  Ограничения явные, есть rejection/failure/request counters; это ещё не production policy.
  M1 должен измерить нагрузку/latency и утвердить пределы до полной parity-приёмки.
- Пропускная способность polling и поведение реальных PLC на Windows ещё не доказаны.
- Нет Windows MMF adapter, INI/XML parser, REST/gRPC/history, tray, shell, DFB/COM/ACE emulator и MSI.
- Не выполнено сравнение полного размера с .NET и сквозной differential test старого server vs Rust.

## CI и регрессии

Первый запуск CI каркаса выявил два дефекта verification script: Get-Item без -Force
не читает dot-prefixed baseline на Linux; raw hash SPA сравнивал Windows CRLF checkout
с frozen LF. Исправлены чтение metadata и canonical Git hash comparison; исходные .NET файлы не менялись.
Ни одна полная строка parity matrix не получает PASS до Windows/AddIn/operator acceptance.

Текущий toolchain — Rust 1.99.1 / Edition 2024: [политика и идиомы](RUST_IDIOMS.ru.md).
