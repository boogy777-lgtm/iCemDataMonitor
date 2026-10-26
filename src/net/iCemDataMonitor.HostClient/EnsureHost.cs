namespace iCemDataMonitor.HostClient;

/// <summary>
/// B.2.1 ensure-host decision table. The AUTHORITATIVE probe is the pipe
/// (GetStatus answers); the mutex probe only refines the detail text.
/// </summary>
public enum HostPresence
{
    Running,
    NotRunning,
}

public sealed record EnsureHostVerdict(HostPresence Presence, string Detail);

public static class EnsureHost
{
    /// <summary>
    /// Mirror of SingleInstanceGuard.DefaultMutexName (iCemDataMonitor.Host).
    /// The Host exe project cannot be referenced from net8, so the name is
    /// duplicated here ONCE with a cross-reference comment — drift would only
    /// degrade the dialog detail, never correctness (pipe probe decides).
    /// </summary>
    public const string HostMutexName = @"Global\iCemDataMonitorHost_v1";

    /// <summary>True when SOME process currently holds the host single-instance mutex.</summary>
    public static bool MutexExists() => Mutex.TryOpenExisting(HostMutexName, out _);

    /// <summary>Authoritative probe: does the host answer GetStatus on the pipe?</summary>
    public static async Task<bool> PipeRespondsAsync(TimeSpan timeout)
    {
        try
        {
            using var api = new GrpcHostClient();
            using var cts = new CancellationTokenSource(timeout);
            await api.GetStatusAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // any failure (pipe missing / deadline / RPC error) = not serving
            return false;
        }
    }

    /// <summary>Decision table (unit-tested, 4 rows collapse to 3 outcomes).</summary>
    public static EnsureHostVerdict Decide(bool mutexExists, bool pipeResponds) =>
        (mutexExists, pipeResponds) switch
        {
            (_, true) => new EnsureHostVerdict(HostPresence.Running,
                "pipe отвечает — хост работает"),
            (true, false) => new EnsureHostVerdict(HostPresence.NotRunning,
                "mutex занят, но pipe не отвечает — хост стартует или завис"),
            (false, false) => new EnsureHostVerdict(HostPresence.NotRunning,
                "хост не запущен"),
        };

    /// <summary>
    /// E.3.1 uniqueness law (owner decree 2026-08-24): the UI starts the server
    /// ONLY when it is not running and NEVER spawns a copy of a live one.
    /// A Running verdict (mutex✓+pipe✓ or pipe✓ alone) means an instance ALREADY
    /// serves — the client connects to it; spawning would create a second
    /// Host.exe which the single-instance guard then kills with exit 1.
    /// 2026-08-31 (owner decree): the spawn path is NO LONGER operator-gated —
    /// the «Запустить хост?» dialog is removed and a NotRunning verdict makes
    /// the shell spawn the host IMMEDIATELY.
    /// </summary>
    public static bool ShouldSpawnNewHostProcess(EnsureHostVerdict verdict) =>
        verdict.Presence == HostPresence.NotRunning;
}
