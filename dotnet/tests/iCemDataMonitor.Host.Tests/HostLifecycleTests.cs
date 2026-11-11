using Microsoft.Extensions.Hosting;
using iCemDataMonitor.Host.Tray;

namespace iCemDataMonitor.Host.Tests;

/// <summary>
/// 2026-08-31 (owner decree «единый механизм запуска/рестарта/закрытия»):
/// the HostLifecycle coordinator IS the single mechanism — these tests pin
/// its verbs through the injected seams (no real process is ever spawned,
/// no real exit happens). The gate semantics come from TrayActionGate
/// (pinned in TrayLogicTests); here the COORDINATION is pinned.
/// </summary>
public class HostLifecycleTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public int StopCalls { get; private set; }
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => StopCalls++;
    }

    [Fact]
    public void RequestRestart_SpawnFailure_ReleasesGate_RetryExitsZero()
    {
        var logs = new List<string>();
        var exits = new List<int>();
        var spawnCalls = 0;
        var lifecycle = new HostLifecycle(
            args: [],
            lifetime: new FakeLifetime(),
            log: logs.Add,
            spawn: _ =>
            {
                spawnCalls++;
                if (spawnCalls == 1)
                    throw new InvalidOperationException("simulated spawn failure");
                return null; // fake successor — pid unknown is fine for the log
            },
            exit: exits.Add);

        lifecycle.RequestRestart(); // spawn fails — the host must SURVIVE…
        Assert.Empty(exits);
        Assert.Contains(logs, l => l.Contains("spawn FAILED") && l.Contains("gate released"));

        lifecycle.RequestRestart(); // …and the retry must reach the blunt exit
        Assert.Equal(2, spawnCalls);
        Assert.Equal(new[] { 0 }, exits);
        Assert.Contains(logs, l => l.Contains("host stopped (fast restart exit)"));
    }

    [Fact]
    public void RequestRestart_ReentryWhileBusy_IsLoggedNoOp()
    {
        var logs = new List<string>();
        var spawnCalls = 0;
        var lifecycle = new HostLifecycle(
            args: [],
            lifetime: new FakeLifetime(),
            log: logs.Add,
            spawn: _ => { spawnCalls++; return null; },
            exit: _ => { /* the process would be gone here — the gate stays taken */ });

        lifecycle.RequestRestart(); // succeeds (gate stays taken — exiting)
        lifecycle.RequestRestart(); // busy — never a second successor
        Assert.Equal(1, spawnCalls);
        Assert.Contains(logs, l => l.Contains(TrayActionGate.RestartBusyMessage));
    }

    [Fact]
    public void RequestClose_SignalsAllUiShutdownEvents_ThenStopsGracefully()
    {
        var logs = new List<string>();
        var signalled = new List<string>();
        var lifetime = new FakeLifetime();
        var exits = new List<int>();
        var lifecycle = new HostLifecycle(
            args: [],
            lifetime: lifetime,
            log: logs.Add,
            exit: exits.Add, // close must NEVER blunt-exit
            signalEvent: name => { signalled.Add(name); return true; });

        lifecycle.RequestClose();

        Assert.Equal(TrayCloseContract.UiShutdownEventNames, signalled);
        Assert.Equal(1, lifetime.StopCalls); // graceful drain, not a blunt exit
        Assert.Empty(exits);
        foreach (var name in TrayCloseContract.UiShutdownEventNames)
            Assert.Contains(logs, l => l.Contains($"[close] {name} signalled"));
    }

    [Fact]
    public void RequestClose_ReentryWhileBusy_IsLoggedNoOp()
    {
        var logs = new List<string>();
        var lifetime = new FakeLifetime();
        var lifecycle = new HostLifecycle(
            args: [],
            lifetime: lifetime,
            log: logs.Add,
            signalEvent: _ => true);

        lifecycle.RequestClose();
        lifecycle.RequestClose(); // busy — never a second drain
        Assert.Equal(1, lifetime.StopCalls);
        Assert.Contains(logs, l => l.Contains(TrayActionGate.CloseBusyMessage));
    }
}
