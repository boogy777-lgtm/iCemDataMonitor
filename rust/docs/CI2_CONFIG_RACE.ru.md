# CI-2a / D54: canonical INI read/create race

Основа: main `c460ae9d7f0bd64e12d12f14c23d2b2c79acef62`.
Отдельный prerequisite для CI-1; политика workflow и ensure-host не меняются.

## Наблюдение и механизм

На CI-1 head `0283817` Rust run `39433195261`, job `118664064048` в
13:56:31.850 завершил неизменённый тест
`concurrent_creation_publishes_only_complete_valid_file` ошибкой Windows
`os error 2 / NotFound` для config.ini. Legacy 941, MSI contracts и baseline
прошли свои gates; Rust gate остаётся FAIL. Повторный запуск без исправления
не используется как доказательство устранения.

Старый `station.rs::load_inner` после неудачного чтения проверял позднее
`!canonical.try_exists()`, а не вид исходной ошибки. Возможна последовательность:
первое чтение возвращает NotFound → другой участник публикует готовый canonical
→ exists уже true → исходный NotFound становится фатальным. Обратная смена
состояния позволяла ошибке разбора/доступа перейти к созданию defaults, если
файл успели удалить. Это подтверждённый дефект исходного predicate. Лог Windows
не фиксирует каждую файловую операцию, поэтому точное чередование исходного
CI run остаётся предположением, а не восстановленным scheduler trace.

Class: результаты чтения canonical station INI в `icem-config::station`;
emulator XML и копирование legacy — соседние, но отдельные политики.
Inv: только исходный `io::ErrorKind::NotFound` canonical разрешает defaults;
чужой опубликованный файл не переписывается и проходит строгий INI parser.
N+1: новая ошибка чтения остаётся fail-fast в той же private границе;
регрессии проверяют valid/corrupt winner, parse/access errors и legacy absence.

## Изменение и воспроизведение

`load_inner` выбирает путь и выполняет исходное чтение один раз, передавая
результат в private `load_selected`. Этот helper механически содержит прежнюю
политику после чтения. Guard классифицирует boxed `io::Error` через downcast
и ErrorKind::NotFound, без второго exists probe. Публичный API не расширен.
Hard-link publish, create_new временного файла, AlreadyExists/no-clobber,
RAII cleanup, legacy copy/fallback и bounded parser остаются прежними.

Детерминированный тест сначала получает настоящий NotFound через
`IniOptions::read_existing` выбранного отсутствующего canonical и проверяет
его typed kind. Затем настоящий publish публикует BOM/comment/operator bytes
с web_port=8123; сохранённая ошибка передаётся в тот же private helper.
С механически извлечённым OLD guard тест FAIL: `Os { code: 2, kind: NotFound }`,
0 passed / 1 failed. С исправленным guard он PASS, проверяет полный options,
точные исходные bytes и единственный оставшийся canonical файл. Это доказывает
механизм без timing/sleep/retry и не воспроизводит scheduler исходного Windows run.

Соседние тесты требуют: corrupt winner возвращает ConfigError без изменения
байтов; настоящий parse error после удаления файла остаётся исходным ConfigError
и не создаёт defaults; injected PermissionDenied с исходным сообщением остаётся
ошибкой без создания файла (это policy injection, не настоящий ACL repro);
выбранный legacy, удалённый перед настоящим чтением, возвращает NotFound без
создания canonical. Прежние default/no-winner, legacy publication-failure и
8-thread integration tests сохранены. `thread::scope` соединяет участников и при
раннем выходе; wall-clock cancellation синхронных OS calls не заявляется.

## Проверка и границы

Локальные команды выполняются из rust/ на exact Rust1.99.1 с неизменённым lockfile,
`--locked --offline` для Cargo checks/build/tests/Clippy. Targeted icem-config:
12 unit + 4 emulator + 1 original XML + 6 station tests PASS (23 total).
Red log: `/tmp/icem-ci2a-red.log`; config log: `/tmp/icem-ci2a-config.log`;
workspace log: `/tmp/icem-ci2a-validation.log` (не входят в repository).
Полный локальный workspace PASS: `cargo fmt --all --check`,
`cargo check --workspace --all-targets --locked --offline`,
`cargo build --workspace --all-targets --locked --offline`,
`cargo test --workspace --locked --offline` (203 passed, 0 failed, 0 ignored,
77 result groups, включая пустые cfg/doc группы),
`cargo clippy --workspace --all-targets --locked --offline -- -D warnings`.
`rustc --version`: 1.99.1 (58cf8f9c3 2026-10-13); `git diff --check` PASS.

Локальный Linux не выполняет Windows cfg/process/MSI/ACL contracts. Обязательны
независимое review и все Windows gates на окончательном head перед merge.
Никакой Windows PASS здесь не заявлен; original FAIL сохранён как evidence.
.NET production/baseline, frozen proto/SPA/API, зависимости, unsafe/lint policy,
toolchain pin и E23 не изменены.
