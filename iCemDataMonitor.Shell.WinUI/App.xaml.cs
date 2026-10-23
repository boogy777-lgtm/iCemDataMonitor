using Microsoft.UI.Xaml;
using iCemDataMonitor.Sdk.Diagnostics;

namespace iCemDataMonitor.Shell.WinUI;

/// <summary>
/// WinUI 3 shell (W11) — D.5 (BD-hybrid): the shell is a CLIENT of the
/// headless iCemDataMonitor.Host.exe. In-proc hosting is GONE (GAP-2 closed):
/// no Kestrel, no Modbus, no sniffer, no engine spawn in this process.
/// Startup = ensure-host flow (B.2.1): probe the host → connect, or —
/// 2026-08-31 (owner decree) — spawn it IMMEDIATELY; the «Хост не запущен /
/// Запустить хост?» dialog is REMOVED (launching the UI never asks).
/// Viewer mode (--viewer) is the ONLY degraded entry point left: it skips the
/// probe and opens straight into the read-only bus view.
/// P1 (production 2026-08-26): the SINGLE tray owner is the HOST
/// (iCemDataMonitor.Host.exe — full lifecycle menu: Открыть панель / Открыть
/// Web / Статус / консоль / Перезапуск / Закрыть). The shell creates NO tray
/// icon of its own: closing the window EXITS the shell process while the host
/// keeps running (E.2.7 intact — the panel never had a host-shutdown RPC).
/// Second-instance wake (ShowEventName) is preserved without the tray.
/// </summary>
public partial class App : Application
{
    public static Window? MainWindowInstance { get; private set; }

    private Mutex? _singleInstance;
    private MainWindow? _window;

    // Second-instance "show me" signal: a relaunched exe can't own the mutex,
    // so it pokes this named event and exits — the running instance restores
    // its window. Otherwise the user double-clicks and sees NOTHING (the shell
    // is already in the tray).
    private const string ShowEventName = @"Local\iCemDataMonitorTray_v1_ShowWindow";

    // P2 2026-08-26 (owner decree «[Закрыть] = закрытие всех UI и host»): the
    // host tray's [Закрыть] sets this event; this instance closes its window
    // and exits. Literal mirrors Host.Tray.CrossProcessNames.ShellShutdownEvent
    // (named-objects registry: specs/dotnet-server/Э2_contracts.csv). AutoReset,
    // single waiter — a pending Set survives until consumed.
    private const string ShutdownEventName = @"Local\iCemDataMonitorTray_v1_Shutdown";

    public App()
    {
        // W10: crash diagnostics — WinUI stowed exceptions (0xC000027B) carry
        // no message to the console. W23 (owner log policy 2026-08-26): ALL
        // handlers write to the station log in ProgramData via StationFileLog
        // (never-throw, 25 MB cap with in-place tail-half rewrite, lazy dir).
        UnhandledException += (_, e) => LogCrash($"XamlUnhandled: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash($"AppDomain: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash($"UnobservedTask: {e.Exception}");
            e.SetObserved(); // evidence is on file — keep the default no-crash behavior
        };
        InitializeComponent();
    }

    /// <summary>Station crash log (owner log policy): ProgramData\
    /// Halliburton\iCem\DataMonitoring\Logs\iCemDataMonitor.Shell\shell-crash.log.
    /// StationFileLog arrives transitively (Core→Sdk) — append + cap, never
    /// throws; replaces the old overwrite-on-every-crash crash.log next to the
    /// exe (history was lost on every new crash and the file grew uncapped).</summary>
    private static readonly string CrashLogPath = Path.Combine(
        StationFileLog.AppLogDir("iCemDataMonitor.Shell"), "shell-crash.log");

    private static void LogCrash(string text) => StationFileLog.Write(CrashLogPath, text);

    internal static void ShellLog(string text)
    {
        try
        {
            StationFileLog.WriteFormatted(
                Path.Combine(AppContext.BaseDirectory, "startup.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [winui] {text}{Environment.NewLine}");
        }
        catch { /* best effort */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool viewer = Environment.GetCommandLineArgs().Contains("--viewer");

        // SAME mutex as the WinForms tray — transition guard: the two shells
        // must never run side by side on the stand.
        _singleInstance = new Mutex(initiallyOwned: true, @"Global\iCemDataMonitorTray_v1", out bool createdNew);
        if (!createdNew)
        {
            ShellLog("another instance holds Global\\iCemDataMonitorTray_v1 — signalling it to show and exiting");
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
                showEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // first instance died before creating the event — nothing to signal
            }
            // BUGFIX (zombie): a bare return leaves the WinUI message loop
            // running with NO window and NO tray — an invisible process.
            Application.Current.Exit();
            return;
        }

        // P3 2026-08-27 (core-zone audit): create BOTH signal handles
        // IMMEDIATELY after the mutex is ours — BEFORE MainWindow is
        // constructed. A tray «Статус»/«Открыть панель» Set landing while the
        // window is still being built used to hit the OTHER instance's
        // WaitHandleCannotBeOpenedException (the event did not exist yet) —
        // the pending signal survived but the instant wake was delayed.
        // P4 2026-08-27 (stale-signal zombie, startup.log 08:28:25): named
        // kernel events PERSIST their signalled state across process
        // lifetimes. A host-tray «Закрыть» Set with NO shell listening stayed
        // armed — the next shell spawn consumed the stale Set and exited
        // seconds after spawning (the panel died on every spawn). Same class:
        // a stale ShowEventName Set ⇒ spurious ShowFromTray right after start.
        // Reset = сигналы прошлых жизней процесса не наследуются. A post-Reset
        // Set (arriving DURING window construction) stays armed until the wait
        // loop below consumes it — closing/showing right after start is the
        // CORRECT response to a live request.
        var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        wakeEvent.Reset();
        shutdownEvent.Reset();

        ShellLog("starting shell as a CLIENT of iCemDataMonitor.Host (in-proc hosting removed, GAP-2)");
        // P1 (production 2026-08-26): single tray owner = HOST — the shell runs
        // windowless-tray (no icon of its own); close = exit of THIS process.
        ShellLog("shell runs windowless-tray: single tray owner = host");
        _window = new MainWindow(viewerMode: viewer);
        MainWindowInstance = _window;
        // Wait loops start only AFTER the window exists — capture the
        // constructed instance so the loops need no null-forgiveness.
        var window = _window;

        // Second-instance wake, UNCONDITIONAL (was gated on SessionReady, which
        // existed only to create the tray): a relaunched exe can't own the
        // mutex, pokes ShowEventName and exits — this instance restores its
        // window. Needed even with no tray icon.
        Task.Run(() =>
        {
            while (wakeEvent.WaitOne())
                window.DispatcherQueue.TryEnqueue(() => window.ShowFromTray());
        });

        // P2 2026-08-26: host tray [Закрыть] = close ALL UI + host. This
        // instance exits when the host signals the shutdown event. Closing the
        // window is the canonical exit path (P1: close = exit of this process,
        // the host is never stopped from the panel).
        Task.Run(() =>
        {
            shutdownEvent.WaitOne();
            ShellLog("host tray [Закрыть] — closing the panel");
            window.DispatcherQueue.TryEnqueue(() => window.Close());
        });

        _window.Activate();
    }
}
