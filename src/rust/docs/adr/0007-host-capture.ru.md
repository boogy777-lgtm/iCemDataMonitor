# ADR 0007 — read-only Host capture в существующем owner

Class: receive-only Npcap capture в `icem-windows-native::pcap` и
`icem-platform-windows::sniffer`; emulator send и AddIn/MMF вне класса.
Inv: session и BPF allocation имеют единственного RAII owner; наружу выходят только
owned bounded bytes; native ABI fault tests и safe capture/layer tests проверяют cleanup.
N+1: adapter остаётся данными существующего lifecycle; новых queues/threads/core flags нет.

Решение: переиспользовать serialized Host loop и bounded LayerRequest queue.
Capture-only `CaptureSession` скрывает PacketSession и не имеет send API.
Native exception ADR 0001 расширяется только в существующем `pcap.rs`; все остальные
crates сохраняют forbid unsafe. Новых dependencies и lint allowances нет.

CaptureApi разрешает семь documented C ABI exports из того же удерживаемого Arc/Pcap
System32 module: pcap_setbuff, pcap_setuserbuffer, pcap_setmintocopy,
pcap_setnonblock, pcap_compile, pcap_setfilter, pcap_freecode. Emitter `Pcap::load/open`
не требует этих exports. Функции имеют конкретные signatures, не общий untyped dispatcher.
BpfProgram имеет C-layout (u32 count + opaque instruction pointer), Rust не разыменовывает
инструкции. Успешный compile получает ровно один freecode после setfilter, включая
setfilter failure. Любая ошибка setup закрывает уникальный session через Drop.
Pcap module удерживается session до close; borrowed foreign packet не покидает ABI.

Npcap [nonblocking contract](https://npcap.com/guide/wpcap/pcap_setnonblock.html)
требует явного setnonblock после open_live; один read timeout его не заменяет.
Используются [compile](https://npcap.com/guide/wpcap/pcap_compile.html),
[setfilter](https://npcap.com/guide/wpcap/pcap_setfilter.html) и
[Windows buffer extensions](https://npcap.com/guide/npcap-api.html).
Kernel/user buffers = 64 KiB, snaplen ≤65535, minimum-to-copy=1; multicast BPF как legacy.
Polling ограничен 64 packets; processing pause отбрасывает read data, без app backlog.
MAC cardinality cap=1024 и explicit saturation — resource delta относительно unbounded
legacy tracking. EOF/error закрывает session, read-only reconnect не чаще 30 s.

Поддельные ABI functions в Windows unit test проверяют production open/setup path,
семь success/failure cases и cleanup без Npcap/физического NIC. Это software evidence,
а не driver compatibility или аппаратный timing verdict. Production capture использует
реальные dynamically loaded exports, а не тестовый source.

Синхронные enumerate/open/close не имеют принудительной отмены Windows driver call.
RPC deadline ограничивает ожидание клиента; не является гарантией завершения driver.
Nonblocking poll устраняет ожидание трафика, но hardware hang/latency остаётся gate.
Декодирование и capture-to-file не входят в текущую вертикаль. Описание active wiring,
plan/actual D06 и availability delta: [HOST_E10](../HOST_E10.ru.md).
