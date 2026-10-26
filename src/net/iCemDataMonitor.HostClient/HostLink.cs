using System.Reactive.Linq;
using System.Reactive.Subjects;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Transport;

namespace iCemDataMonitor.HostClient;

/// <summary>UI-facing mirror of Transport.ReconnectState (D.2.4 FSM).</summary>
public enum HostLinkState
{
    Connected,
    Lost,
    Reconnecting,
}

/// <summary>
/// D.5.1 connection manager for the WinUI clients: owns the metrics
/// subscription and replays the D.2.4 reconnect policy
/// (Transport.ReconnectBackoff: 3→6→12→24s, cap 60s, ±10% jitter).
///
/// FSM semantics (mirrors the server-side helper):
///   Connected → Lost (failure observed / no samples > StaleThreshold)
///             → Reconnecting (retry scheduled) → Connected …
/// The banner state is derived from State + AgeOfLastSample; stale data is
/// NEVER masked — consumers keep rendering the last known values.
///
/// Test seams (§15.7 (a)): clock, retry scheduler and jitter source are
/// injectable; the class performs no I/O itself beyond the IHostApi calls.
/// </summary>
public sealed class HostLink : IDisposable
{
    private readonly Func<IHostApi> _apiFactory;
    private readonly ReconnectBackoff _backoff;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<TimeSpan, Action> _scheduleRetry;

    private readonly Subject<MetricsSample> _samples = new();
    private readonly Subject<HostLinkState> _states = new();
    private readonly object _lock = new();

    private IHostApi? _api;
    private IMetricsStream? _stream;
    private HostLinkState _state = HostLinkState.Lost; // before Start() there is no link
    private DateTimeOffset _lastSampleAt;
    private bool _retryPending;
    private bool _disposed;

    /// <summary>No-data threshold for the Lost banner (D.5/B.2.2: >3 s).</summary>
    public TimeSpan StaleThreshold { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Interval requested from SubscribeMetrics (server clamps to ≥200 ms).</summary>
    public int MetricsIntervalMs { get; init; } = 1000;

    public IObservable<MetricsSample> Samples => _samples.AsObservable();
    public IObservable<HostLinkState> States => _states.AsObservable();

    public HostLinkState State
    {
        get { lock (_lock) return _state; }
    }

    public DateTimeOffset LastSampleAt
    {
        get { lock (_lock) return _lastSampleAt; }
    }

    public TimeSpan AgeOfLastSample => _clock() - LastSampleAt;

    public HostLink(
        Func<IHostApi>? apiFactory = null,
        Func<DateTimeOffset>? clock = null,
        Action<TimeSpan, Action>? scheduleRetry = null,
        Random? backoffJitter = null)
    {
        _apiFactory = apiFactory ?? (() => new GrpcHostClient());
        _clock = clock ?? (() => DateTimeOffset.Now);
        // Default scheduler = one-shot system timer. Tests inject an immediate
        // or captured executor.
        _scheduleRetry = scheduleRetry ?? ((delay, action) =>
            new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan));
        _backoff = backoffJitter is not null
            ? new ReconnectBackoff(backoffJitter)
            : new ReconnectBackoff();
    }

    /// <summary>Open the first metrics stream (called once by the owner).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_state == HostLinkState.Connected || _retryPending) return;
            OpenStreamLocked();
        }
    }

    // ── stream wiring ─────────────────────────────────────────────────────────

    private void OpenStreamLocked()
    {
        _stream?.Dispose();
        _stream = null;
        _api?.Dispose();

        var api = _apiFactory();
        var stream = api.SubscribeMetrics(MetricsIntervalMs);
        stream.Sample += OnSample;
        stream.Failed += OnStreamFailed;

        _api = api;
        _stream = stream;
    }

    private void OnSample(MetricsSample sample)
    {
        bool wasDisconnected;
        lock (_lock)
        {
            wasDisconnected = _state != HostLinkState.Connected;
            _lastSampleAt = _clock();
        }
        if (wasDisconnected)
        {
            // Successful data ⇒ link restored: resets the backoff sequence.
            _backoff.ReportConnected();
            SetState(HostLinkState.Connected);
        }
        _samples.OnNext(sample);
    }

    private void OnStreamFailed(Exception reason) => ScheduleReconnect(reason);

    // ── watchdog ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Called ~1 Hz by the owner: silent-stall detection (no error event but
    /// also no samples past the threshold). Errors are swallowed — the FSM
    /// transition itself is the signal.
    /// </summary>
    public void CheckStale()
    {
        Exception? trigger = null;
        lock (_lock)
        {
            if (!_retryPending && _state == HostLinkState.Connected &&
                _clock() - _lastSampleAt > StaleThreshold)
            {
                trigger = new TimeoutException(
                    $"no samples for more than {StaleThreshold.TotalSeconds:0}s");
            }
        }
        if (trigger is not null)
            ScheduleReconnect(trigger);
    }

    // ── reconnect scheduling ──────────────────────────────────────────────────

    private void ScheduleReconnect(Exception reason)
    {
        TimeSpan delay;
        Action retry;
        lock (_lock)
        {
            if (_disposed) return;

            // ReportLost ONLY on the Connected→Lost edge: calling it per failure
            // would reset the backoff sequence every time (= infinite 3 s retries).
            if (_state == HostLinkState.Connected)
                _backoff.ReportLost();

            SetState(HostLinkState.Lost);

            if (_retryPending)
                return;

            _retryPending = true;
            delay = _backoff.NextDelay(); // moves the backoff FSM to Reconnecting
            SetState(HostLinkState.Reconnecting);
            retry = () =>
            {
                lock (_lock)
                {
                    _retryPending = false;
                    if (_disposed) return;
                    OpenStreamLocked();
                }
            };
        }
        _scheduleRetry(delay, retry);
    }

    private void SetState(HostLinkState next)
    {
        bool changed;
        lock (_lock)
        {
            changed = _state != next;
            _state = next;
        }
        if (changed)
            _states.OnNext(next);
    }

    // ── unary passthroughs (throw when the link is down — callers surface it) ─

    private IHostApi Api()
    {
        var api = _api;
        if (api is null)
            throw new InvalidOperationException("host link is not started");
        return api;
    }

    public Task<HostStatus> GetStatusAsync(CancellationToken ct = default) =>
        Api().GetStatusAsync(ct);

    public Task<LayerState> SetLayerAsync(LayerKind layer, bool desired, CancellationToken ct = default) =>
        Api().SetLayerAsync(layer, desired, ct);

    // TOMBSTONE (2026-08-27): ControlEngineAsync passthrough REMOVED — the
    // emulator is a separate independent app, no engine control over the pipe.

    // TOMBSTONE (E.2.7): ShutdownHostAsync REMOVED — the host closes ONLY from
    // its own tray; no client can stop it over the wire.

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
            _api?.Dispose();
            _api = null;
        }
        _samples.Dispose();
        _states.Dispose();
    }
}
