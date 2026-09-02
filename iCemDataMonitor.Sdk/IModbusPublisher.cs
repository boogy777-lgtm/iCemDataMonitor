using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Sdk;

/// <summary>
/// Modbus TCP publisher surface (Bus v3 — TZ_ARCH_V2 §5 layers.modbus, W3).
///
/// v3 contract (dynamic-regCount law): the register file is RUNTIME-SIZED from
/// the bus header regCount (default dictionary 10001 HR — v5, D3) and refreshed
/// by a 1 Hz pure-word mirror of the bus snapshot — the publisher needs NO
/// runtime configuration (ModbusTcpPublisher.ConfigureAsync is an inert no-op).
///
/// ConfigureAsync is retained ONLY as the v1 pipeline contract surface
/// (iCemDataMonitor.Core.PipelineWorker config-activation path, covered by
/// iCemDataMonitor.Core.Tests.ContractTests). The v1 pipeline is not wired into any
/// host (CoreServicesExtensions has no call sites); when it is removed, this
/// member goes with it. ¬ [Obsolete]: keeping the attribute would make every
/// v1-pipeline caller emit CS0618 for code we still deliberately test.
/// </summary>
public interface IModbusPublisher
{
    /// <summary>TRUE while the TCP listener is actually bound (W10.1: the lamp
    /// must reflect the socket, not the ini flag — a startup bind failure or a
    /// runtime stop flips this to false).</summary>
    bool IsBound { get; }

    Task StartAsync(CancellationToken ct = default);

    /// <summary>v1 pipeline no-op — v3 publishers mirror the bus, ¬ a configured map.</summary>
    Task ConfigureAsync(IReadOnlyList<ModbusRegisterEntry> registerMap);

    Task StopAsync();
}
