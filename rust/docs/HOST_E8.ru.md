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

## Реализация инкремента

`apps/host/src/layers.rs` использует существующую bounded очередь (16 запросов,
RPC wait 5 s): Modbus bind/stop, Web session-plan toggle, OPC external no-op.
Никаких автоматических повторов `SetLayer` нет. Одинаковые desired_state при Web
и Modbus переключают дважды. Sniffer по-прежнему UNIMPLEMENTED и недоступен в UI.

`WebState.web_enabled` — неизменяемый снимок startup web_on. Router, SPA, history
и sampler используют его; `HostLayers.web` сообщает session plan, как legacy
TelemetryServiceImpl.BuildLayerState. Это различие сохраняется и для заново
построенного router, и для уже работающего TCP listener. Только новый Host с
заново прочитанным config применяет сохранённый план.

Web/Modbus сохраняют текущий общий план в active config, только если SetAsDefault=1;
остальные INI поля сохраняются. Session-only Web toggle не переписывает файл.
OPC не меняет состояния и не выполняет persistence, даже после ошибки другого слоя.
Предыдущая документация «Web/OPC persist-only» была неточной относительно OPC.

Ошибки bind/stop/persistence передаются через LayerState.last_error, как в legacy
controller; transport/queue/deadline errors остаются RPC errors. Ошибка сохранения
оставляет session plan и last-saved IniOptions раздельными, следующий успешный toggle
сохраняет совокупный план. Это также исправляет прежний Rust ответ INTERNAL вместо
legacy LayerState на ошибку операции. При bind failure состояние отражает отсутствие
socket owner. Не введены retry/rollback мутаций. Полная эквивалентность rollback/busy
при отказе Modbus stop остаётся открытой; software stop не имитируется как аппаратный.

Проверки: owner unit tests с настоящими TCP listeners и INI файлами (save on/off,
ошибка записи в directory, recovery, aggregate persistence, занятый port), HTTP/RPC
контракт при противоположных effective/plan flags; Windows process gRPC test расширен
двойным Web toggle, OPC no-op и отказом sniffer. Полный результат относится только
к соответствующему commit/run в PR; visual/hardware/MSI gates не закрываются.

## Локальная проверка E8

Rust `1.99.1 (58cf8f9c3 2026-10-13)`, Edition 2024/resolver 3, cargo fetch --locked.
Verify-Scaffold (без RequireCompleteBaseline только из-за недоступного через connector
original PCAP) прошёл: fmt/check/build/test/Clippy -D warnings; 108 Rust tests, 25 guard
cases, mapping 176/176. Baseline локально 512/513 с явным warning; full 513/513 проверяет
неизменённый RequireCompleteBaseline на реальном GitHub checkout. Отсутствующий файл
не добавлен/не удалён/не подменён. Cargo.lock и legacy sources этого инкремента не менялись.

Первый коммит PR `aad778412e5c7ac34a34d9651192ff7ea8c11c10`: Linux CI run 39064480150
выполнил полный guard 513/513, Scaffold и Chrome/theme contracts успешно. Это доказательство
baseline-исправления, не подмена проверки последующего Host-кода. Результат Windows и
последующего exact-head CI фиксируется в PR после завершения jobs.
