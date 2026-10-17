# Host E8: фактические пробелы после E7

Проверена база main `a38d372911fa6972f9620a458588023141e6ef13`.
Development EXE не является production/MSI replacement. Полная parity не доказана.

| Область | Фактический код | Следующий проверяемый результат |
|---|---|---|
| Modbus | apps/host main: owning loop управляет bind/stop; SetLayer игнорирует desired_state; persistence только modbus_on | Сохранять общий план всех поддержанных флагов; тестировать ошибки persistence и реальный socket |
| Web | icem-web: router/assets/history опираются на HostLayers.web; RPC пока UNIMPLEMENTED | Отделить startup-effective Web от плана следующего старта; legacy persist-only toggle через owner queue, без изменения listeners/routes на лету |
| OPC DA | Legacy LayerRuntimeController.Toggle(OpcDa) возвращает сообщение, не меняет ни флаги, ни INI; это внешний stub | RPC возвращает неизменённый LayerState; фактический OPC server/control требует отдельного решения и приёмки |
| Sniffer | Rust ACE codecs/emitter существуют, Host capture owner отсутствует; RPC UNIMPLEMENTED | Отдельный read-only capture lifecycle, counters/probe и pause/resume; не подключать dormant REST и не писать MMF |
| Boot | BootStages replay/live и READY после bind существуют | Ошибки/lag/reconnect, cancel при backpressure и новый процесс: не сохранять старый READY |
| Navigation | Host pending/query и Shell show существуют | Проверить read-and-clear failures/reconnect и смену процесса, избежать повторных мутаций |
| Restore/config | options() read_existing; read-once; station migration/default create не подключены | Перенести legacy search/migration/active-path policy, failure evidence; не молча создавать Data.dat |
| CLI/ensure-host | Только development argv; Shell требует работающий Host | Production argv/stdout/exit и bounded READY wait; один launch owner, без retry мутаций |
| Console/lifecycle | Singleton/events/tray/stop/restart есть; console visibility есть | Console-close policy, repeated stop/reopen/singleton и отдельная независимость emulator |
| Payload/MSI | Отдельные dev EXE; installer план | Измеренный payload и чистая Windows VM install/upgrade/uninstall/offline; не объявлять Rust replacement заранее |

Порядок текущего инкремента: исправление baseline guard → Web/OPC owner integration →
проверки/документация/PR. Остальные строки остаются открытыми, а не воспроизводят
исторические списки E4/E5. Нативные GPUI/tray решения E6/E7 сохраняются.

Class: SetLayer с owner-очередью (`icem-web::{model,rpc}`, `apps/host::{main,layers}`);
Web — deferred plan, Modbus — runtime, OPC — external no-op; sniffer пока вне реализации.
Inv: один сериализованный owner, bounded queue/5s RPC, desired_state не setter;
startup-effective SPA/history не меняются deferred toggle; process/HTTP/RPC tests.
N+1: следующий layer реализует arm owner; core и независимый emulator не изменяются.
