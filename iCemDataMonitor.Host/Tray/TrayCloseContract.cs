namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// P2 2026-08-26 (owner decree): tray [Закрыть] = close the UI + the host.
/// The panel listens on its OWN named event (one waiter ⇒ AutoReset is safe)
/// and exits gracefully; the host then drains through the existing
/// StopApplication path. [Перезапуск] stays a HOST-ONLY restart (RestartPlan
/// spawns the successor, the UI reconnects to the new host over the pipe —
/// unchanged semantics).
///
/// 2026-08-27: the standalone emulator UI event (EmuUiShutdownEvent) was
/// REMOVED — the emulator is now a SEPARATE INDEPENDENT app (own exe, built-in
/// UI+CLI) and [Закрыть] must close host + WinUI panel ONLY, never the
/// emulator.
/// Pure contract, unit-tested: the signaling loop in Program.cs iterates
/// these names best-effort (missing event = that UI is not running).
/// </summary>
public static class TrayCloseContract
{
    public static IReadOnlyList<string> UiShutdownEventNames { get; } = new[]
    {
        CrossProcessNames.ShellShutdownEvent,
    };
}
