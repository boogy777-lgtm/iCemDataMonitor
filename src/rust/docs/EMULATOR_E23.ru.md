# E23 — самостоятельный --landiag

Исторический отчёт указанного инкремента. Текущее software-состояние:
[N+1 tracker](NPLUS1_TRACKER.ru.md), [implementation status](IMPLEMENTATION_STATUS.ru.md).
Прежние NEXT/PARTIAL описывают тот checkpoint; hardware/UI/operator/release gates
остаются отдельными. [C11](../../docs/LEGACY_REFACTORING_C11.ru.md) удалил dormant
REST/serial wrappers и их эксклюзивные tests; com0com driver сохранён.

Источники: `iCemEmule.Core/SelfTest/LanLoopbackDiag.cs`, `Emitters/AceLanEmitter.cs`,
`iCemEmulator/Program.cs` из pinned baseline. .NET/proto/SPA не менялись.

Class: standalone LAN diagnostic в `apps/emulator/src/landiag.rs` и platform
`landiag::Port/SystemPort`; Host/engine вне класса. Inv: независимые handles,
фиксированные frames/pacing, bounded capture, partial counts при read fault,
cleanup при output fault; portable injected tests и native ABI shim.
N+1: новый capture profile меняет приватный native open и общую таблицу проверок;
новый diagnostic outcome меняет исчерпывающий match четырёх комбинаций counts.

Первый аргумент trim/ASCII case-insensitive `--landiag` dispatch до settings,
logger, mutex, pipe и UI. Нужен отдельный непустой adapter substring; ошибка exit2,
суффикс игнорируется. Два независимых enumeration выбирают первый match description
или name. Enumeration exception, missing sender и missing capture различаются;
exit1. Ошибки циклов не прекращают variant B и дают exit0 даже при нулевых counts.
Ошибки write/flush прекращают выполнение с exit1 и освобождают handles.

Capture: snaplen200/timeout500/promiscuous; reviewed native setup повторно использует
nonblocking poll, multicast BPF и 64KiB buffers, RAII. Host остаётся65535/100.
Каждый wait pump читает максимум256 packets, затем sleep1ms; при read fault count
успешно разобранных ACE сохраняется. Sender закрывается до post-capture wait.
Fixed SV:13120203/06/00/09/0C/0F; equipment00A86F46, payload124 ascending bytes,
первые4 — BE SV. Variant A MAC00:80:2F:23:F4:44, B real MAC или нулевой fallback.
Два bursts по6 frames:938ms/5ms, междуbursts100ms; pre200ms, post
max(500,5000−sendElapsed), settle1500ms. FramesSent учитывает успешные send;
per-send fault не прерывает остальные frames. Четвёртый legacy исход A>0/B=0
печатает только diagnosis heading; нового вывода не изобретено.

Проверено текущим кодом: Linux focused emulator/platform tests PASS; native и
platform all-targets `x86_64-pc-windows-msvc` check PASS (compile-only).
Native setup shim покрывает оба profiles×7 outcomes, включая один free/close.
Portable tests: точные bytes/MAC/pacing, четыре diagnosis outcomes, partial fault,
send/setup/output fault cleanup; process missing/blank adapter.
Windows unmatched-adapter process test добавлен; здесь не исполнялся.
Полный Windows CI/runtime, Npcap/NIC/operator/SDK/UI acceptance остаётся OPEN.
E24 stdin CLI/default dispatch — следующий отдельный checkpoint.
