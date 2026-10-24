# P7c2: независимое ревью SDK native boundary

2026-10-24. База `bd7e91c`; это ревью ADR0019 перед adoption,
а не закрытие P7. Cross-apartment/process RPC и Windows runtime остаются OPEN.

Class: lifecycle/error delivery SDK subscription и bounded owned callback decode;
входит failed Unadvise/GIT startup/retrieval и входной BSTR, не входит acquisition.
Inv: cookie сохраняется до successful disconnect; registration имеет одного владельца;
failed callback delivery возвращает HRESULT error; owned event <=512KiB до allocation.
N+1: `client/callbacks.rs` и `server/callbacks.rs`, targeted native regressions;
complete reversible `icem.patch` и неизменные original42 SHA256 служат guard.

## Проверяемые дефекты (до исправления)

1. HIGH: `DataSubscription::close` делал `cookie.take()` до Unadvise: временный
   HRESULT failure терял возможность retry, тогда как сервер мог удерживать sink.
2. HIGH: reentrant closure игнорировала GIT retrieval failure и возвращала success:
   событие терялось без вызова handler и без наблюдаемой ошибки у сервера.
3. HIGH: count<=4096 не ограничивал owned память входного callback: UTF-16 BSTR
   конвертировался в String до проверки размера. Массивы уже отвергались converter.
4. HIGH: worker создавал registration guard только после MTA/GIT initialization;
   ошибка до этого места оставляла caller registration без revoke. Нужен явный
   caller/worker handoff с rollback до передачи и RAII после передачи.

## Результат и доказательства

Проверки и окончательный вывод будут записаны после исправлений.

## Дополнительный найденный ordering defect (до исправления)

Class: baseline periodic delivery при Refresh2/Cancel2, не явный AsyncRead.
Inv: baseline меняется только после успешной доставки; clear_latest + cancelled
refresh не подавляют неизменившееся, но ещё не доставленное значение.
N+1: local `PeriodicBaseline` в server/callbacks.rs; deterministic queue test
проходит clear_latest/cancel и проверяет subsequent delivery без sleeps.

5. HIGH: periodic sampling сдвигал baseline до dispatch. Refresh очищал latest и
   увеличивал epoch, а Cancel заменял refresh на CancelComplete: ни один data frame
   мог не дойти, но следующий unchanged sample уже считался доставленным.
