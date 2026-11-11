using System;

namespace iCemDataMonitor.Transport;

/// <summary>Connection FSM state for UI loss indicators (D.5): Connected is the
/// healthy state; Lost means the failure was just observed; Reconnecting means
/// a retry is scheduled/in flight.</summary>
public enum ReconnectState
{
    Connected,
    Lost,
    Reconnecting,
}

/// <summary>
/// D.2.4 client reconnect policy (transport zone, NO gRPC dependency — usable
/// by WinUI pipe clients and SPA-adjacent .NET consumers alike):
///   delays 3s → 6s → 12s → 24s, cap 60s, jitter ±10% (bounded — no jitter storm);
///   FSM Connected → Lost (indicator) → Reconnecting → Connected.
///
/// §15.7 whitelist (a) test-seam: the jitter source is injectable
/// (<see cref="Func{Double}"/> returning [0..1), or a seeded <see cref="Random"/>
/// via the convenience constructor). Production uses the parameterless ctor.
/// The class is pure: it computes delays and tracks state, it does NOT perform
/// I/O itself — the caller owns the connect attempt loop.
/// </summary>
public sealed class ReconnectBackoff
{
    /// <summary>Base delay table in seconds before the cap applies.</summary>
    public static readonly int[] StepsSeconds = { 3, 6, 12, 24 };

    /// <summary>Absolute delay ceiling (also the value used once the table runs out).</summary>
    public const int CapSeconds = 60;

    /// <summary>Jitter half-width: final delay ∈ [0.9 × base .. 1.1 × base].</summary>
    public const double JitterFraction = 0.10;

    private readonly Func<double> _jitter01;
    private readonly object _lock = new();
    private int _attempt;
    private ReconnectState _state = ReconnectState.Connected;

    /// <summary>Production constructor: unseeded Random jitter source.</summary>
    public ReconnectBackoff() : this(new Random()) { }

    /// <summary>Deterministic constructor: seeded Random (test seam).</summary>
    public ReconnectBackoff(Random jitterSource)
        : this(() => jitterSource?.NextDouble() ?? throw new ArgumentNullException(nameof(jitterSource)))
    {
        ArgumentNullException.ThrowIfNull(jitterSource);
    }

    /// <summary>Primary seam constructor: returns a double in [0..1).</summary>
    public ReconnectBackoff(Func<double>? jitter01)
    {
        _jitter01 = jitter01 ?? (() => Random.Shared.NextDouble());
    }

    /// <summary>Current FSM state (lock-free read; transitions are atomic).</summary>
    public ReconnectState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>Fires on every state change with the NEW state (UI indicator hook).</summary>
    public event Action<ReconnectState>? StateChanged;

    /// <summary>Report a successful connection: resets the backoff sequence.</summary>
    public void ReportConnected()
    {
        lock (_lock)
        {
            _attempt = 0;
            Transition(ReconnectState.Connected);
        }
    }

    /// <summary>Report a lost connection: resets the sequence and raises Lost
    /// exactly once per loss (the UI shows the "connection lost" banner here).</summary>
    public void ReportLost()
    {
        lock (_lock)
        {
            _attempt = 0;
            Transition(ReconnectState.Lost);
        }
    }

    /// <summary>
    /// Delay before the NEXT reconnect attempt: 3s→6s→12s→24s then capped at 60s,
    /// each value multiplied by jitter ∈ [0.9..1.1]. First call after
    /// <see cref="ReportLost"/> moves the FSM into <see cref="ReconnectState.Reconnecting"/>.
    /// </summary>
    public TimeSpan NextDelay()
    {
        lock (_lock)
        {
            var baseSeconds = _attempt < StepsSeconds.Length ? StepsSeconds[_attempt] : CapSeconds;
            _attempt++;

            if (_state == ReconnectState.Lost)
                Transition(ReconnectState.Reconnecting);

            // Bounded jitter: ±10% of the base step, never below 90% of it.
            var factor = 1.0 - JitterFraction + (2 * JitterFraction * Clamp01(_jitter01()));
            return TimeSpan.FromSeconds(baseSeconds * factor);
        }
    }

    private void Transition(ReconnectState next)
    {
        if (_state == next)
            return;
        _state = next;
        StateChanged?.Invoke(next);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
}
