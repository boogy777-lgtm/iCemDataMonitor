# Legacy C2 behavior and validation

L02 intentionally corrects observed gRPC status after a Modbus bind failure:
requested and persisted `modbus_on=1` no longer imply a bound socket. The
production factory probes publisher `IsBound` and sniffer `IsRunning` (missing
services are false). Sniffer STARTING/RUNNING policy and availability-by-service
registration remain unchanged. One immutable capture copies requested plans and
samples each observed probe once; REST/proto layer projections share it. Web is
a next-start plan and OPC DA remains an external plan. REST keys and witness
stats are preserved. StationHealth/MMF sensor-at-send is outside this change.

Portable acceptance: `LayerRuntimeStatusTests` and
`SnifferWitnessSurfaceGuardTests`. Windows MMF, pipe, UI and MSI acceptance must
run on Windows; portable tests do not establish full platform parity.

L04 intentionally queues concurrent mutation commands through one controller
`SemaphoreSlim`, replacing the racy Busy rejection. Toggle, SetSetAsDefault and
RefreshIni share admission and ownership through actuator acknowledgement,
requested state update, persistence and final state. Status reads use only short
locks. StateChanged runs after gate release so reentrant callbacks can read and
submit commands. Existing synchronous controller methods are thin adapters.
Production actuators and gRPC SetLayer await Task APIs directly.

Cancellation before admission (including while queued) has no effects and
preserves the previous LastError. After admission, an operation finishes without
passing request cancellation to publisher lifetime loops. RPC desired_state is
still ignored: the existing requested/session toggle law remains. Web has no
actuator and OPC DA remains a no-op. Modbus actuator failure attempts the inverse
operation; sniffer failure does not. Failed persistence retains applied requested
and observed runtime changes but keeps the last successful Ini projection.
Successful SetSetAsDefault now clears stale LastError (intentional correction).
RefreshIni retains strict exception propagation on load failure and releases the
gate while recording the error.

Portable Host acceptance: 14 tests passed (`LayerRuntimeStatusTests`,
`LayerCommandTests`, `SnifferWitnessSurfaceGuardTests`) on Linux/.NET 10.0.302.
Controlled task acknowledgements cover queued toggles/save/refresh, accessible
Busy status, queued and admitted cancellation, rollback, deterministic save
failure, subsequent recovery, reentrant callbacks, and RPC toggle/Web/OPC DA.
The RPC fixture uses an unopened BusReader and does not exercise named MMF.

Existing `UI.Tests/LayerRuntimeControllerTests`: 14/14 passed on the same Linux
runner (pure controller tests; this does not exercise WinUI or Windows APIs).
Builds for Host.Tests and UI.Tests completed successfully with no new dependencies.
