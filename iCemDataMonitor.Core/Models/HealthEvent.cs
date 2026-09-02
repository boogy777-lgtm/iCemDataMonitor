namespace iCemDataMonitor.Core.Models;

/// <summary>
/// Health check snapshot emitted by HealthCheckService every 5s.
/// Consumed by monitoring infrastructure — Core never inspects payload.
/// </summary>
public readonly record struct HealthEvent(
    DateTime Timestamp,
    bool DbOk,
    bool IcemOk
);
