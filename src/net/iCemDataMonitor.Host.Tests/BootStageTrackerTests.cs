using System.Threading.Channels;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Host.Boot;

namespace iCemDataMonitor.Host.Tests;

// W21.3 unit coverage of the boot-stage ring buffer (splashscreen mirror).
public class BootStageTrackerTests
{
    [Fact]
    public void Begin_Complete_Snapshot_OrderOrdinalsAndFlags()
    {
        var t = new BootStageTracker();
        t.Begin("ini", "конфигурация");
        t.Begin("composition", "композиция");
        t.Complete("ini", "port=8090");

        var snap = t.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal(1u, snap[0].Ordinal);
        Assert.Equal(2u, snap[1].Ordinal);
        Assert.True(snap[0].Completed);
        Assert.Equal("port=8090", snap[0].Detail);
        Assert.False(snap[1].Completed); // begun but not completed → unchecked box
    }

    [Fact]
    public void Complete_UnknownId_FailsLoud()
    {
        var t = new BootStageTracker();
        Assert.Throws<InvalidOperationException>(() => t.Complete("ghost"));
    }

    [Fact]
    public void Snapshot_IsACopy_NotLiveView()
    {
        var t = new BootStageTracker();
        t.Begin("ini", "конфигурация");
        var snap = t.Snapshot();
        t.Complete("ini");
        Assert.False(snap[0].Completed);          // old snapshot unchanged
        Assert.True(t.Snapshot()[0].Completed);   // live view updated
    }

    [Fact]
    public async Task Subscribe_ReplaysSnapshotThenDeliversLiveUpdates()
    {
        var t = new BootStageTracker();
        t.Begin("ini", "конфигурация");
        t.Complete("ini");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = t.Subscribe(cts.Token);

        // Replay arrives first.
        var first = await reader.ReadAsync(cts.Token);
        Assert.Equal("ini", first.Id);
        Assert.True(first.Completed);

        // Live update after subscription.
        t.Begin("web", "веб-сервер");
        var second = await reader.ReadAsync(cts.Token);
        Assert.Equal("web", second.Id);
        Assert.False(second.Completed);

        // Updated record for the SAME id is re-published (checkbox flip).
        t.Complete("web", ":8090");
        var third = await reader.ReadAsync(cts.Token);
        Assert.Equal("web", third.Id);
        Assert.True(third.Completed);
    }

    [Fact]
    public async Task Subscribe_Cancelled_CompletesReader()
    {
        var t = new BootStageTracker();
        using var cts = new CancellationTokenSource();
        var reader = t.Subscribe(cts.Token);
        cts.Cancel();
        // Throws TimeoutException (test failure) if the reader never completes.
        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
