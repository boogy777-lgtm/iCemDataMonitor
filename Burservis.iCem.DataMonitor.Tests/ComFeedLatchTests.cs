using System;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

/// <summary>
/// D1 first-valid-wins latch law (bus register map v5, reg 0) — vendor-free
/// state machine tests.
/// </summary>
public class ComFeedLatchTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    [Fact]
    public void FirstLineLatches_OnFirstValidDevice()
    {
        var latch = new ComFeedLatch();
        Assert.False(latch.IsLatched);
        Assert.Equal(0, latch.PortNumber);

        Assert.True(latch.OnLine("COM1", 1)); // first device delivering ⇒ wins
        Assert.True(latch.IsLatched);
        Assert.Equal(1, latch.PortNumber);
    }

    [Fact]
    public void LinesFromOtherDevices_AreDiscarded_WhileLatched()
    {
        var latch = new ComFeedLatch();
        Assert.True(latch.OnLine("COM1", 1));
        Assert.True(latch.OnLine("COM1", 1)); // latched device keeps flowing

        Assert.False(latch.OnLine("COM2", 2)); // D1: second device DISCARDED
        Assert.False(latch.OnLine("COM3", 3));
        Assert.Equal(1, latch.PortNumber);     // latch unchanged
    }

    [Fact]
    public void LatchedDeviceLines_RefreshEvidence()
    {
        var latch = new ComFeedLatch();
        Assert.True(latch.OnLine("COM1", 1));
        DateTime first = latch.LastEvidenceUtc;
        Assert.True(first != DateTime.MinValue);

        Assert.True(latch.OnLine("COM1", 1));
        Assert.True(latch.LastEvidenceUtc >= first);
    }

    [Fact]
    public void Release_ReArbitrates_NextLineWins()
    {
        var latch = new ComFeedLatch();
        Assert.True(latch.OnLine("COM1", 1));
        latch.Release();

        Assert.False(latch.IsLatched);
        Assert.Equal(0, latch.PortNumber);

        Assert.True(latch.OnLine("COM2", 2)); // re-arbitration: first valid wins again
        Assert.Equal(2, latch.PortNumber);
        Assert.False(latch.OnLine("COM1", 1)); // old device now discarded
    }

    [Fact]
    public void IsSilentBeyond_TracksEvidenceWindow()
    {
        var latch = new ComFeedLatch();
        Assert.True(latch.OnLine("COM1", 1));
        DateTime now = latch.LastEvidenceUtc;

        Assert.False(latch.IsSilentBeyond(now, Idle));
        Assert.False(latch.IsSilentBeyond(now.AddSeconds(5), Idle));   // boundary: not beyond
        Assert.True(latch.IsSilentBeyond(now.AddSeconds(5).AddTicks(1), Idle));
    }

    [Fact]
    public void IsSilentBeyond_NotLatched_AlwaysFalse()
    {
        var latch = new ComFeedLatch();
        Assert.False(latch.IsSilentBeyond(DateTime.UtcNow.AddDays(1), Idle));
    }

    [Fact]
    public void NullOrEmptyKey_NeverLatches()
    {
        var latch = new ComFeedLatch();
        Assert.False(latch.OnLine(null, 1));
        Assert.False(latch.OnLine("", 1));
        Assert.False(latch.IsLatched);
    }

    [Fact]
    public void Release_IsIdempotent()
    {
        var latch = new ComFeedLatch();
        Assert.True(latch.OnLine("COM1", 1));
        latch.Release();
        latch.Release();
        Assert.False(latch.IsLatched);
        Assert.True(latch.OnLine("COM1", 1)); // clean re-latch
        Assert.Equal(1, latch.PortNumber);
    }
}
