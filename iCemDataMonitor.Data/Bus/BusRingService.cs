using Microsoft.Extensions.Hosting;

namespace iCemDataMonitor.Data.Bus;

/// <summary>
/// 1 Hz ring sampler loop (Э1 topology: RING[BusRingService 600s]) — reads the
/// shared <see cref="BusReader"/> once per tick and pushes the snapshot through
/// <see cref="BusAddrRingService"/> into <see cref="RingHistory"/>. HOME STYLE:
/// MetricsSampler's sample-then-delay producer loop behind a BackgroundService
/// Start/Stop shell (AceSnifferService style).
///
/// Tick discipline (no fallback garbage):
///   - a FRESH Snapshot() every tick (sensor-at-send law — MetricsSampler);
///     a cached LastSnapshot would re-ingest the same frozen words every tick;
///   - snapshot null (bus never openable) or Stale (mutex-timeout last-good)
///     ⇒ the tick is SKIPPED — the ring never stores re-stamped frozen data;
///   - the loop keeps polling either way (the reader dedup-logs open errors).
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

    private readonly BusReader _reader;
    private readonly BusAddrRingService _mapper;
    private readonly TimeSpan _interval;

    /// <summary>Create the ring service over the shared reader and mapper.</summary>
    /// <param name="reader">Shared bus read surface (also used by telemetry/health pollers).</param>
    /// <param name="mapper">Addr-keyed sampler bound to the RingHistory the endpoint queries.</param>
    /// <param name="interval">Tick period; null ⇒ 1 Hz. Test seam: inject a short interval.</param>
    public BusRingService(BusReader reader, BusAddrRingService mapper, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(mapper);
        _reader = reader;
        _mapper = mapper;
        _interval = interval.GetValueOrDefault(DefaultInterval);
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), _interval, "tick interval must be positive");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sample-then-delay (MetricsSampler law): the first tick lands during
        // StartAsync, then one tick per interval until StopAsync cancels.
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

    private void TickOnce()
    {
        var snapshot = _reader.Snapshot(); // fresh copy; internally falls back to last-good on open failure
        if (snapshot is null || snapshot.Stale)
            return; // no data / frozen last-good — skip the tick, never re-stamp garbage

        _mapper.Ingest(snapshot);
    }
}
