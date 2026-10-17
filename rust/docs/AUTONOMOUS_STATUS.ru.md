# Автономное завершение миграции

Рабочая ветка rust/autonomous-completion содержит сохраняемые checkpoints.
Проверенная база main: E3, 5eaa67e. Эта рабочая ветка пока не является
production-заменой: изменение будет перенесено в main после интеграционных проверок.
.NET AddIn и исходные проекты не изменяются. Rust 1.99.1 / Edition 2024.

В работе: XML/COM freeze/catalog, все команды и runtime, IPC/physical emitters,
GPUI с исходными controls и встроенным web, host control/lifecycle/CLI, затем tray.
Полная визуальная/аппаратная приёмка и размер полного дистрибутива отмечаются
по фактическим доказательствам; готовность не выводится из наличия файлов.

2026-10-17, продолжение: [E4](EMULATOR_E4.ru.md) восстанавливает собираемость
checkpoint, подключает runtime, COM/Npcap adapters и Windows control pipe.
Добавлен icem-emulator-engine-dev (headless process + CLI + singleton + XML).
Проверены Linux workspace tests/Clippy и Windows GNU cross-check/Clippy;
Windows CI и hardware/UI acceptance фиксируются отдельно по результату.

Следующий этап: устранить D24 (LAN burst под engine gate), полный CLI watch,
затем восстановить GPUI renderer по inventory/XAML, локальный Host gRPC/control
и lifecycle, после этого tray. Desktop windows.rs отсутствует в checkpoint;
модель/bridge/input/theme сохранены, фиктивный run() не экспортируется.

2026-10-17 E5: [Desktop / Host](DESKTOP_STATUS.ru.md) содержит GPUI renderer,
независимые GPUI Tray, monitor с WebView, local Host gRPC и Modbus owner toggle.
Полный Linux workspace: 84 tests, Clippy; Windows GNU workspace/all-targets Clippy
прошёл. MSVC runtime/UI evidence фиксируется по отдельному CI run; аппаратная
приёмка NOT RUN. Открытые M3/M6 пункты перечислены в статусе и control mapping.

Уточнение E5: GPUI Tray 0.1.4 заменил собственный native tray; profiles и variable
combos сохраняют identity, COM/LAN drafts разделены. Добавлена original HKCU
autostart policy и --autostart. Latest exact-commit CI указан в PR/Actions.
