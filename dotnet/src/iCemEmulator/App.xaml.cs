using Microsoft.UI.Xaml;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Core;

namespace Halliburton.iCemEmulator;

/// <summary>
/// iCemEmulator (owner decree 2026-08-27) — the emulator's ONE app: a WinUI 3
/// window hosting the EmulatorPage AND the engine IN-PROCESS
/// (EmuEngineHost.Start() — no child spawn, no host RPC, no IEngineControlChannel).
/// The engine keeps the control pipe (\\.\pipe\iCemEmule.Control) so
/// `iCemEmulator.exe --cli <command>` talks to THIS running instance; the
/// window close (X / Alt+F4) HIDES to the tray (W15, owner request
/// 2026-08-28) — the process and the engine keep living; the REAL exit paths
/// are the tray menu «Выход» and the pipe `quit`, both funnelling into
/// RequestAppExit (engine stopped, settings saved).
/// </summary>
public partial class App : Application
{
    private EmuEngineHost? _engine;
    private MainWindow? _window;
    private TrayIconHost? _tray;

    public App()
    {
        // W23 (owner log policy 2026-08-26): crashes/errors → station log
        // C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\iCemEmulator\
        // crash.log via the engine library's CrashLog (canonical TapLog
        // contract, never-throw, 25 MB cap, lazy dir). The app renames the
        // evidence folder at startup (engine-internal logs included).
        CrashLog.UseAppName(EmulatorAppContract.ProcessName);
        UnhandledException += (_, e) => CrashLog.Log($"XamlUnhandled: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Log($"AppDomain: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Log($"UnobservedTask: {e.Exception}");
            e.SetObserved(); // evidence is on file — keep the default no-crash behavior
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Engine IN-PROCESS: emule_config.xml is loaded once and honored at
        // startup (passive defaults — streams/emulation start only when
        // emule_config.xml explicitly enables them; the transport live gate
        // patches emule_config.xml pre-launch and relies on this).
        // 2026-08-31: canonical path moved to ProgramData\...\DataMonitoring
        // (legacy %LOCALAPPDATA% settings.xml is copy-migrated on first load).
        _engine = new EmuEngineHost(EmuSettings.Load());
        _engine.Start();

        // P5 2026-08-28 (root cause of «НЕ РАБОТАЮТ ПЕРЕКЛЮЧАТЕЛИ», live repro
        // 4/8): with the legacy iCemEmule.exe TrayApp ALSO running, TWO
        // servers listen on \\.\pipe\iCemEmule.Control and a client
        // connection lands on a RANDOM engine — the UI's toggle commands were
        // randomly answered by the WRONG engine while the 1 Hz snapshot read
        // THIS process's engine ⇒ switches dead/flickering. The engine is
        // IN-PROCESS — the UI executes the SAME JSON grammar (CommandProcessor)
        // straight against it, no OS-pipe hop: ONE source of truth. The pipe
        // server stays bound for the CLI and external tools.
        ControlPipeClient.LocalExecutor = line => _engine.Execute(line);

        _window = new MainWindow();

        // W15 (owner request 2026-08-28): tray presence — X hides the window,
        // the process keeps living in the tray; «Выход» is the real exit.
        _tray = new TrayIconHost();
        _tray.OpenRequested += () => _window?.ShowFromTray();
        _tray.ExitRequested += RequestAppExit;

        // W16 (owner 2026-08-29: «оставь подсказки — COM on/off и LAN on/off»):
        // the tray tooltip reflects the LIVE stream states from the in-proc
        // engine (the ONE source of truth), refreshed every second.
        _tipTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _tipTimer.Tick += (_, _) => RefreshTrayTip();
        _tipTimer.Start();
        RefreshTrayTip();

        // A REAL window close (only reachable via AllowRealClose) stops the
        // engine (settings saved). Stop() is idempotent — RequestAppExit may
        // have stopped it already.
        _window.Closed += (_, _) => _engine?.Stop();

        // Pipe `quit` (CLI) must close the app too: WaitForQuit unblocks when
        // the engine's control plane requests quit — funnel into the ONE real
        // exit path (works with the window hidden in the tray as well).
        Task.Run(() =>
        {
            try
            {
                _engine.WaitForQuit();
            }
            catch { return; }
            _window.DispatcherQueue.TryEnqueue(RequestAppExit);
        });

        _window.Activate();
    }

    private Microsoft.UI.Xaml.DispatcherTimer? _tipTimer;

    /// <summary>W16: tray tooltip = «iCemEmulator · COM: вкл/выкл · LAN:
    /// вкл/выкл» from the in-proc engine's GetStatus() (same JSON the CLI
    /// reads — one source of truth). Runs on the UI thread (DispatcherTimer).</summary>
    private void RefreshTrayTip()
    {
        if (_tray is null || _engine is null) return;
        try
        {
            var status = _engine.GetStatus();
            bool comOn = TryStreaming(status, "com");
            bool lanOn = TryStreaming(status, "lan");
            _tray.UpdateTip(
                $"iCemEmulator · COM: {(comOn ? "вкл" : "выкл")} · LAN: {(lanOn ? "вкл" : "выкл")}");
        }
        catch { /* tray cosmetics must never fault the app */ }
    }

    private static bool TryStreaming(System.Collections.Generic.Dictionary<string, object?> status, string channel)
    {
        return status.TryGetValue(channel, out var node)
            && node is System.Collections.Generic.Dictionary<string, object?> d
            && d.TryGetValue("streaming", out var s)
            && s is bool b
            && b;
    }

    /// <summary>The ONE real-exit path (tray «Выход» / pipe `quit`): release
    /// the tray icon, let the window close for real, stop the engine (Stop is
    /// idempotent and saves emule_config.xml — the window's Closed handler
    /// stops it again harmlessly), then exit the UI thread.</summary>
    private void RequestAppExit()
    {
        try { _tray?.Dispose(); } catch { }
        _window?.AllowRealClose();
        try { _engine?.Stop(); } catch { }
        Application.Current.Exit();
    }
}
