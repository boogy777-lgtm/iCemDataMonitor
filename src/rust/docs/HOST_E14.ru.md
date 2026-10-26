# E14 — lifecycle окон desktop

Исторический отчёт указанного инкремента. Текущее software-состояние:
[N+1 tracker](NPLUS1_TRACKER.ru.md), [implementation status](IMPLEMENTATION_STATUS.ru.md).
Прежние NEXT/PARTIAL описывают тот checkpoint; hardware/UI/operator/release gates
остаются отдельными. [C11](../../docs/LEGACY_REFACTORING_C11.ru.md) удалил dormant
REST/serial wrappers и их эксклюзивные tests; com0com driver сохранён.

PR #9 слит по отдельной команде пользователя. База:
`abd9055d65a9ca0f867e792a685287dcd473d3b0`, branch `rust/window-lifecycle`.
Владелец сообщил об исчерпанном лимите Actions. Новый commit использует [skip ci];
workflows/guards не изменяются. Пропущенный CI не является PASS.

X/Alt+F4 emulator скрывает существующее окно вместо destroy/recreate HWND. Engine
и tray остаются активны. Open восстанавливает тот же HWND/view; actual visibility
и restore проверяются после async OS request с deadline 2 s. Нет optimistic ack
или automatic mutation retry. Отказ/timeout сохраняется как ошибка model.note.
Tray/pipe Quit сохраняет прежний engine-owned ACK/stop/save. Shell X закрывает
только Shell; его UI callback больше не ждёт backend. Владелец приложения завершает
WorkerCompletion после UI loop, сохраняя завершение уже начатой записи/очистки.

[ADR 0010](adr/0010-desktop-window-lifecycle.ru.md) фиксирует Class/Inv/N+1,
native interface/dependency review и границы отмены. Новых контролов нет;
mapping остаётся 176 legacy + 6 logging extensions. Visual parity не доказана.

Portable tests: stalled backend при UI close, full reply queue, retained view,
отбрасывание queued clear/toggle, освобождение port после in-flight и actual-state
ack/restore/timeout с injected time. Native Windows test создаёт owned HWND и
выполняет Hide/Show/minimize/restore с message pump и конечным deadline; foreign
desktop HWND отклоняется до эффекта. Результаты локальных проверок — в PR.

Windows UI/operator execution NOT_RUN: X/Open ×3 с несохранённым input, scroll,
размером/selection; minimized/maximized restore; tray/pipe Quit в видимом и скрытом
состояниях; Shell close/reopen при независимом живом Host; focus/DPI/session/UAC и
ошибка tray. При недоступном tray запуск возвращает ошибку. Linux/Windows GNU
cross-check/link не заменяют эти gates, SDK/AddIn load или COM/Npcap acceptance.

Console Host X → hide остаётся OPEN (ADR 0009). Реальный release workflow не
запускался; SDK не подтверждён. MSI/force/Repair и reboot/rollback/offline gates прежние.
