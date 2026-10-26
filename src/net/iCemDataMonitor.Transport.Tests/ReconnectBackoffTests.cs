using iCemDataMonitor.Transport;
using Xunit;

namespace iCemDataMonitor.Transport.Tests;

/// <summary>
/// D.2.4 reconnect policy: delay table 3→6→12→24 cap 60, jitter ±10%,
/// FSM Connected→Lost→Reconnecting→Connected with state-change events.
/// Deterministic via injected jitter seam (§15.7 whitelist (a)).
/// </summary>
public class ReconnectBackoffTests
{
    [Fact]
    public void DelayTable_ExactSequence_WithZeroJitter()
    {
        var b = new ReconnectBackoff(() => 0.0); // factor = 0.9 exactly

        Assert.Equal(TimeSpan.FromSeconds(2.7), b.NextDelay());   // 3 × 0.9
        Assert.Equal(TimeSpan.FromSeconds(5.4), b.NextDelay());   // 6 × 0.9
        Assert.Equal(TimeSpan.FromSeconds(10.8), b.NextDelay());  // 12 × 0.9
        Assert.Equal(TimeSpan.FromSeconds(21.6), b.NextDelay());  // 24 × 0.9
        Assert.Equal(TimeSpan.FromSeconds(54.0), b.NextDelay());  // cap 60 × 0.9
        Assert.Equal(TimeSpan.FromSeconds(54.0), b.NextDelay());  // stays capped
    }

    [Fact]
    public void DelayTable_CapAt60_NeverExceeds()
    {
        var b = new ReconnectBackoff(() => 1.0); // factor = 1.1 exactly
        for (int i = 0; i < 4; i++) _ = b.NextDelay();

        var capped = b.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(66.0), capped); // 60 × 1.1 — table exhausted, base stays 60

        // ...and many more attempts never grow beyond the cap+jitter.
        for (int i = 0; i < 50; i++)
            Assert.True(b.NextDelay() <= TimeSpan.FromSeconds(66.0));
    }

    [Fact]
    public void Jitter_StaysWithinTenPercent_ForSeededRandom()
    {
        var random = new Random(20260824);
        var b = new ReconnectBackoff(random);

        int[] bases = { 3, 6, 12, 24, 60, 60 };
        foreach (var expectedBase in bases)
        {
            var d = b.NextDelay();
            var lo = TimeSpan.FromSeconds(expectedBase * 0.9 - 0.001);
            var hi = TimeSpan.FromSeconds(expectedBase * 1.1 + 0.001);
            Assert.InRange(d, lo, hi);
        }
    }

    [Fact]
    public void ReportConnected_ResetsSequence()
    {
        var b = new ReconnectBackoff(() => 0.5);
        _ = b.NextDelay(); // attempt 1 consumed (3s)
        b.ReportLost();
        _ = b.NextDelay();
        b.ReportConnected();

        // After a successful reconnect the sequence restarts at 3s.
        Assert.Equal(TimeSpan.FromSeconds(3.0), b.NextDelay());
    }

    [Fact]
    public void Fsm_Connected_Lost_Reconnecting_Connected_Events()
    {
        var b = new ReconnectBackoff(() => 0.5);
        var seen = new List<ReconnectState>();
        b.StateChanged += seen.Add;

        Assert.Equal(ReconnectState.Connected, b.State);

        b.ReportLost();
        Assert.Equal(ReconnectState.Lost, b.State);

        _ = b.NextDelay(); // scheduling the first retry moves to Reconnecting
        Assert.Equal(ReconnectState.Reconnecting, b.State);

        b.ReportConnected();
        Assert.Equal(ReconnectState.Connected, b.State);

        Assert.Equal(
            new[] { ReconnectState.Lost, ReconnectState.Reconnecting, ReconnectState.Connected },
            seen);
    }

    [Fact]
    public void ReportLost_Twice_RaisesLostOnce()
    {
        var b = new ReconnectBackoff(() => 0.5);
        var count = 0;
        b.StateChanged += _ => count++;

        b.ReportLost();
        b.ReportLost(); // same state → no duplicate event
        Assert.Equal(1, count);
    }
}
