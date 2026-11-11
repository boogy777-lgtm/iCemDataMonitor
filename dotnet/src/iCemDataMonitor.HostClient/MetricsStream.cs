using Grpc.Core;
using iCemDataMonitor.Contracts;

namespace iCemDataMonitor.HostClient;

/// <summary>
/// Event-pump adapter over the gRPC server-streaming call. A clean stream end
/// and an error are BOTH link-loss signals for the reconnect FSM (the sampler
/// only ends a stream when the client cancels or the host dies/fail-fasts).
/// </summary>
internal sealed class MetricsStream : IMetricsStream
{
    private readonly AsyncServerStreamingCall<MetricsSample> _call;
    private readonly CancellationTokenSource _cts = new();

    public event Action<MetricsSample>? Sample;
    public event Action<Exception>? Failed;

    public MetricsStream(AsyncServerStreamingCall<MetricsSample> call)
    {
        _call = call;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var sample in _call.ResponseStream.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                Sample?.Invoke(sample);
            // Clean completion without cancellation = the host went away.
            Failed?.Invoke(new InvalidOperationException("metrics stream completed by the server"));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // cancelled by our own Dispose — not a failure
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && _cts.IsCancellationRequested)
        {
            // ditto
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _call.Dispose();
        }
        catch
        {
            // best effort — disposal races with the pump are expected
        }
        _cts.Dispose();
    }
}
