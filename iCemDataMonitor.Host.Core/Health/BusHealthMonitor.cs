using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Host.Core.Health;

/// <summary>Health v2 zone status: activeFlag + age of the last writer tick.</summary>
public readonly record struct ZoneHealth(bool Active, long AgeMs);

/// <summary>
/// Immutable health v2 snapshot consumed by /api/status (web_on only).
/// AddInAlive = bus reg 3 (heartbeat) ADVANCING (liveness of the iCem TRUTH,
/// AC3 — NOT POC liveness). No payload inspection: liveness signals only (A2).
/// CaseLanState = header reg 2 (BusLayout.RegCaseLanState, tap-exposed LAN/cRIO
/// state) — the HONEST surface; the former LanStatus field read header reg 8,
/// which is a com_stream float-pair low word in v5 (garbage rendered as LAN
/// state — dishonest-surface fix 2026-08-28).
/// </summary>
public sealed record StationHealth(
    bool BusOpen,
    bool AddInAlive,
    short Heartbeat,
    short ComPort,
    short CaseLanState,
    short CaseState,
    ZoneHealth Com,
    ZoneHealth Lan,
    ZoneHealth Raw,
    DateTime UpdatedAtUtc);

/// <summary>Volatile holder for the latest StationHealth — lock-free reads from the endpoint.</summary>
public sealed class StationHealthState
{
    private volatile StationHealth _current = new(
        BusOpen: false, AddInAlive: false, Heartbeat: 0, ComPort: 0, CaseLanState: 0, CaseState: 0,
        Com: default, Lan: default, Raw: default, UpdatedAtUtc: DateTime.UtcNow);

    public StationHealth Current => _current;

    public void Update(StationHealth health) => _current = health;
}

/// <summary>
/// Health v2 probe (W5): samples the bus at 1 Hz and derives liveness from
/// header reg 3 (heartbeat) advancement. The heartbeat ticks from the AddIn
/// inside iCem — it keeps ticking while the POC is dead (AC3), which is exactly
/// the signal the operator wants: "is the TRUTH source alive".
///
/// AddInAlive semantics: reg 3 must change within the last 3 samples (3 s).
/// First sample establishes the baseline (alive = false until movement seen).
/// A stale/null snapshot never flips liveness — it reflects staleness only.
///
/// STALE-DECAY LAW (2026-08-28): a STALE snapshot (mutex-timeout last-good or
/// the file-absent degrade, BUS-LIFECYCLE rows #11/#12) still carries REAL
/// zone ticks — so the AGE-GATED zone verdicts are RECOMPUTED on every probe
/// (age keeps growing ⇒ a zone goes inactive after 5 s naturally, BusZoneInfo.
/// IsActive). Frozen are ONLY the values that CANNOT be recomputed from a
/// frozen copy: the heartbeat-movement verdict (AddInAlive), the register
/// VALUES (Heartbeat/ComPort/CaseLanState — last-good readings). Pre-fix the
/// whole verdict froze, so after Data.dat vanished LAN stayed «active» forever.
/// </summary>
public sealed class BusHealthMonitor : BackgroundService
{
    private const int MaxStagnantSamples = 3; // 3 s window without reg 3 movement ⇒ AddIn dead

    private readonly ILatestBusState _latest;
    private readonly StationHealthState _state;
    private readonly ILogger<BusHealthMonitor> _logger;
    private readonly TimeProvider _timeProvider;

    private short _lastHeartbeat;
    private bool _haveBaseline;
    private int _stagnantSamples;

    public BusHealthMonitor(ILatestBusState latest, StationHealthState state, ILogger<BusHealthMonitor> logger)
        : this(latest, state, logger, TimeProvider.System)
    {
    }

