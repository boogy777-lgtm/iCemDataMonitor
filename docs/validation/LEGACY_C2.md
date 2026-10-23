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
