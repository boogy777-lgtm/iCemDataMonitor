using iCemDataMonitor.HostClient;
using iCemDataMonitor.Shell.WinUI;

namespace iCemDataMonitor.UI.Tests;

public sealed class ShellBootstrapCoordinatorTests
{
    private sealed class Effects
    {
        internal readonly List<string> Calls = new();
        internal bool Running, PipeUp = true, Missing, SpawnFailure;
        internal TaskCompletionSource? ProbeGate;
        internal ShellBootstrapCoordinator Create() => new(
            async () => { Calls.Add("probe"); if (ProbeGate is not null) await ProbeGate.Task; return EnsureHost.Decide(false, Running); },
            () => { Calls.Add("resolve"); return Missing ? null : "host.exe"; },
            _ => { Calls.Add("spawn"); if (SpawnFailure) throw new IOException("spawn"); },
            timeout => { Assert.Equal(TimeSpan.FromMilliseconds(400), timeout); Calls.Add("pipe"); return Task.FromResult(PipeUp); },
            ms => { Assert.Equal(500, ms); Calls.Add("delay"); return Task.CompletedTask; },
            _ => { });
        internal Action Waiting => () => Calls.Add("waiting");
        internal Func<Task> Mirror => () => { Calls.Add("mirror"); return Task.CompletedTask; };
    }

    [Fact]
    public async Task ViewerBypassesProbeAndSpawn_AndLivePipeNeverSpawns()
    {
        var effects = new Effects();
        var coordinator = effects.Create();
        Assert.Equal(ShellBootstrapOutcome.Viewer, await coordinator.RunAsync(true, effects.Waiting, effects.Mirror));
        Assert.Empty(effects.Calls);
        effects.Running = true;
        Assert.Equal(ShellBootstrapOutcome.ExistingHost, await coordinator.RunAsync(false, effects.Waiting, effects.Mirror));
        Assert.Equal(new[] { "probe" }, effects.Calls);
    }

    [Fact]
    public async Task SpawnWaitMirror_Order_AndCrossSessionSingleFlight()
    {
        var effects = new Effects { ProbeGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var coordinator = effects.Create();
        var first = coordinator.RunAsync(false, effects.Waiting, effects.Mirror);
        Assert.Equal(ShellBootstrapOutcome.Busy, await coordinator.RunAsync(false, effects.Waiting, effects.Mirror));
        effects.ProbeGate.SetResult();
        Assert.Equal(ShellBootstrapOutcome.SpawnedHost, await first);
        Assert.Equal(new[] { "probe", "resolve", "spawn", "waiting", "pipe", "mirror" }, effects.Calls);
        effects.Running = true;
        Assert.Equal(ShellBootstrapOutcome.ExistingHost, await coordinator.RunAsync(false, effects.Waiting, effects.Mirror));
    }

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public async Task FailureIsRetryable_TimeoutKeepsOriginalBudget(bool missing, bool spawnFailure)
    {
        var effects = new Effects { Missing = missing, SpawnFailure = spawnFailure, PipeUp = false };
        var coordinator = effects.Create();
        Assert.Equal(ShellBootstrapOutcome.Failed, await coordinator.RunAsync(false, effects.Waiting, effects.Mirror));
        Assert.DoesNotContain("mirror", effects.Calls);
        Assert.Equal(missing || spawnFailure ? 0 : 60, effects.Calls.Count(c => c == "pipe"));
        Assert.Equal(missing || spawnFailure ? 0 : 60, effects.Calls.Count(c => c == "delay"));
        effects.Missing = effects.SpawnFailure = false;
        effects.PipeUp = true;
        Assert.Equal(ShellBootstrapOutcome.SpawnedHost, await coordinator.RunAsync(false, effects.Waiting, effects.Mirror));
    }

    [Fact]
    public async Task MirrorFailureReleasesFlight_WhileMissingReadyMayCompleteNormally()
    {
        var effects = new Effects();
        var coordinator = effects.Create();
        await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(false, effects.Waiting, () => throw new IOException("stream")));
        Assert.Equal(ShellBootstrapOutcome.SpawnedHost, await coordinator.RunAsync(false, effects.Waiting, effects.Mirror));
    }
}
