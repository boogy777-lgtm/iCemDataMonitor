using Microsoft.Extensions.Hosting;

namespace iCemDataMonitor.Data.Bus;

/// <summary>A coherent immutable publication; timestamps use the acquisition TimeProvider's monotonic clock.</summary>
public sealed record LatestBusState(
    BusSnapshot? Snapshot,
    bool IsOpen,
    string? LastError,
    long? AttemptStartedTimestamp,
    long? CompletedTimestamp,
    long? LastSuccessfulCaptureTimestamp,
    long SuccessfulCaptureSequence);

/// <summary>Read-only, nonblocking bus state. Reading never opens or captures the source.</summary>
public interface ILatestBusState
{
    LatestBusState Current { get; }
}

// Native boundary seam for lifecycle/blocked-capture tests. No consumer receives this surface.
internal interface IBusCaptureSource
{
    BusSnapshot? Snapshot();
    BusSnapshot? LastSnapshot { get; }
    bool IsOpen { get; }
    string? LastError { get; }
}

/// <summary>Single host owner of sequential BusReader captures. DI owns and disposes the reader after StopAsync joins.</summary>
public sealed class BusAcquisitionService : BackgroundService, ILatestBusState
{
    private readonly IBusCaptureSource _source;
    private readonly TimeSpan _period;
    private readonly TimeSpan _expiry;
    private readonly TimeProvider _clock;
    private readonly long _created;
    private readonly object _captureGate = new();
    private LatestBusState _current = new(null, false, null, null, null, null, 0);
    private bool _stopped;
    private int _disposed;

    public BusAcquisitionService(BusReader reader, TimeSpan period)
        : this((IBusCaptureSource)reader, period, TimeProvider.System) { }

    internal BusAcquisitionService(IBusCaptureSource source, TimeSpan period, TimeProvider clock)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _expiry = period + TimeSpan.FromMilliseconds(BusReader.MutexTimeoutMs);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _created = clock.GetTimestamp();
    }

    public LatestBusState Current
    {
        get
        {
            var state = Volatile.Read(ref _current);
            if (_clock.GetElapsedTime(state.CompletedTimestamp ?? _created, _clock.GetTimestamp()) <= _expiry)
                return state;
            return state with
            {
                Snapshot = AsStale(state.Snapshot),
                LastError = state.LastError ?? "Bus acquisition publication expired",
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Also guarantees asynchronous startup on runtimes where ExecuteAsync starts inline.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                CaptureOnce();
                await Task.Delay(_period, _clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_captureGate)
            {
                _stopped = true;
                var state = Volatile.Read(ref _current);
                Volatile.Write(ref _current, state with
                {
                    Snapshot = AsStale(state.Snapshot), IsOpen = false, LastError = "Bus acquisition stopped",
                });
            }
        }
    }

    // Explicit capture seam for deterministic fixtures; never used by consumers.
    internal void CaptureOnce()
    {
        lock (_captureGate)
        {
            if (_stopped) return;
            var prior = Volatile.Read(ref _current);
            var started = _clock.GetTimestamp();
            var before = _source.LastSnapshot;
            string? error = null;
            try { _source.Snapshot(); }
            catch (Exception ex) { error = ex.Message; } // includes sticky ACL: publish diagnosis, keep host alive
            var completed = _clock.GetTimestamp();
            var after = _source.LastSnapshot;
            bool fresh = after is not null && !ReferenceEquals(before, after);
            Volatile.Write(ref _current, new LatestBusState(
                fresh ? after : AsStale(prior.Snapshot),
                _source.IsOpen,
                fresh ? null : error ?? _source.LastError ?? "Bus capture did not produce a new snapshot",
                started, completed,
                fresh ? completed : prior.LastSuccessfulCaptureTimestamp,
                prior.SuccessfulCaptureSequence + (fresh ? 1 : 0)));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // A host deadline cannot safely turn a synchronous native wait into concurrent Reader disposal.
        await base.StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_captureGate)
        {
            _stopped = true;
            var state = Volatile.Read(ref _current);
            Volatile.Write(ref _current, state with
            {
                Snapshot = AsStale(state.Snapshot), IsOpen = false, LastError = "Bus acquisition stopped",
            });
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // DI can dispose an unstarted/partially started host too. Always join before
        // the factory-owned reader is disposed later in reverse creation order.
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        base.Dispose();
    }

    private static BusSnapshot? AsStale(BusSnapshot? snapshot)
        => snapshot is null || snapshot.Stale ? snapshot : snapshot with { Stale = true };
}
