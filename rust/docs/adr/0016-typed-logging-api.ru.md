# ADR 0016 — типизированное управление логами

Class: Get/SetEnabled(bool)/Clear принадлежат `icem-logging`; команды emulator и
Host status не входят в класс. Inv: bool обязателен, nullable enabled и actual
available/recording не теряются; exhaustive matches, owner/codec/model/pipe tests.
N+1: новая команда меняет owner и HTTP method/body adapter; общий Worker не знает
команд. Нового manager/listener/command bus нет.

`Logs::request(Command)` возвращает `#[must_use] Reply`: Completed (чтение), Applied
(завершённая мутация), Incomplete (операция не завершена, возможны эффекты).
Status хранит семь прежних facts. GET с диагностической error остаётся успешным.
Set закрывает writer и может подготовить/восстановить файл до отказа сохранения;
Incomplete не обещает rollback или отсутствие эффекта. Сохранённый enabled не
заменяется неподтверждённым значением. Нативный ClearReport хранит deleted/absent/
failed (число отказов удаления), reopened (actual writer presence после clear).
False reopened также означает OFF/unknown, когда reopen не требовался; отдельное
обещание успешности попытки из этого поля не следует. Ошибка reopen остаётся в
status.error и Incomplete. Каждый allowlisted файл по-прежнему обрабатывается один
раз; запись при частичном отказе продолжает доступный writer.

SharedLogs может отказать до вызова owner (ControlError::Rejected). Ошибка после
передачи запроса/потеря или повреждение ответа — ControlError::Unknown. Потерянный
результат не повторяется автоматически. Тип ошибки сохраняется через вложенный
Result в shell::Response::{Logs,LocalLogs}. Только LogView добавляет русскую
пометку Unknown/no-retry; Rejected имеет отдельную пометку. Pending/ошибка связи
сохраняют last-known status, но запрещают действия до свежего чтения.

JSON сериализуется только на прежней HTTP/2 границе `/internal/desktop/v1/logs`.
Точный v1 envelope `{ok,data}` и семь status keys/messages сохранены, новых wire
полей нет. `decode_reply` проверяет все обязательные типы и recording ⇒ available
&& enabled=true; null enabled сохраняется. V1 не содержит структурных clear counts:
после decode `clear=None`; operation text не разбирается. False ok означает
Incomplete без выдуманного атомарного отказа; true GET не стирает diagnostic error.

GPUI controls передают `logs::Request` прямо; UiPort имеет отдельные logs(Command)
и emulator(Command). Старый Value bridge/LoggedPort удалены. E18 worker 16/16,
Drop/WorkerCompletion, Shell diagnostic writes и независимость emulator сохранены.
Storage settings/rotation/allowlist/pins/reparse/hardlink/ADR0013 не менялись.
Dependencies/pins/lints прежние; icem-web → icem-logging теперь production edge
(раньше dev edge), новых crates/features нет. XAML/themes/inventory прочитаны;
геометрия, строки контролов, цвета и mapping 176+6 сохранены.

Успешный v1 Set подтверждается только при enabled==requested; ON также требует
recording=true, как гарантирует owner. Противоречивый ACK становится Unknown.

Shell `host::start` продолжает записывать Host transport failures, включая nested
Response::Logs(Err), в свой diagnostic logger. LocalLogs/Incomplete не логируются
повторно; typed ControlError передаётся UI неизменным, без рекурсии logger failures.


E19 includes separately authorized legacy test maintenance D45/D46, not a logging
API semantic change: loss-free freshness characterization and diagnostic-only
serial retry timeout evidence. Original failures, unchanged meaningful assertions,
deadlines and immutable-baseline provenance are in [SOURCE_AUDIT](../SOURCE_AUDIT.ru.md).
Production .NET code is unchanged; 14 existing exact approvals remain 14.
