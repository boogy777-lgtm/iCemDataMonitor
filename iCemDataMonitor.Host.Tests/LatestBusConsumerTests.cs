using Grpc.Core;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Data.Telemetry;
using iCemDataMonitor.Host.Core.Health;
using iCemDataMonitor.Host.Grpc;
using iCemDataMonitor.Host.Tray;
using iCemDataMonitor.Sdk.Bus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace iCemDataMonitor.Host.Tests;

public sealed class LatestBusConsumerTests
{
    private sealed class Latest : ILatestBusState
    {
        public int Reads;
        public LatestBusState Value = new(null, false, "waiting for first publication", null, null, null, 0);
        public LatestBusState Current { get { Reads++; return Value; } }
    }

    private static BusSnapshot Snapshot()
    {
        var words = new ushort[600];
        words[0] = 3; words[1] = 7; words[2] = 11; words[3] = 13;
        words[4] = 0x00a3; words[5] = 0x6553; // exact int32 raw bits, not float conversion
        return new(BusLayout.Magic, BusLayout.Version, BusLayout.LayoutVersion, 600,
            words, default, default, default, default, false, 0);
    }

    [Fact]
    public void Health_OneCurrent_ProbeIncludesCaseState_InSameSnapshotAsOtherFields()
    {
        var latest = new Latest { Value = new(Snapshot(), true, null, 0, 0, 0, 1) };
        var state = new StationHealthState();
        using var monitor = new BusHealthMonitor(latest, state, NullLogger<BusHealthMonitor>.Instance);
        monitor.Probe();
        Assert.Equal(1, latest.Reads);
        Assert.True(state.Current.BusOpen);
        Assert.Equal(7, state.Current.CaseState);
        Assert.Equal(11, state.Current.CaseLanState);
        Assert.Equal(13, state.Current.Heartbeat);
        latest.Value = latest.Value with { IsOpen = false, Snapshot = Snapshot() with { Stale = true } };
        monitor.Probe();
        Assert.Equal(2, latest.Reads);
        Assert.False(state.Current.BusOpen);
        Assert.Equal(7, state.Current.CaseState);
    }

    [Fact]
    public async Task RegisterRpc_FirstStateUnavailable_OneCurrent_KnownOpenStaleKeepsExactBits_ClosedRejects()
    {
        var latest = new Latest();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var svc = Create(latest, new StationHealthState(), provider);
        var unavailable = await Assert.ThrowsAsync<RpcException>(() => svc.GetRegisters(new(), new TestCallContext()));
        Assert.Equal(StatusCode.Unavailable, unavailable.StatusCode);
        Assert.Equal(1, latest.Reads);
        latest.Value = new(Snapshot() with { Stale = true }, true, "mutex timeout", 0, 0, 0, 1);
        var reply = await svc.GetRegisters(new(), new TestCallContext());
        Assert.Equal(2, latest.Reads);
        Assert.Equal(unchecked((int)0x655300a3), reply.Values.Single(v => v.Addr == 4).Int32Value);
        latest.Value = latest.Value with { IsOpen = false };
        unavailable = await Assert.ThrowsAsync<RpcException>(() => svc.GetRegisters(new(), new TestCallContext()));
        Assert.Equal(StatusCode.Unavailable, unavailable.StatusCode);
        Assert.Equal(3, latest.Reads);
    }

    [Fact]
    public async Task StatusAndControls_ReadHealthCaseState_WithoutReadingLatestOrSource()
    {
        if (!OperatingSystem.IsWindows()) return; // status also reads the real HKCU autostart sensor
        var latest = new Latest();
        var health = new StationHealthState();
        health.Update(health.Current with { CaseState = 123, Heartbeat = 456 });
        using var provider = new ServiceCollection().BuildServiceProvider();
        var svc = Create(latest, health, provider);
        var status = await svc.GetStatus(new(), new TestCallContext());
        Assert.Equal(123, status.CaseState);
        Assert.Equal(456, status.Heartbeat);
        await svc.SetConsoleVisible(new SetConsoleVisibleRequest { Visible = false }, new TestCallContext());
        Assert.Equal(0, latest.Reads);
    }

    private sealed class BlockingSource(ManualResetEventSlim entered, ManualResetEventSlim release) : IBusCaptureSource, IDisposable
    {
        public bool Disposed;
        public bool CaptureFinished;
        public BusSnapshot? LastSnapshot { get; private set; }
        public bool IsOpen => true;
        public string? LastError => null;
        public BusSnapshot? Snapshot()
        {
            entered.Set();
            release.Wait();
            Assert.False(Disposed);
            CaptureFinished = true;
            return LastSnapshot = LatestBusConsumerTests.Snapshot();
        }
        public void Dispose()
        {
            Assert.True(CaptureFinished);
            Disposed = true;
        }
    }

    [Fact]
    public async Task ContainerDisposal_JoinsInflightAcquisitionBeforeFactoryOwnedSource_EvenWithoutHostStop()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new BlockingSource(entered, release));
        services.AddSingleton(sp => new BusAcquisitionService(sp.GetRequiredService<BlockingSource>(),
            TimeSpan.FromMilliseconds(200), TimeProvider.System));
        services.AddSingleton<ILatestBusState>(sp => sp.GetRequiredService<BusAcquisitionService>());
        var provider = services.BuildServiceProvider();
        Task? disposal = null;
        try
        {
            var owner = provider.GetRequiredService<BusAcquisitionService>();
            var source = provider.GetRequiredService<BlockingSource>();
            Assert.Same(owner, provider.GetRequiredService<ILatestBusState>());
            await owner.StartAsync(CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            using var disposing = new ManualResetEventSlim();
            disposal = Task.Factory.StartNew(() => { disposing.Set(); provider.Dispose(); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(disposal.IsCompleted);
            Assert.False(source.Disposed);
            release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(source.CaptureFinished);
            Assert.True(source.Disposed);
            Assert.False(owner.Current.IsOpen);
        }
        finally
        {
            release.Set();
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            else provider.Dispose();
        }
    }

    private static TelemetryServiceImpl Create(Latest latest, StationHealthState health, IServiceProvider provider)
        => new(health, latest, new MetricsSampler(latest),
            new AutostartManager(runKeyPath: "Software\\C6_Unused", valueName: "unused", exePath: "unused"),
            new ConsoleVisibilityService(null, () => ConsolePresence.None, (_, _) => { }),
            new UiPrefsStore(Path.Combine(Path.GetTempPath(), "c6-unused-" + Guid.NewGuid().ToString("N"))),
            provider, NullLogger<TelemetryServiceImpl>.Instance);
}
