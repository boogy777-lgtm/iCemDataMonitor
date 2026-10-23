using System.Diagnostics;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Data.Telemetry;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

public sealed class BusAcquisitionTests
{
    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

    private sealed class Source : IBusCaptureSource, IDisposable
    {
        public int Calls;
        public bool Disposed;
        public void Dispose() => Disposed = true;
        public BusSnapshot? LastSnapshot { get; set; }
        public bool IsOpen { get; set; }
        public string? LastError { get; set; }
        public Func<BusSnapshot?>? Capture;
        public BusSnapshot? Snapshot()
        {
            Assert.False(Disposed);
            Interlocked.Increment(ref Calls);
            var snapshot = Capture?.Invoke();
            Assert.False(Disposed);
            return snapshot;
        }
    }

    private sealed class Latest : ILatestBusState
    {
        public LatestBusState State = new(null, false, "missing", null, null, null, 0);
        public int Reads;
        public LatestBusState Current { get { Reads++; return State; } }
    }

    private static BusSnapshot Snapshot() => new(BusLayout.Magic, BusLayout.Version, BusLayout.LayoutVersion,
        600, new ushort[600], default, default, default, default, false, 123);

    [Fact]
    public void Publication_FirstState_CoherentSuccess_FallbackAndClosedFailure()
    {
        var source = new Source();
        var clock = new Clock();
        using var acquisition = new BusAcquisitionService(source, TimeSpan.FromMilliseconds(200), clock);
        Assert.Null(acquisition.Current.Snapshot);
        Assert.False(acquisition.Current.IsOpen);
        Assert.Equal(0, source.Calls);
        source.Capture = () => { source.IsOpen = true; return source.LastSnapshot = Snapshot(); };
        clock.Now = 10;
        acquisition.CaptureOnce();
        var good = acquisition.Current;
        Assert.Equal(1, good.SuccessfulCaptureSequence);
        Assert.Equal(10, good.AttemptStartedTimestamp);
        Assert.Equal(10, good.CompletedTimestamp);
        Assert.Equal(10, good.LastSuccessfulCaptureTimestamp);
        Assert.True(good.IsOpen);
        Assert.Null(good.LastError);

        // Real reader reopen/copy/disposal paths can return non-Stale lastGood.
        source.Capture = () => source.LastSnapshot;
        clock.Now = 20;
        acquisition.CaptureOnce();
        var failed = acquisition.Current;
        Assert.True(failed.Snapshot!.Stale);
        Assert.False(good.Snapshot!.Stale); // prior immutable publication stays unchanged
        Assert.Equal(1, failed.SuccessfulCaptureSequence);
        Assert.Equal(10, failed.LastSuccessfulCaptureTimestamp);
        Assert.Equal(20, failed.CompletedTimestamp);
        Assert.NotNull(failed.LastError);
        Assert.True(failed.IsOpen);

        source.IsOpen = false;
        source.LastError = "ACL denial remains sticky";
        source.Capture = () => throw new UnauthorizedAccessException(source.LastError);
        acquisition.CaptureOnce();
        Assert.False(acquisition.Current.IsOpen);
        Assert.Equal(source.LastError, acquisition.Current.LastError);
        Assert.True(acquisition.Current.Snapshot!.Stale);
        source.Capture = () => source.LastSnapshot; // subsequent sticky policy returns old data
        acquisition.CaptureOnce();
        Assert.False(acquisition.Current.IsOpen);
        Assert.Equal(source.LastError, acquisition.Current.LastError);
        Assert.Equal(1, acquisition.Current.SuccessfulCaptureSequence);
    }

    [Fact]
    public void IdenticalBytesAndSourceTime_NewReferenceIsNewSuccess_ExpiryHasNoSourceIO()
    {
        var source = new Source { IsOpen = true };
        source.Capture = () => source.LastSnapshot = Snapshot();
        var clock = new Clock();
        using var acquisition = new BusAcquisitionService(source, TimeSpan.FromMilliseconds(200), clock);
        acquisition.CaptureOnce();
        acquisition.CaptureOnce();
        Assert.Equal(2, acquisition.Current.SuccessfulCaptureSequence);
        clock.Now = 700;
        Assert.False(acquisition.Current.Snapshot!.Stale);
        clock.Now = 701;
        for (int i = 0; i < 1000; i++)
        {
            var expired = acquisition.Current;
            Assert.True(expired.Snapshot!.Stale);
            Assert.True(expired.IsOpen);
            Assert.Equal(2, expired.SuccessfulCaptureSequence);
            Assert.Contains("expired", expired.LastError!);
        }
        Assert.Equal(2, source.Calls);
        acquisition.CaptureOnce();
        Assert.False(acquisition.Current.Snapshot!.Stale);
        Assert.Equal(3, acquisition.Current.SuccessfulCaptureSequence);
    }

