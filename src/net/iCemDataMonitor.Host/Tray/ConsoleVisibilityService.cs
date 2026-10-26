using iCemDataMonitor.Host.Tray;

namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// THE single console-visibility service (owner decree 2026-08-28: «команды
/// из трея кривые и не привязаны к общим командам host»). Every WRITE path —
/// the tray toggle ([Показать/Скрыть консоль]), the WinUI Status-tab RPC
/// SetConsoleVisible and the --autostart startup hide — lands HERE: state is
/// probed fresh, <see cref="ConsoleVisibilityPolicy.Decide"/> picks the action
/// from the closed table, and the executor performs it with a log line on the
/// startup log (evidence for a dead-looking toggle, P3 2026-08-27 lesson).
/// Registered as a DI singleton in HostAppFactory; Program.cs wires the tray
/// delegates to this instance and TelemetryServiceImpl receives it via ctor —
/// there is no second path to the Win32 console API.
/// </summary>
public sealed class ConsoleVisibilityService
{
    private readonly Action<string>? _log;
    private readonly Func<ConsolePresence> _probe;
    private readonly Action<ConsoleWindowAction, Action<string>?> _execute;

    /// <summary>Production composition: the probe/executor are the real Win32
    /// implementations on <see cref="ConsoleWindow"/>.</summary>
    public ConsoleVisibilityService(Action<string>? log = null)
        : this(log, ConsoleWindow.ProbePresence, ConsoleWindow.ExecuteAction)
    {
    }

    /// <summary>
    /// Test seam (§15.7 whitelist (a) — nondeterminism ONLY): the Win32 console
    /// state is process-global — a unit test running in the shared testhost
    /// must NEVER AllocConsole / SW_HIDE real windows, so the whole native
    /// boundary is injectable as two delegates. This is a data seam, not a
    /// strategy hierarchy: one closed action enum, one probe, one executor.
    /// </summary>
    internal ConsoleVisibilityService(
        Action<string>? log,
        Func<ConsolePresence> probe,
        Action<ConsoleWindowAction, Action<string>?> execute)
    {
        _log = log;
        _probe = probe;
        _execute = execute;
    }

    /// <summary>Tray «Показать консоль» / RPC SetConsoleVisible(true).</summary>
    public void Show() => SetVisible(visible: true);

    /// <summary>Tray «Скрыть консоль» / RPC SetConsoleVisible(false).</summary>
    public void Hide() => SetVisible(visible: false);

    /// <summary>Flip the console by its CURRENT state (re-probed at call time —
    /// the caption/state can change outside the tray: RPC, X-minimize, attach).</summary>
    public void Toggle() => SetVisible(_probe() != ConsolePresence.Visible);

    /// <summary>The ONE decision point: fresh probe → closed-table action →
    /// executor (the executor logs the outcome on the startup log).</summary>
    public void SetVisible(bool visible)
    {
        var action = ConsoleVisibilityPolicy.Decide(_probe(), visible ? ConsoleCommand.Show : ConsoleCommand.Hide);
        _execute(action, _log);
    }
}
