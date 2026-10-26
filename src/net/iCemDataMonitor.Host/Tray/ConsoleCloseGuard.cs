using System.ComponentModel;
using System.Runtime.InteropServices;

namespace iCemDataMonitor.Host.Tray;

/// <summary>What the host does with a console control event (pure table).</summary>
public enum ConsoleCtrlAction
{
    /// <summary>Operator closed the console window (X) — swallow the event,
    /// HIDE the window, keep the server running (W21 G.1 owner decree).</summary>
    SwallowAndHideConsole,

    /// <summary>Everything else keeps DEFAULT Windows/.NET behaviour.</summary>
    DefaultHandling,
}

/// <summary>
/// W21 G.1: closing the console window must NOT stop the host — the process
/// stays alive in Task Manager and remains reachable through the tray icon.
///
/// Decision law (unit-tested table):
/// - CTRL_CLOSE (the X button / Alt+F4 on the console) → swallow + hide;
/// - CTRL_C stays a GRACEFUL STOP — the documented LOCAL-keyboard exception to
///   the tray-only stop law (E.2.7, see Program.cs header). Returning false
///   lets the default .NET ConsoleLifetime path run unchanged;
/// - CTRL_BREAK / CTRL_LOGOFF / CTRL_SHUTDOWN → default handling too (system
///   shutdown must never be blocked by a monitoring host).
/// </summary>
public static class ConsoleClosePolicy
{
    public const int CtrlCEvent = 0;
    public const int CtrlBreakEvent = 1;
    public const int CtrlCloseEvent = 2;
    public const int CtrlLogoffEvent = 5;
    public const int CtrlShutdownEvent = 6;

    public static ConsoleCtrlAction Decide(int ctrlType) =>
        ctrlType == CtrlCloseEvent
            ? ConsoleCtrlAction.SwallowAndHideConsole
            : ConsoleCtrlAction.DefaultHandling;
}

/// <summary>
/// Installs the Win32 console control handler that enforces
/// <see cref="ConsoleClosePolicy"/>. Registration happens BEFORE any other
/// startup work so an X click even during boot hides instead of kills.
/// The handler delegate is rooted in a static field — a collected handler
/// would crash the process on the next console event.
///
/// P2 (demand-attach, 2026-08-26): SetConsoleCtrlHandler registration is
/// PROCESS-WIDE — it covers any console the process is attached to at event
    /// time, INCLUDING one allocated later via the console-visibility service
    /// (ConsoleVisibilityService → ConsoleWindow.AttachAndShow → AllocConsole)
    /// on a headless-spawned host. An X click on such a late console still
    /// hides instead of killing the host.
///
/// 2026-08-27 DISPATCH-ORDER FIX (owner decree, .NET trap): Windows invokes
/// console handlers in REVERSE registration order — the LAST registered
/// handler runs FIRST. Microsoft.Extensions.Hosting ConsoleLifetime registers
/// its OWN SetConsoleCtrlHandler (which calls StopApplication) while the host
/// STARTS (WaitForStartAsync — before our post-StartAsync re-registration).
/// That registration happens AFTER our Install() in ProgramMain, so without a
/// counter-action ConsoleLifetime's handler would run BEFORE ours and the X
/// click would kill the host. FIX: after the WebApplication host is started
/// (Program.cs, right after app.StartAsync()), call <see cref="Reinstall"/> —
/// SetConsoleCtrlHandler(Handler, add: true) AGAIN, which moves our callback
/// to the HEAD of the dispatch list. It then swallows CTRL_CLOSE (returns
/// true) BEFORE ConsoleLifetime ever sees the event.
/// </summary>
public static class ConsoleCloseGuard
{
    private delegate bool ConsoleCtrlRoutine(int ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlRoutine handler, bool add);

    // Static root: prevents GC of the native callback target.
    private static readonly ConsoleCtrlRoutine Handler = OnCtrlType;

    private static Action<string>? _log;
    private static volatile bool _installed;

    public static bool Installed => _installed;

    /// <exception cref="InvalidOperationException">native registration failed.</exception>
    public static void Install(Action<string>? log = null)
    {
        if (_installed)
            return;
        _log = log;
        if (!SetConsoleCtrlHandler(Handler, add: true))
            throw new InvalidOperationException(
                $"SetConsoleCtrlHandler failed (Win32 error {new Win32Exception().ErrorCode})");
        _installed = true;
        log?.Invoke("console close guard installed (X hides the console — server keeps running)");
    }

    /// <summary>
    /// 2026-08-27 dispatch-order fix (see class doc): re-register the SAME
    /// handler after the WebApplication host is built+started. Windows runs
    /// console handlers in reverse registration order, so this re-add moves
    /// our callback to the HEAD — ahead of ConsoleLifetime's own handler that
    /// was registered while the host started. Fail-loud on native failure: a
    /// host whose X click kills it must not boot silently.
    /// Idempotent-by-necessity: the duplicate registration only matters when
    /// the FIRST entry's event is not swallowed (CTRL_C etc.) — both entries
    /// then see the event, and the second is a harmless no-op duplicate.
    /// </summary>
    public static void Reinstall()
    {
        if (!SetConsoleCtrlHandler(Handler, add: true))
            throw new InvalidOperationException(
                $"SetConsoleCtrlHandler re-registration failed (Win32 error {new Win32Exception().ErrorCode})");
        _log?.Invoke("console close guard re-registered at dispatch head — X is swallowed BEFORE ConsoleLifetime sees it");
    }

    private static bool OnCtrlType(int ctrlType)
    {
        if (ConsoleClosePolicy.Decide(ctrlType) != ConsoleCtrlAction.SwallowAndHideConsole)
            return false; // default handling (Ctrl+C graceful stop included)

        // P2 (owner complaint #2, 2026-08-26): the console window stays on
        // screen until EVERY handler returns — anything slow inside this
        // handler delays the visible hide. So: Hide() FIRST and synchronously
        // (ShowWindow SW_HIDE — instant), then log OFF the handler thread.
        // The old code invoked the file logger inline; StationFileLog holds a
        // per-file lock that a concurrent 25 MB tail-truncate can hold for
        // seconds, which read as «скрытие с задержкой».
        try
        {
            ConsoleWindow.Hide();
        }
        catch
        {
            // Even if hiding fails, the event is still swallowed: the decree is
            // "X must not kill the host".
        }

        var log = _log;
        if (log is not null)
            _ = Task.Run(() =>
            {
                try { log("console hidden by operator (X) — server keeps running; reopen via tray"); }
                catch { /* evidence channel must never hurt the host */ }
            });

        return true;
    }
}
