# E16 — Host sniffer decode/catalog

Продолжение после E15 и разрешения владельца исправлять Legacy CI/сливать и
продолжать миграцию. [ADR 0012](adr/0012-host-sniffer-decode.ru.md) содержит
Class/Inv/N+1, source semantics, resource limits и intentional deltas.

Реализован активный путь Capture.poll → Host decoder → equipment/SV tracking →
atomic last-good signal cache → periodic witness через общий logger. Existing
pause/resume/counters/singleton/CLI/console/tray/config/E12/E14 сохранены. Host
uses original lantable, не emulator firmware merge. Исходный Host JSON включён
через существующий MSI payload inventory; packaging guard проверяет relocation/hash.

External REST/proto/web/control contracts не меняются. Catalog/preview доступны
как read-only Rust snapshots; dormant REST/file capture не активированы. В текущем
legacy AceFrameDecoded не имеет production subscriber. MMF writer не добавляется.

Windows oracle сравнивает неизменённый C# Host decoder с Rust на полном pinned
catalog и реальном PCAP. Portable owner tests проверяют IDs/unknown/equipment,
preview/null/sentinel, atomic failure, idle rates, cardinality/text budgets и pause.
Exact-head Windows fmt/check/build/test/Clippy, baseline/browser/mapping/MSI results
фиксируются в PR после выполнения. До evidence полного PASS нет.

Следующий ограниченный программный инкремент — emulator CLI watch stream consumer.
Затем app --selftest/--landiag; status selfTest=null не изменяется, поскольку legacy
тоже возвращает null. Capture-to-file и D22 остаются explicit scope/compatibility
решениями. Real SDK/COM/Npcap/UI/operator/install hardware gates сохраняются.
