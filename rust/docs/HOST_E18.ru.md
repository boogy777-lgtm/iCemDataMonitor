# E18 — типизированный Shell → Host

База main ed2b79f5b2d24fe387dff4e161032313e54f9655,
tree e3811bcb11d27311f1cb5e8127f35be205af6c67. Post-merge Rust/MSVC
[39213090749](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/39213090749)
и Legacy [39213090732](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/39213090732)
завершились SUCCESS. Открытых PR при старте нет; новая ветка rust/typed-shell-host.

[ADR 0015](adr/0015-shell-host-api.ru.md): typed Shell requests/generated replies,
проверка status/parameters, общий bounded worker. Устранены missing value→false,
layer default/truncation и connected=true без status data. Console/autostart
получают actual Host truth. Внешний protobuf и SetLayer toggle прежние.

Проверки: 12 операций и pairing, malformed/default status, missing/duplicate zones,
missing layers, Layer/Selection bounds, boot reset/fault, stale metrics, navigation
Unknown, typed worker close. Existing E14 backpressure/completion и Windows Shell
reconnect/consume tests сохранены. Exact-head MSVC/baseline/mapping/differential/
browser/MSI results фиксируются в PR после фактического завершения workflows.

Следующие инкременты: typed logging owner/result, emulator response payloads,
dashboard snapshots; затем app --selftest/--landiag и stdin/REPL characterization.
D22, dormant capture-to-file и external no-op OPC DA остаются открытыми решениями.
SDK/AddIn/COM/Npcap, Windows X/Open drafts/scroll/selection, physical input/DPI/session,
install/reboot/rollback/offline и measurements — отдельные NOT_RUN gates.
