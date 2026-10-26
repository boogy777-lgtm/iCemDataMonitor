# ADR 0003 — граница Windows COM/Npcap

Статус: development integration; аппаратная приёмка открыта.

Checkpoint `8989936` добавил `serial.rs`/`pcap.rs` с локальным allow(unsafe_code),
но не сохранил упомянутый ими ADR 0004. Этот ADR явно фиксирует расширение
native-границы; ADR 0001 для MMF и workspace forbid остаются в силе.

Class: owned serial/pcap sessions в `icem-windows-native/src/{serial,pcap}.rs`;
COM domain logic, UI, injection scheduling вне класса. Inv: handle уникален,
CloseHandle/pcap_close выполняется RAII, DLL живёт через Arc до закрытия sessions,
пакеты копируются в owned Vec до следующего capture call. Check: Windows compile,
Clippy undocumented_unsafe_blocks и adapter tests; hardware validation отдельно.
N+1: следующая OS операция требует отдельного review инварианта; domain получает
только safe методы и не получает pointers/handles/native buffers.

Новые first-party unsafe операции в этом инкременте не добавлялись: существующие
native source были подключены через safe adapters и отформатированы.

COM: CreateFileW exclusive sync, 9600/8N1, finite write timeout 500 ms на native
write, проверка полного/нулевого write, cap 65536 bytes, handle closes on error.
Это не утверждение о суммарном deadline повторных partial writes.

Npcap: LoadLibraryExW только абсолютных System32/Npcap или System32 paths;
exports transmute только к конкретной документированной C ABI signature, без
универсального symbol cast. pcap_t unique; все изменяющие методы требуют &mut.
Capture header/caplen проверены (<=65535 и <=wire len), buffer копируется до
следующего next_ex. Foreign list/string validity опирается на контракт Npcap.
MAC list из GetAdaptersInfo ограничен 1 MiB и проверяется по bounds/alignment.
Для произвольно повреждённой DLL memory safety не доказывается.

Dependency: уже присутствующий windows-sys 0.61.2 (MIT OR Apache-2.0), Windows-only.
Npcap не встраивается в дистрибутив; его наличие/лицензия/driver install — отдельные
deployment requirements, приложение не устанавливает драйвер автоматически.
Расширенный feature list checkpoint сохранён; pruning неиспользуемых tray/registry
bindings допустим отдельным dependency review, без объявления их реализованными.

IPC использует уже имеющийся Tokio (MIT) с rt/net/sync/time/macros/io-util.
Не добавлен новый runtime или собственный unsafe named-pipe слой. Third-party
Tokio/windows bindings имеют свой unsafe код; first-party forbid не ослабляется.
GPUI/WRY зависимости checkpoint теперь покрыты lockfile; renderer отсутствует,
их наличие не означает ни приёмку UI, ни полный dependency security audit.
