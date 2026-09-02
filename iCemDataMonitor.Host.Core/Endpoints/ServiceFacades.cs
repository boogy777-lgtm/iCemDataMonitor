using iCemDataMonitor.Transport;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Host.Core.Endpoints;

/// <summary>
/// Thin facades that wrap iCemDataMonitor.Transport concrete services for testability.
/// Registered in DI as singletons delegating to the real hosted services.
/// </summary>

public interface ISnifferService
{
    AceSnifferStatus GetStatus();
    IReadOnlyList<AceServiceVectorInfo> GetServiceVectors();
    IReadOnlyList<AceSignalPreview> GetSignalPreview(int vectorId);
    Task StartSnifferAsync();
    Task StopSnifferAsync();
    Task StartCaptureAsync(int? durationSeconds);
    Task StopCaptureAsync();
    AceCaptureStatus GetCaptureStatus();
    string? GetCaptureFilePath();
}

public sealed class SnifferServiceFacade : ISnifferService
{
    private readonly AceSnifferService _inner;
    public SnifferServiceFacade(AceSnifferService inner) => _inner = inner;
    public AceSnifferStatus GetStatus() => _inner.GetStatus();
    public IReadOnlyList<AceServiceVectorInfo> GetServiceVectors() => _inner.GetServiceVectors();
    public IReadOnlyList<AceSignalPreview> GetSignalPreview(int vectorId) => _inner.GetSignalPreview(vectorId);
    public Task StartSnifferAsync() => _inner.StartSnifferAsync();
    public Task StopSnifferAsync() => _inner.StopSnifferAsync();
    public Task StartCaptureAsync(int? durationSeconds) => _inner.StartCaptureAsync(durationSeconds);
    public Task StopCaptureAsync() => _inner.StopCaptureAsync();
    public AceCaptureStatus GetCaptureStatus() => _inner.GetCaptureStatus();
    public string? GetCaptureFilePath() => _inner.GetCaptureFilePath();
}

public interface ITeeService
{
    ComTeeStatus GetStatus();
    Task StartManuallyAsync(CancellationToken ct = default);
    Task StopManuallyAsync();
}

public sealed class TeeServiceFacade : ITeeService
{
    private readonly SerialTeeService _inner;
    public TeeServiceFacade(SerialTeeService inner) => _inner = inner;
    public ComTeeStatus GetStatus() => _inner.GetStatus();
    public Task StartManuallyAsync(CancellationToken ct = default) => _inner.StartManuallyAsync(ct);
    public Task StopManuallyAsync() => _inner.StopManuallyAsync();
}

/// <summary>No-op ITeeService used when Tee is disabled in config.</summary>
public sealed class DisabledTeeService : ITeeService
{
    public ComTeeStatus GetStatus() => new()
    {
        Up = false, PhysicalPort = "", Baud = 0, State = "STOPPED"
    };
    public Task StartManuallyAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopManuallyAsync() => Task.CompletedTask;
}
