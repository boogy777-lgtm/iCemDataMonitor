# Host E19 — типизированные логи

Исторический отчёт указанного инкремента. Текущее software-состояние:
[N+1 tracker](NPLUS1_TRACKER.ru.md), [implementation status](IMPLEMENTATION_STATUS.ru.md).
Прежние NEXT/PARTIAL описывают тот checkpoint; hardware/UI/operator/release gates
остаются отдельными. [C11](../../docs/LEGACY_REFACTORING_C11.ru.md) удалил dormant
REST/serial wrappers и их эксклюзивные tests; com0com driver сохранён.

`icem-logging::Command` исключает set без bool. Status хранит tri-state enabled,
available/recording, directory/error/operation/written. Reply различает Completed,
Applied и Incomplete; clear возвращает фактические counts/reopened локально.
Отказ после эффекта не объявляется атомарным failure/success. Подробности:
[ADR 0016](adr/0016-typed-logging-api.ru.md).

Host service queue, Shell local worker, независимый emulator и GPUI используют
один typed owner API. Shell Responses сохраняют ControlError без String roundtrip;
Bridge разделяет Logs/Emulator replies на прежнем bounded Worker. Управление логами
не запускает emulator. HTTP/2 v1 остаётся только на существующем local Host pipe;
frozen proto/web/emulator grammar, envelope/status/messages не меняются.
V1 не переносит clear counts: decode оставляет None. Missing/malformed reply не
подтверждает freshness; Unknown не ретраится, last-known факты сохраняются.

Проверки: malformed/unknown-field PUT отклоняется до owner, строгий codec/tri-state/
recording consistency, typed Get/Set/Clear, locked-file partial clear с actual
writer и counts, refusal сохранения после tail-repair эффекта, ON/OFF+restart,
rotation/allowlist/hardlink/reparse/pins, реальный pipe с потерей ответа ПОСЛЕ OFF
и counter=1 без retry, E14 typed logs discard/in-flight completion, Shell regression.
Portable fmt/check/build/test/Clippy выполняются Rust 1.99.1 locked/offline;
Локально: Rust 1.99.1 Linux fmt/check/build/Clippy PASS; 175 portable tests PASS.
Windows/MSVC/legacy/mapping/browser/differential/native/process/pipe/MSI результаты
должны быть привязаны к точному PR head. Linux не является Windows PASS.

## Отдельная диагностика существующего Windows gate

Post-merge main `8f40bcc88af4b94651cb652811f1d741d358cbc4`, Rust run
39245950842 / job 118037765260: console_stop FAIL за 11.42 s с единственным
`child deadline exceeded`. По этой строке нельзя доказать ни Host teardown failure,
ни PowerShell startup timeout. В E19 добавлены только stage/PID/action/deadline
сообщения всех child waits и stderr phases helper (startup/interop/action).
Class: child waits в `apps/host/tests/console_stop.rs` и
`tests/windows/Send-OwnedConsoleCtrlC.ps1`, не production lifecycle.
Inv: stage/PID/action/deadline видны при сбое, stdout helper остаётся только JSON
(проверяется существующим serde_json decode); прогресс пишет только stderr.
N+1: обязательный stage аргумент wait проверяется компилятором во всех call sites.
Host teardown 5 s, helper 10 s и state 2 s неизменны; retry мутаций не добавлен.
Production Host/.NET исходники этим изменением не исправлялись. Причина базового
сбоя остаётся неустановленной до диагностического Windows evidence.

## Открытые gates

Physical emulator X/Open×3 с drafts/scroll/selection, keyboard/focus/DPI/session,
real iCem/AddIn/COM/Npcap, SDK-built release, install/reboot/rollback/offline и payload
measurements — NOT_RUN/OPEN. Mapping 176+6 и appearance прежние; visual parity не
объявляется. E20 typed emulator payloads и E21 dashboard остаются следующими
инкрементами. Dormant capture-to-file не активирован.


## E19 Legacy CI: D45/D46

Run 39249279178 / job 118048857901 on ba61f4262aac5fe10e5a9868feea22190c50fee1
failed two unchanged legacy tests. [Audit D45/D46](SOURCE_AUDIT.ru.md) retains the
original evidence and Class/Inv/N+1. D45 fixes the confirmed first-consumed versus
first-produced DropOldest assumption using a bounded loss-free attempt, atomic MMF
mutation, explicit producer stop/join and writer disposal; original freshness
assertions remain. D46 adds timeout diagnostics only, retaining 15s/10s and exact
PAIR_MISSING/second-attempt assertions. Serial timeout cause remains unknown.
Production .NET sources, immutable baseline and 14 approval count remain unchanged.
Exact approved test blobs refer to source-only commit
6d2d87c2ac8daaad555e886bb5f673bc531aa30a in PR15; new Windows CI is required.


## D47: typed renderer mapping guard

The earlier ba61f42 Rust run 39249279337 failed a stale mapping guard searching for
wire action strings in the typed renderer. The guard now checks the actual typed
button array and exact index/action pairing, retaining all six extension identities
and composition surfaces, frozen 176 controls and Host console checks. Eight
fixture cases run from Scaffold (positive and seven negative mutations). Local
Python-equivalent mapping/negative checks PASS; actual PowerShell is NOT_RUN here.
[Audit D47](SOURCE_AUDIT.ru.md) retains the failure and architecture checks.

The v1 successful Set decoder also requires available=true for both bool values:
even OFF must publish through the owner's directory. ON additionally requires a
ready writer. Contradictory ACKs are Unknown; existing codec tests cover unavailable
success replies without changing the v1 serializer or owner behavior.


## Late post-merge evidence: D48 / PR16

Merged E19 main c1c4c0daf2addc59d517e853d4a8faa3a0ba5922 had a Legacy CI failure:
run 39253155562 / job 118061709492, DropOldest overflow test line 187. Its first consumed
sample was incorrectly assumed to be the producer's full sample. D45 freshness and
Serial tests passed; the original post-merge failure remains part of the record.
PR16 applies the same bounded loss-free initial observation to this one test,
retaining every original overflow assertion, 15s total budget and 10s cleanup, and
disposing the writer. Other test methods and production code are unchanged.
[Audit D48](SOURCE_AUDIT.ru.md) records Class/Inv/N+1 and source-only provenance
ba8d1af1ca49e4ade40070ff13a358b93cb9a850. Existing approval count remains 14 and the
immutable baseline is unchanged. Actual Windows execution is NOT_RUN before PR16 CI;
source/diff/baseline checks alone are not a Windows runtime PASS.
