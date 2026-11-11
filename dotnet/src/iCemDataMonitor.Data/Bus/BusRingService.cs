using Microsoft.Extensions.Hosting;

namespace iCemDataMonitor.Data.Bus;

/// <summary>
/// 1 Hz ring sampler reads the latest acquisition publication. Null/stale and
/// already ingested successful capture sequences are skipped; new captures with
/// identical words/timestamps are still ingested. It never captures the source.
///
/// web_on gate: Э4 INV-32 — "the sampler runs when web_on=1". The GATE lives
/// in host composition (conditional registration — not this file): this service
/// is gate-agnostic, Start/Stop is the whole contract, one loop task, thread-
/// safe (RingHistory/BusAddrRingService are thread-safe by themselves).
///
/// TEST SEAM §15.7 whitelist (a) — data seam, ¬ code seam: the tick INTERVAL is
/// injected (ctor, default 1 s); tests pass milliseconds so two ticks land
/// without sleeping seconds.
/// </summary>
public sealed class BusRingService : BackgroundService
{
    /// <summary>Production tick: 1 Hz (600 s history @ 600 entries).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly ILatestBusState _latest;
    private readonly BusAddrRingService _mapper;
    private readonly TimeSpan _interval;
    private long _lastSequence;

    /// <summary>Create the ring service over the shared reader and mapper.</summary>
    /// <param name="latest">Shared bus read surface (also used by telemetry/health pollers).</param>
    /// <param name="mapper">Addr-keyed sampler bound to the RingHistory the endpoint queries.</param>
    /// <param name="interval">Tick period; null ⇒ 1 Hz. Test seam: inject a short interval.</param>
    public BusRingService(ILatestBusState latest, BusAddrRingService mapper, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(mapper);
        _latest = latest;
        _mapper = mapper;
        _interval = interval.GetValueOrDefault(DefaultInterval);
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), _interval, "tick interval must be positive");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sample-then-delay (MetricsSampler law): the first tick lands in the background,
        // then one tick per interval until StopAsync cancels.
        while (!stoppingToken.IsCancellationRequested)
        {
            TickOnce();
            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // StopAsync — normal shutdown
            }
        }
    }

    internal void TickOnce()
    {
        var state = _latest.Current;
        var snapshot = state.Snapshot;
        if (snapshot is null || snapshot.Stale || state.SuccessfulCaptureSequence == _lastSequence)
            return; // no data / frozen last-good — skip the tick, never re-stamp garbage

        _mapper.Ingest(snapshot);
        _lastSequence = state.SuccessfulCaptureSequence;
    }
}
