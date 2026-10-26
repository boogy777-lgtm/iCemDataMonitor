using System.Diagnostics;

namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// E.2.2 [Перезапуск]: pure builder of the successor process start (unit-tested
/// decision table). Semantics: spawn a DETACHED copy of THIS exe with THE SAME
/// arguments BEFORE the current instance exits, then drain gracefully through
/// the existing host stop path. Never kills itself — the OS reaps it after
/// StopAsync completes; the mutex is released by process exit and the
/// successor (grace env-var set) retries acquisition briefly.
/// </summary>
public static class RestartPlan
{
    /// <summary>
    /// Handshake env-var: tells the successor that it was spawned by a draining
    /// predecessor, so a temporarily held single-instance mutex is retried for
    /// a bounded window instead of failing fast. WITHOUT this var the very
    /// first failed acquisition remains an honest operator error.
    /// </summary>
    public const string GraceEnvVar = "STATION_HOST_RESTART_GRACE";

    /// <summary>Bounded retry window for the successor: 30 × 500 ms = 15 s.</summary>
    public static readonly TimeSpan MutexGraceWindow = TimeSpan.FromSeconds(15);

    public static ProcessStartInfo Build(string currentExePath, IReadOnlyList<string> currentArgs)
    {
        var psi = new ProcessStartInfo(currentExePath);
        foreach (var arg in currentArgs)
            psi.ArgumentList.Add(arg);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true; // hidden console — same contract as tray/UI spawn
        psi.WorkingDirectory = Path.GetDirectoryName(currentExePath)
            ?? throw new InvalidOperationException($"exe path has no directory: '{currentExePath}'");
        psi.Environment[GraceEnvVar] = "1";
        return psi;
    }
}
