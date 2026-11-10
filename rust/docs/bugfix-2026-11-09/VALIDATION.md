# Windows runtime bugfix — план и результаты проверки

Запрос: MMF при работающем iCem, Host tray, независимость Host от окна
приложения и подтверждённые смежные дефекты. Только Windows x64 и Release.

## Источники и границы

- [MMF / конфигурация](MMF.md): живая секция наблюдалась read-only; первичный
  воспроизведённый дефект находится до reader, в публикации конфигурации.
- [Tray](TRAY.md): отсутствие значка из-за раннего отказа запуска не равно
  доказательству ошибки Windows notification area.
- [Время жизни](LIFETIME.md): процессные проверки не заменяют операторский
  сценарий закрытия конкретного окна.
- [Dashboard](AUDIT.md): адрес берётся у владельца фактического listener, не
  угадывается по установленному INI.

Установленный продукт не удаляется и не обновляется этими проверками. Реальные
конфигурация, AddIn, Data.dat и процессы iCem не изменяются. Изолированные
проверки используют собственные временные каталоги, pipe, порты и процессы.
Фикстуры и статические проверки не являются доказательством hardware parity.

## Итог общего gate

Проверен application/build commit `44b6b62bf86de1acd7db818ca214afe4a749c966`.
Последующий коммит с этим отчётом не меняет исполняемый код пакета.

| Проверка | Результат |
|---|---|
| Baseline, frozen proto/SPA, desktop mapping | PASS; 487 файлов + 26 утверждённых удалений, 176 controls + 6 logging extensions |
| fmt / check / build / test / clippy `-D warnings` | PASS; Windows x64, Release, Rust 1.99.1, 370 tests в 94 группах |
| SDK-backed AddIn, static CRT payload, MSI и Setup | PASS, версия 0.1.1, без обновления установленного продукта |
| Извлечённый пакет, relocation, Host bind, emulator ACK, Shell loader | PASS |
| Setup policy для двух семейств, SHA256 payload и UI inventory | PASS; обнаружена установленная Rust 0.1.0, удаление не запускалось |
| Контракт результата builder | PASS; ровно один объект setup/checksum, не текст native stdout или COM null |
| HostTray start/poll/stop с настоящей иконкой | PASS в общем Rust suite |
| Визуальная видимость/меню tray, DPI, Explorer restart | NOT_TESTED |

Сначала общий gate обнаружил Clippy `field_reassign_with_default` в новом
процессном тесте: исправлен initializer, без lint allowance. Следующий run
собрал пакет, но обнаружил загрязнение результата `Build-WindowsSetup.ps1`
COM null и native stdout; исправлены направления потоков. Последний run
полностью завершился успешно. Известное future-incompat предупреждение
стороннего `proc-macro-error2 2.0.1` остаётся предупреждением будущего Rust,
не подавлялось и не блокирует текущие 1.99.1 checks.

## Живая проверка после исправления прав mutex

В обычном, не elevated процессе запущена извлечённая копия Host из окончательного
ZIP. Применены собственные PROGRAMDATA/LOCALAPPDATA, новый config.ini, уникальный
Host pipe, отдельные loopback-порты; MMF и mutex AddIn открывались существующие.
Проверка осуществлялась при работающем iCem. Config создан с нуля успешно.

После `WM_CLOSE` только собственного диагностического HWND окно стало скрытым,
а Host продолжил HTTP. Стандартные Shell UI events перед запуском отсутствовали;
запущен собственный Shell с custom pipe без `--web-url`. После `WM_CLOSE` его
собственного HWND процесс Shell успешно завершился, Host остался жив.
Затем `/api/status` показал busOpen=true и heartbeat **8398 → 8399**,
`/api/regs` — stale=false. В конце остановлены только сохранённые тестовые
процессы. Иконка tray создавалась Host, но её внешний вид глазами оператора
этой проверкой не подтверждается.

Артефакты: [live-result.json](artifacts/live-result.json),
[воспроизводимый acceptance script](artifacts/Test-LiveHost.ps1).
Подробные локальные stdout/stderr:
`%LOCALAPPDATA%\Temp\opencode\icem-live-bugfix-39ca2fd998004ca5a08fa748e2221f15`.
Общий transcript: `%LOCALAPPDATA%\Temp\opencode\build-icem-bugfix-20261109.log`.
Прежние оговорки в отчётах агентов относятся к их промежуточным checks;
этот раздел фиксирует последующую общую проверку.

## Выдаваемые файлы

Каталог: `dist/rust/bugfix-20261109-final/`.

- `iCemDataMonitor-rust-dev-0.1.1-windows-x64-Setup.exe` — interactive entry,
  13 825 536 bytes; SHA256
  `5F57A3445AF0082B07E24DC2F7FAC267562E38BF6533949AC0D08EDFAC72E96C`.
- MSI: `AF1DC30AB6B2E1D77A9061F36562CD74BCF726D0FD0F4DDAD9EA38BF0F913C02`.
- ZIP: `23B27882671FFAB8CBEC2209EF002085F1EB8DFE312A3630DCC59A6987998A44`.

На один head и одну Windows-сессию подтверждены software/live свойства выше,
не вся hardware/operator/production-cutover матрица. Пакет сохраняет rust-dev
маркировку. Автоматическая установка/удаление не выполнялись.