    internal BusHealthMonitor(ILatestBusState latest, StationHealthState state, ILogger<BusHealthMonitor> logger,
        TimeProvider timeProvider)
    {
        _latest = latest;
        _state = state;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[health] Bus heartbeat monitor started (liveness = reg 3 advancing, 3 s window)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Probe();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[health] Probe iteration failed");
            }

            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("[health] Bus heartbeat monitor stopped");
    }

    /// <summary>
    /// One 1 Hz health iteration. INTERNAL for tests (InternalsVisibleTo →
    /// iCemDataMonitor.Host.Tests): the verdicts are pinned directly against
    /// real BusReader fixtures without hosting the 1 Hz loop.
    /// </summary>
    internal void Probe()
    {
        var publication = _latest.Current;
        var snap = publication.Snapshot;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (snap is null)
        {
            // Never any data (no last-good copy at all): nothing can be
            // recomputed — hold the whole last verdict, reflect reader state.
            var last = _state.Current;
            _state.Update(last with
            {
                BusOpen = publication.IsOpen,
                UpdatedAtUtc = now,
            });
            return;
        }

        if (snap.Stale)
        {
            // STALE-DECAY LAW (2026-08-28): the frozen last-good copy still
            // carries REAL zone ticks — recompute the age-gated verdicts so a
            // zone decays to inactive after 5 s even though the snapshot never
            // refreshes (pre-fix: the whole verdict froze ⇒ LAN «active»
            // forever after Data.dat vanished). Freeze ONLY what cannot be
            // recomputed from a frozen copy: AddInAlive (heartbeat movement
            // needs fresh samples) and the register VALUES. Rows #11/#12
            // degrade semantics are untouched — the SNAPSHOT surface still
            // serves lastGood with Stale=true; this is verdict-side only.
            var last = _state.Current;
            _state.Update(last with
            {
                BusOpen = publication.IsOpen,
                Com = ToZoneHealth(snap.ComMain, now),
                Lan = ToZoneHealth(snap.Lan, now),
                Raw = ToZoneHealth(snap.RawReserve, now),
                UpdatedAtUtc = now,
            });
            return;
        }

        bool alive;
        if (!_haveBaseline)
        {
            _haveBaseline = true;
            _stagnantSamples = 0;
            alive = false; // conservative until movement is observed
        }
        else if (snap.Heartbeat != _lastHeartbeat)
        {
            _stagnantSamples = 0;
            alive = true;
        }
        else
        {
            _stagnantSamples++;
            alive = _stagnantSamples < MaxStagnantSamples;
        }
        _lastHeartbeat = snap.Heartbeat;

        _state.Update(new StationHealth(
            BusOpen: publication.IsOpen,
            AddInAlive: alive,
            Heartbeat: snap.Heartbeat,
            ComPort: snap.ComPort, // v4: renamed ActiveComPort → ComPort (reg 0 unchanged)
            CaseLanState: snap.CaseLanState, // FIX 2026-08-28: header reg 2 (tap-exposed LAN/cRIO state) — was snap.LanStatus (reg 8 = com_stream float-pair low word, garbage)
            CaseState: snap.CaseState,
            Com: ToZoneHealth(snap.ComMain, now),
            Lan: ToZoneHealth(snap.Lan, now),
            Raw: ToZoneHealth(snap.RawReserve, now),
            UpdatedAtUtc: now));
    }

    private static ZoneHealth ToZoneHealth(BusZoneInfo zone, DateTime now)
    {
        // W10.1: the zone's activeFlag is the writer's OWN claim (it can stay 1
        // forever after a writer death). The REAL liveness = UTC-tick age — the
        // contract says writers tick every 1 s, so a zone older than 5 s is NOT
        // active no matter what the stale flag claims (lamps must follow truth).
        // THE law lives on BusZoneInfo.IsActive (ONE predicate + ONE constant
        // for every surface — health, REST mirrors, truth-table rows) and is
        // recomputable from stale snapshots (stale-decay law, Probe doc).
        return new ZoneHealth(zone.IsActiveAt(now), zone.AgeMs(now));
    }
}

