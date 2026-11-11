namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// Named kernel objects OWNED BY THE SHELL (WinUI UI single-instance domain).
/// W20 F.2 (owner decree «спина к спине»): the legacy Tray backend is removed
/// from the solution, and with it the old "legacy conflict" reading of these
/// names — they have NOTHING to do with backend conflicts any more. The Host
/// only MIRRORS them for its own signal/spawn decision ([Открыть панель]):
/// mutex held ⇒ signal the show-window event; free ⇒ spawn the shell exe.
/// Registry of named objects: specs/dotnet-server/Э2_contracts.csv; the
/// literals MIRROR Shell.WinUI App.xaml.cs (the Contracts pipe-name law covers
/// pipes only — these are mutex/event names, kept next to their single
/// consumer here and referenced in the registry row).
/// </summary>
public static class CrossProcessNames
{
    /// <summary>
    /// Single-instance mutex of the WinUI shell (UI domain: one panel window).
    /// OWNER: the shell (App.xaml.cs). Host.exe NEVER acquires it — it only
    /// probes it to decide signal-vs-spawn for [Открыть панель].
    /// </summary>
    public const string ShellMutex = @"Global\iCemDataMonitorTray_v1";

    /// <summary>
    /// Second-instance "show window" signal of the WinUI shell (UI domain:
    /// PanelOpen). OWNER: the shell; Host only sets it when the shell is
    /// already running.
    /// </summary>
    public const string ShellShowWindowEvent = @"Local\iCemDataMonitorTray_v1_ShowWindow";

    /// <summary>
    /// P2 2026-08-26 (owner decree «[Закрыть] = закрытие всех UI и host»): the
    /// tray [Закрыть] sets this event; the WinUI panel listens and closes
    /// itself gracefully. OWNER: the shell (App.xaml.cs). AutoReset, ONE
    /// waiter — a pending Set survives until the listener consumes it.
    /// </summary>
    public const string ShellShutdownEvent = @"Local\iCemDataMonitorTray_v1_Shutdown";

    /// <summary>
    /// 2026-08-31 (owner decree): shell → host restart poke for the shell's
    /// «Перезапуск хоста» button. INVERTED OWNERSHIP vs the names above: the
    /// HOST is the OWNER (it creates the event and waits on it); the WinUI
    /// shell only SETS it. Same event-poke pattern as ShellShutdownEvent,
    /// direction reversed — E.2.7 intact: the host restarts ITSELF through
    /// the identical TrayActionGate + RestartPlan path as its tray
    /// [Перезапуск]; the shell has no restart RPC and never spawns/kills the
    /// host here. AutoReset, ONE waiter; the host Reset()s it at boot so a
    /// stale Set from a previous host lifetime is never inherited (the P4
    /// stale-signal zombie lesson). Mirror constant: Shell.WinUI MainWindow.
    /// </summary>
    public const string HostRestartEvent = @"Local\iCemDataMonitorTray_v1_Restart";

    /// <summary>
    /// 2026-08-31 (слой CLI, owner decree «закрой пробел CLI — n+1 источник
    /// команд»): client → host SHUTDOWN poke for the CLI `stop` verb (and any
    /// future n+1 command source). SAME inverted-ownership pattern as
    /// HostRestartEvent: the HOST creates and waits; the client only SETS it.
    /// The host runs the IDENTICAL RequestClose as its tray [Закрыть]
    /// (HostLifecycle) — E.2.7 intact: the host closes ITSELF. AutoReset, ONE
    /// waiter; Reset() at boot (P4 stale-signal lesson). NOT part of
    /// TrayCloseContract.UiShutdownEventNames — that list is the REVERSE
    /// direction (host → UI).
    /// </summary>
    public const string HostShutdownEvent = @"Local\iCemDataMonitorTray_v1_HostShutdown";

    /// <summary>
    /// 2026-08-31 (owner decree «даже смена темы — тоже CLI: один bool для
    /// одновременного использования в web и winui»): the station-wide theme
    /// poke. The WRITER saves the ui-theme bool first, then SETs this event;
    /// every listener (WinUI shell StartThemeListener today, any future UI
    /// surface) re-reads the file — the flip is SIMULTANEOUS across live UIs.
    /// OWNER: the shell (creates + waits — MainWindow.UiThemeChangedEventName,
    /// mirrored literal here per the registry law of this class). The HOST is
    /// only a WRITER (UiPrefsStore pokes it after SetUiTheme's file write) —
    /// same writer role as the shell's own toggle, no ownership inversion.
    /// AutoReset, ONE waiter. NOT owner-created by the host: OpenExisting —
    /// nobody listening is fine.
    /// </summary>
    public const string UiThemeChangedEvent = iCemDataMonitor.Core.Config.UiThemePreferenceStore.ChangedEventName;

    // 2026-08-27 TOMBSTONE: EmuUiShutdownEvent (Local\iCemEmuleUi_v1_Shutdown)
    // REMOVED — the emulator is now a SEPARATE INDEPENDENT app (own exe,
    // built-in UI+CLI); tray [Закрыть] closes host + WinUI panel ONLY and
    // must NOT close the emulator.
}
