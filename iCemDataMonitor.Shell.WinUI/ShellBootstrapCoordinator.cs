using iCemDataMonitor.HostClient;

namespace iCemDataMonitor.Shell.WinUI;

internal enum ShellBootstrapOutcome { Viewer, ExistingHost, SpawnedHost, Failed, Busy }

/// <summary>One cross-session launch flight. Visuals/session resources belong to
/// the window; effects are supplied by the composition root.</summary>
internal sealed class ShellBootstrapCoordinator
{
    private readonly Func<Task<EnsureHostVerdict>> _probe;
    private readonly Func<string?> _resolve;
    private readonly Action<string> _spawn;
    private readonly Func<TimeSpan, Task<bool>> _pipeResponds;
    private readonly Func<int, Task> _delay;
    private readonly Action<string> _log;
    private int _inFlight;

    internal ShellBootstrapCoordinator(Func<Task<EnsureHostVerdict>> probe, Func<string?> resolve,
        Action<string> spawn, Func<TimeSpan, Task<bool>> pipeResponds, Func<int, Task> delay, Action<string> log)
    {
        _probe = probe; _resolve = resolve; _spawn = spawn;
        _pipeResponds = pipeResponds; _delay = delay; _log = log;
    }

    internal async Task<ShellBootstrapOutcome> RunAsync(bool viewer, Action showWaiting, Func<Task> mirrorBoot,
        Func<ShellBootstrapOutcome, Task>? complete = null)
    {
        if (Interlocked.Exchange(ref _inFlight, 1) == 1)
        {
            _log("[launch] host launch already in flight — click ignored");
            return ShellBootstrapOutcome.Busy;
        }
        try
        {
            var outcome = await RunCoreAsync(viewer, showWaiting, mirrorBoot);
            // Session replacement/failure presentation is part of the same flight.
            // Its implementation remains with the window.
            if (complete is not null) await complete(outcome);
            return outcome;
        }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    private async Task<ShellBootstrapOutcome> RunCoreAsync(bool viewer, Action showWaiting, Func<Task> mirrorBoot)
    {
        if (viewer)
        {
            _log("viewer mode (--viewer): bus view WITHOUT control");
            return ShellBootstrapOutcome.Viewer;
        }
        var verdict = await _probe();
        if (!EnsureHost.ShouldSpawnNewHostProcess(verdict))
        {
            _log($"host detected: {verdict.Detail}");
            return ShellBootstrapOutcome.ExistingHost;
        }
        _log($"host NOT running: {verdict.Detail} — spawning the host immediately (no dialog)");
        var exe = _resolve();
        if (exe is null)
        {
            _log("host exe NOT FOUND (sibling + dev-layout probes) — launch aborted");
            return ShellBootstrapOutcome.Failed;
        }
        try { _spawn(exe); }
        catch (Exception ex)
        {
            _log($"host spawn FAILED: {ex.Message}");
            return ShellBootstrapOutcome.Failed;
        }
        showWaiting();
        for (int waitedMs = 0; waitedMs < 30_000; waitedMs += 500)
        {
            if (await _pipeResponds(TimeSpan.FromMilliseconds(400)))
            {
                _log($"host pipe answering after ~{waitedMs} ms");
                await mirrorBoot();
                return ShellBootstrapOutcome.SpawnedHost;
            }
            await _delay(500);
        }
        _log("host did NOT answer on the pipe within 30 s");
        return ShellBootstrapOutcome.Failed;
    }
}
