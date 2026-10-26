namespace iCemDataMonitor.Host.Tray;

/// <summary>The ACTUAL console state of this process (probe-derived).</summary>
public enum ConsolePresence
{
    /// <summary>No console window handle at all (headless CreateNoWindow spawn).</summary>
    None,

    /// <summary>A console window exists but is not visible (autostart SW_HIDE / X).</summary>
    Hidden,

    /// <summary>A console window exists and is visible (manual run).</summary>
    Visible,
}

/// <summary>What the operator asked for: reveal or conceal the console.</summary>
public enum ConsoleCommand
{
    Show,
    Hide,
}

/// <summary>The one thing each (presence × command) pair must DO.</summary>
public enum ConsoleWindowAction
{
    /// <summary>No console exists + show requested → demand-attach a console
    /// (AllocConsole; the windowless-console swap FreeConsole+AllocConsole is
    /// inside the executor) + rebind std handles + banner.</summary>
    AttachAndShow,

    /// <summary>A hidden console exists + show requested → SW_SHOW / SW_RESTORE
    /// + focus (a plain SW_SHOW without SetForegroundWindow can surface behind
    /// the shell or require a click — the owner decree says show AND focus).</summary>
    ShowWithFocus,

    /// <summary>A console exists + hide requested → SW_HIDE (instant).</summary>
    Hide,

    /// <summary>Nothing to do (hide with no console; show on an already visible one).</summary>
    Nothing,
}

/// <summary>
/// 2026-08-28 console-visibility decision table (owner decree «команды из трея
/// кривые и не привязаны к общим командам host»): the tray toggle, the RPC
/// SetConsoleVisible and the autostart hide ALL route through
/// <see cref="ConsoleVisibilityService"/> → THIS closed table. modes:
/// None/Hidden/Visible × Show/Hide → action. Fail-safe default = Nothing
/// (an unknown combination must never spawn/hide anything).
/// ROOT CAUSE fixed here (startup.log 07:56:58, owner machine): a host spawned
/// by the WinUI shell with CreateNoWindow owns a WINDOWLESS console —
/// GetConsoleWindow()==NULL yet AllocConsole fails with ERROR_ACCESS_DENIED
/// (5) because the console OBJECT exists; the executor's FreeConsole+AllocConsole
/// swap is what actually materialises a visible window (live-probe pinned in
/// ConsoleAttachRecipeProbeTests).
/// </summary>
public static class ConsoleVisibilityPolicy
{
    /// <summary>Pure classification (unit-tested): handle + visibility → presence.</summary>
    public static ConsolePresence Classify(bool hasConsoleWindow, bool consoleWindowVisible) =>
        !hasConsoleWindow ? ConsolePresence.None
        : consoleWindowVisible ? ConsolePresence.Visible
        : ConsolePresence.Hidden;

    /// <summary>The closed decision table (unit-tested): 6 arms + fail-safe.</summary>
    public static ConsoleWindowAction Decide(ConsolePresence presence, ConsoleCommand command) =>
        (presence, command) switch
        {
            (ConsolePresence.None, ConsoleCommand.Show) => ConsoleWindowAction.AttachAndShow,
            (ConsolePresence.Hidden, ConsoleCommand.Show) => ConsoleWindowAction.ShowWithFocus,
            (ConsolePresence.Visible, ConsoleCommand.Show) => ConsoleWindowAction.Nothing, // already visible
            (ConsolePresence.None, ConsoleCommand.Hide) => ConsoleWindowAction.Nothing,    // nothing exists
            (ConsolePresence.Hidden, ConsoleCommand.Hide) => ConsoleWindowAction.Hide,     // idempotent SW_HIDE
            (ConsolePresence.Visible, ConsoleCommand.Hide) => ConsoleWindowAction.Hide,
            _ => ConsoleWindowAction.Nothing, // fail-safe: unknown ⇒ never act
        };
}
