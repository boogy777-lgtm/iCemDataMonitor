using System.Diagnostics;
using System.Reflection;
using iCemDataMonitor.Contracts;
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
        // A scheduling/deadlock guard per stage, not a Current latency or product SLA.
        var stageGuard = TimeSpan.FromSeconds(10);
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
        Task? reads = null;
        try
        {
            await acquisition.StartAsync(CancellationToken.None).WaitAsync(stageGuard);
            Assert.False(release.IsSet);
            // Startup completed although the capture cannot succeed until release is set.
            await entered.Task.WaitAsync(stageGuard);
            // A dedicated worker can run while the native capture occupies a pool thread.
            // If Current waits for capture, this worker cannot finish before the stage guard;
            // finally releases capture and joins both workers before disposing the source.
            reads = Task.Factory.StartNew(() =>
            {
                var state = acquisition.Current;
                Assert.Null(state.Snapshot);
                Assert.False(state.IsOpen);
                Assert.Equal(0, state.SuccessfulCaptureSequence);
                Assert.Null(acquisition.Current.Snapshot);
                Assert.Equal(1, source.Calls);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await reads.WaitAsync(stageGuard);
            using var cancelledDeadline = new CancellationTokenSource();
            cancelledDeadline.Cancel();
            stop = acquisition.StopAsync(cancelledDeadline.Token);
            Assert.False(stop.IsCompleted); // cannot dispose the source while its capture is still running
            Assert.Equal(1, source.Calls);
            release.Set();
            await stop.WaitAsync(stageGuard);
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
            await (stop ?? acquisition.StopAsync(CancellationToken.None)).WaitAsync(stageGuard);
            try
            {
                if (reads is not null) await reads.WaitAsync(stageGuard);
            }
            finally { source.Dispose(); }
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
    public void Metrics_PerStreamSampleSteps_ReadLatestOnce_AndKeepIndependentDeltas()
    {
        var latest = new Latest { State = new(Snapshot(), true, null, 0, 0, 0, 1) };
        var sampler = new MetricsSampler(latest);
        // Drive the existing per-stream projection deterministically. Subscribe's capacity-1
        // DropOldest channel can legally evict the first full frame before it is consumed;
        // channel delivery, interval clamp and lifecycle have separate real-loop tests.
        var build = typeof(MetricsSampler).GetMethod("BuildSample", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ushort[]? previousA = null, previousB = null;
        int steps = 0;
        MetricsSample Step(ref ushort[]? previous, bool clamped)
        {
            object?[] args = { previous, clamped };
            var sample = (MetricsSample)build.Invoke(sampler, args)!;
            previous = (ushort[]?)args[0];
            Assert.Equal(++steps, latest.Reads); // exactly one latest publication per iteration
            return sample;
        }
        void Publish(ushort heartbeat, float value, long sequence)
        {
            var words = new ushort[600];
            words[3] = heartbeat;
            (words[200], words[201]) = CdabCodec.Split(value);
            latest.State = latest.State with
            {
                Snapshot = Snapshot() with { Words = words }, SuccessfulCaptureSequence = sequence,
            };
        }
        static void AssertChanged(MetricsSample sample, int heartbeat, float value)
        {
            Assert.Equal(heartbeat, sample.Heartbeat);
            Assert.Equal(2, sample.Deltas.Count);
            Assert.Equal(heartbeat, sample.Deltas.Single(d => d.Addr == 3).Int32Value);
            Assert.Equal(value, sample.Deltas.Single(d => d.Addr == 200).FloatValue);
        }

        var firstA = Step(ref previousA, clamped: true);
        Assert.True(firstA.IntervalClamped); // flag projection, not a timing assertion
        Assert.Equal(302, firstA.Deltas.Count);
        Assert.Equal(0, firstA.Deltas.Single(d => d.Addr == 200).FloatValue);
        Publish(9, 1.5f, 2);
        AssertChanged(Step(ref previousA, clamped: true), 9, 1.5f);
        var firstB = Step(ref previousB, clamped: false);
        Assert.False(firstB.IntervalClamped);
        Assert.Equal(302, firstB.Deltas.Count); // A's previous steps cannot initialize B
        Assert.Equal(9, firstB.Deltas.Single(d => d.Addr == 3).Int32Value);
        Assert.Equal(1.5f, firstB.Deltas.Single(d => d.Addr == 200).FloatValue);
        Assert.NotSame(previousA, previousB);
        Assert.Empty(Step(ref previousA, clamped: true).Deltas);
        Publish(10, 2.5f, 3);
        AssertChanged(Step(ref previousB, clamped: false), 10, 2.5f);
        AssertChanged(Step(ref previousA, clamped: true), 10, 2.5f); // B did not advance A's baseline
        Assert.Empty(Step(ref previousB, clamped: false).Deltas);
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
