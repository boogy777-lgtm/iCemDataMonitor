using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// BusRingService (Э7 BusRingServiceTests: "ring history", 1 test): Start →
/// two+ injected-interval ticks really fill the RingHistory from the synthetic
/// bus (BusTestFactory seed + real BusReader — BusReaderTests precedent);
/// Stop halts the loop. §15.7 whitelist (a): the tick INTERVAL is the injected
/// data seam — 50 ms, the test never sleeps seconds; thresholds generous.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // bus MMF + mutexes are Windows-only by contract
public sealed class BusRingServiceTests : IDisposable
{
    private const int UnixSeconds = 1756300000; // pair 4..5 int32 raw bits
    private readonly string _dir;

    public BusRingServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bus_ring_service_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task StartStop_PollsBusIntoRingHistory_StopsClean()
    {
        var mmfPath = Path.Combine(_dir, "bus.dat");
        var mapName = $"RingSvcTestMap_{Guid.NewGuid():N}";
        var mutexName = $"RingSvcTestMutex_{Guid.NewGuid():N}";

        // Synthetic bus: unix raw bits at 4..5, floats at 6 and 200.
        BusTestFactory.CreateFile(mmfPath);
        BusTestFactory.WriteWord(mmfPath, 4, (ushort)(UnixSeconds & 0xFFFF));
        BusTestFactory.WriteWord(mmfPath, 5, (ushort)(UnixSeconds >> 16));
        BusTestFactory.WriteFloat(mmfPath, 6, 6.25f);
        BusTestFactory.WriteFloat(mmfPath, 200, 15.79f);

        using var reader = new BusReader(mmfPath, mapName, mutexName);
        Assert.True(reader.TryOpen(out var err), err);

        var history = new RingHistory();
        var mapper = new BusAddrRingService(history);
        var startedAt = DateTimeOffset.UtcNow;
        using var acquisition = new BusAcquisitionService(reader, TimeSpan.FromMilliseconds(20));
        acquisition.CaptureOnce();
        await acquisition.StartAsync(CancellationToken.None);
        using var service = new BusRingService(acquisition, mapper, TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);

        // Generous deadline: 50 ms ticks must land ≥ 2 points well within 5 s.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (history.QueryAddr(6).Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        await service.StopAsync(CancellationToken.None); // awaits the loop exit — ticks really stop
        await acquisition.StopAsync(CancellationToken.None);
        var stoppedAt = DateTimeOffset.UtcNow;

        // ── the loop really rang the bus into the history ──
        var pts6 = history.QueryAddr(6);
        Assert.True(pts6.Count >= 2, $"two sampler ticks did not reach the ring (got {pts6.Count})");
        Assert.All(pts6, p => Assert.Equal(6.25f, p.Value));

        var pts4 = history.QueryAddr(4);
        Assert.True(pts4.Count >= 2, "the unix pair 4..5 was not ringed");
        Assert.All(pts4, p => Assert.Equal((float)UnixSeconds, p.Value));

        var pts200 = history.QueryAddr(200);
        Assert.True(pts200.Count >= 2, "addr 200 was not ringed");
        Assert.All(pts200, p => Assert.Equal(15.79f, p.Value));

        // ── timestamps come from the ticks: ordered, within the run window ──
        for (int i = 1; i < pts6.Count; i++)
            Assert.True(pts6[i].TimestampUtc >= pts6[i - 1].TimestampUtc, "tick order violated");
        Assert.All(pts6, p =>
        {
            Assert.True(p.TimestampUtc >= startedAt.AddSeconds(-1), "timestamp before service start");
            Assert.True(p.TimestampUtc <= stoppedAt.AddSeconds(1), "timestamp after service stop");
        });

        // ── stop really stops: the count is frozen after StopAsync returns ──
        var atStop = history.QueryAddr(6).Count;
        await Task.Delay(80);
        Assert.Equal(atStop, history.QueryAddr(6).Count);
    }
}
