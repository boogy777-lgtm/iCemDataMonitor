namespace iCemDataMonitor.Sdk;

/// <summary>
/// Modbus TCP publisher surface (Bus v3 — TZ_ARCH_V2 §5 layers.modbus, W3).
///
/// v3 contract (dynamic-regCount law): the register file is RUNTIME-SIZED from
/// the bus header regCount (default dictionary 10001 HR — v5, D3) and refreshed
/// by a 1 Hz pure-word mirror of the bus snapshot — the publisher needs NO
/// runtime configuration.
/// </summary>
public interface IModbusPublisher
{
    /// <summary>TRUE while the TCP listener is actually bound (W10.1: the lamp
    /// must reflect the socket, not the ini flag — a startup bind failure or a
    /// runtime stop flips this to false).</summary>
    bool IsBound { get; }

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();
}
