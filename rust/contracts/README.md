# Compatibility contract registry

- proto/telemetry.proto: original bytes, package/tags/oneof/reserved unchanged.
- http/openapi.json: four currently mapped REST routes; contract, not an implemented server.
- http/route-inventory.json: source-defined routes with active/dormant distinction.
- ../docs/API_SPEC.ru.md: MMF, Modbus, CLI, emulator control, native IPC.
- ../migration/source-manifest.json: complete source inventory and Git blob hashes.

Do not evolve an API by silently changing legacy semantics. In particular, the current
SetLayer implementation toggles and ignores desired_state; a truly idempotent setter needs
an explicit compatibility decision. A generated Rust proto model is not added until M4.
