# Compatibility contract registry

- [Canonical protobuf](../../iCemDataMonitor.Contracts/Protos/telemetry.proto): one source for Rust/.NET; frozen package/tags/oneof/reserved.
- [Current OpenAPI](http/openapi.json): four active REST GET routes, implemented by Rust Host; unchanged by C11.
- [Pinned route inventory](http/route-inventory.json): source evidence at `96e2206562c9389d1543095232115d81eae4b692`, not a current tree inventory.
- [API contracts](../docs/API_SPEC.ru.md): MMF, Modbus, CLI, emulator control, IPC.
- [Source manifest](../migration/source-manifest.json): immutable pinned source inventory/hashes.

[C11 retirement](../../docs/LEGACY_REFACTORING_C11.ru.md) removes the former
21 dormant routes and serial admin wrappers. Their current status is RETIRED,
not an outstanding migration task. The JSON inventory remains historical evidence.
Active `/api/status`, `/api/status/summary`, `/api/regs`, `/api/regs/history` are unchanged.

SetLayer keeps the accepted v1 toggle semantics and ignores desired_state; do not
retry it blindly. Rust generation reads the canonical proto directly.