    [Fact]
    public async Task BlockedCapture_ReadsStayNonblocking_StartDoesNotWait_StopJoinsBeforeSourceDisposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var release = new ManualResetEventSlim();
        var source = new Source { IsOpen = true };
        source.Capture = () =>
        {
            entered.TrySetResult();
            // The synchronous native capture stays blocked; only the test's entry wait is asynchronous.
            release.Wait();
            return source.LastSnapshot = Snapshot();
        };
        using var acquisition = new BusAcquisitionService(source, TimeSpan.FromMilliseconds(200), TimeProvider.System);
        Task? stop = null;
        try
        {
            await acquisition.StartAsync(CancellationToken.None).WaitAsync(budget.Token);
            Assert.False(release.IsSet);
            Assert.Null(acquisition.Current.Snapshot); // startup did not wait for a successful capture
            await entered.Task.WaitAsync(budget.Token);
            var reads = Task.Run(() => { for (int i = 0; i < 10000; i++) Assert.Null(acquisition.Current.Snapshot); });
            await reads.WaitAsync(budget.Token);
            using var cancelledDeadline = new CancellationTokenSource();
            cancelledDeadline.Cancel();
            stop = acquisition.StopAsync(cancelledDeadline.Token);
            Assert.False(stop.IsCompleted); // cannot dispose the source while its capture is still running
            Assert.Equal(1, source.Calls);
            release.Set();
            await stop.WaitAsync(budget.Token);
            Assert.False(acquisition.Current.IsOpen);
            Assert.True(acquisition.Current.Snapshot!.Stale);
            Assert.Contains("stopped", acquisition.Current.LastError!);
            acquisition.CaptureOnce();
            Assert.Equal(1, source.Calls);
            source.Dispose();
            Assert.True(source.Disposed);
        }
        finally
        {
            release.Set();
            await (stop ?? acquisition.StopAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
            source.Dispose();
        }
    }

    [Fact]
    public void Ring_ConsumesEachNewSuccessOnce_IdenticalWordsAndTimestampStillAdvance()
    {
        var latest = new Latest();
        var history = new RingHistory();
        using var ring = new BusRingService(latest, new BusAddrRingService(history));
        ring.TickOnce();
        latest.State = new(Snapshot(), true, null, 0, 0, 0, 1);
        ring.TickOnce();
        ring.TickOnce();
        Assert.Single(history.QueryAddr(4));
        latest.State = latest.State with { SuccessfulCaptureSequence = 2, Snapshot = Snapshot() };
        ring.TickOnce();
        Assert.Equal(2, history.QueryAddr(4).Count);
        latest.State = latest.State with { SuccessfulCaptureSequence = 3, Snapshot = Snapshot() with { Stale = true } };
        ring.TickOnce();
        Assert.Equal(2, history.QueryAddr(4).Count);
        Assert.Equal(5, latest.Reads);
    }

    [Fact]
    public async Task Metrics_IndependentSubscribers_ReadLatestWithoutCapture_AndKeepTheirOwnDeltas()
    {
        var latest = new Latest { State = new(Snapshot(), true, null, 0, 0, 0, 1) };
        var sampler = new MetricsSampler(latest);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var a = sampler.Subscribe(1, cancellation.Token);
        var b = sampler.Subscribe(400, cancellation.Token);
        await using var ea = a.GetAsyncEnumerator(cancellation.Token);
        await using var eb = b.GetAsyncEnumerator(cancellation.Token);
        Assert.True(await ea.MoveNextAsync());
        Assert.True(await eb.MoveNextAsync());
        Assert.True(ea.Current.IntervalClamped);
        Assert.False(eb.Current.IntervalClamped);
        Assert.Equal(302, ea.Current.Deltas.Count);
        Assert.Equal(302, eb.Current.Deltas.Count);
        Assert.True(await ea.MoveNextAsync());
        Assert.Empty(ea.Current.Deltas);
        Assert.True(await eb.MoveNextAsync());
        Assert.Empty(eb.Current.Deltas);
        cancellation.Cancel();
        await Task.WhenAll(a.ProducerCompletion, b.ProducerCompletion);
    }

    [Fact]
    public void ProductionSourceCallMap_OnlyAcquisitionCaptures_ConsumersUseNoReader()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "iCemDataMonitor.Data"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var root = dir!.FullName;
        var consumerFiles = new[]
        {
            "iCemDataMonitor.Data/Telemetry/MetricsSampler.cs", "iCemDataMonitor.Data/Bus/BusRingService.cs",
            "iCemDataMonitor.Transport/ModbusTcpPublisher.cs", "iCemDataMonitor.Host.Core/Health/BusHealthMonitor.cs",
            "iCemDataMonitor.Host.Core/Endpoints/RegisterMirrorEndpoints.cs", "iCemDataMonitor.Host/Grpc/TelemetryServiceImpl.cs",
        };
        foreach (var file in consumerFiles)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            Assert.DoesNotContain(".Snapshot()", text);
            Assert.DoesNotContain(".TryOpen(", text);
            Assert.DoesNotContain("GetRequiredService<BusReader>", text);
            Assert.Contains(".Current", text);
        }
        // Completeness: discover all legacy production projects (including shell/CLI), not a fixed caller list.
        // Reader truth/fixture projects are excluded; standalone ViewerOnly has no capture call.
        foreach (var project in Directory.EnumerateDirectories(root, "iCemDataMonitor*")
                     .Where(path => !Path.GetFileName(path).EndsWith(".Tests", StringComparison.Ordinal)))
        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                Path.GetFileName(file) is "BusReader.cs" or "BusAcquisitionService.cs" or "BootStageTracker.cs") continue;
            Assert.DoesNotContain(".Snapshot()", File.ReadAllText(file));
        }
    }
}
