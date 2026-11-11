using Grpc.Core;
using iCemDataMonitor.Contracts;

namespace iCemDataMonitor.HostClient;

/// <summary>
/// W21.4 adapter: one live SubscribeBootStages server-stream pushed onto
/// events (same doctrine as MetricsStream — the transport layer must not
/// depend on Rx; the splashscreen consumes plain events on its own thread and
/// marshals to the UI thread itself).
/// </summary>
internal sealed class BootStageStream : IBootStageStream
{
    private readonly AsyncServerStreamingCall<BootStage> _call;
    private readonly CancellationTokenSource _cts = new();

    public event Action<BootStage>? Stage;
    public event Action<Exception>? Failed;

    public BootStageStream(AsyncServerStreamingCall<BootStage> call)
    {
        _call = call;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var stage in _call.ResponseStream.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                Stage?.Invoke(stage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed?.Invoke(ex);
        }
        catch (OperationCanceledException)
        {
            // Dispose() — the normal termination path.
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
        _call.Dispose();
        _cts.Dispose();
    }
}
