using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Host;
using iCemDataMonitor.Host.CliMode;
using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Host.Core.Health;
using iCemDataMonitor.Host.Tray;
using iCemDataMonitor.Sdk.Bus;

// ═══════════════════════════════════════════════════════════════════════════
// iCemDataMonitor.Host — headless station host (E.2 tray edition, W19).
//
// Windows Service is REJECTED (owner decision 2026-08-24, UAC): this exe is a
// USER console process forever (rollback law §5). Autostart = HKCU Run-key via
// tools\install-autostart.ps1 (--autostart hides the console).
//
// E.2 (W19): the host's ONLY visual surface is a NotifyIcon with a control
// menu (HostTray). No windows/dashboard/WebView — the panel IS the existing
// WinUI shell, spawned by [Открыть панель]. The icon reuses the existing app
// asset (embedded iCemDataMonitor.ico).
//
// E.2.7 LAW: the host is stopped ONLY by its own tray ([Закрыть] /
// [Перезапуск]) — the remote stop-RPC was REMOVED from proto/service/clients.
// DOCUMENTED EXCEPTION: Ctrl+C in an interactive console session remains a
// graceful stop (default ConsoleLifetime behaviour). Rationale: the local
// keyboard IS the physical operator — this path cannot be invoked remotely or
// programmatically by clients; it exists for manual diagnostics runs only.
//
// W20 F.2 (owner decree «спина к спине»): the legacy Tray is REMOVED from the
// solution — the second backend no longer exists structurally, so the former
// E.3.2 startup refusal (LegacyTrayConflictGuard) is DELETED with it: there is
// nothing left to conflict with. CrossProcessNames.ShellMutex now belongs to
// the UI single-instance domain ONLY (Host mirrors it for the [Открыть панель]
// signal-or-spawn decision).
//
// 2026-08-27 (owner decree, CORRECTED same day):
//   1. ENGINE SUPERVISION DELETED — the emulator is a SEPARATE INDEPENDENT
//      app (own exe, built-in UI+CLI, own folder); the host never launches or
//      supervises it. No EngineSupervisor, no /api/engine, no emule boot text.
//   2. «Web - удали (контрол sim)» — corrected decree: the sim CONTROL on the
//      dashboard page was removed, NOT the page. The web/SPA layer is
//      RESTORED: wwwroot static files, gRPC-Web, web_on/web_remote_on keys,
//      ring history and the «Открыть Web» tray item are back.
//   3. Tray menu is: Открыть панель · Открыть Web · Показать/Скрыть консоль ·
//      Перезапуск · Закрыть. («Статус» stays REMOVED.)
//   4. Console X MINIMIZES to tray — the process SURVIVES. FIX in this file:
//      ConsoleCloseGuard.Reinstall() AFTER app.StartAsync() (see the W21 G.1
//      dispatch-order trap in ConsoleCloseGuard — ConsoleLifetime registers
//      its own handler during Build and would otherwise run first).
//   5. [Закрыть] closes host + WinUI panel ONLY — NOT the emulator (it is
//      independent now).
// ═══════════════════════════════════════════════════════════════════════════

return await ProgramMain(args);

static async Task<int> ProgramMain(string[] args)
{
    // ── -1. CLI MODE dispatcher (2026-08-31, owner decree «пункт 3») ────────
    // The retired icemdatamonitor-cli.exe lives on INSIDE this exe: argv in
    // the cli grammar (snapshot|get|header|stop|restart [--start]) runs the
    // thin-forwarder command and EXITS — BEFORE the console-close guard, the
    // single-instance mutex, config, Kestrel and the tray ever start. A cli
    // invocation is a short-lived remote control of an ALREADY-RUNNING host
    // (or, with --start/restart, spawns one); it must never install host
    // machinery (the close guard would even HIDE the operator's console on X
    // — wrong for a CLI).
    // Classification is total: CliForwarder.IsCliInvocation's tokens never
    // collide with the host flags (--autostart/--port/--ini/--help), so every
    // existing host invocation falls through unchanged.
    if (CliForwarder.IsCliInvocation(args))
        return CliForwarder.CliMain(args);

    var log = StartupLog.Create();

    // ── 0. Console close guard (W21 G.1) — FIRST of all: an X click even
    //       during boot must hide the console, never kill the host. ──────────
    ConsoleCloseGuard.Install(log);

    // ── 1. Argument parsing (fail-fast on unknown args) ─────────────────────
    var parsed = HostArgs.Parse(args);
    if (parsed.ShowHelp)
    {
        Console.WriteLine(HostArgs.HelpText);
        return 0;
    }
    if (!parsed.IsValid)
    {
        foreach (var e in parsed.Errors)
            Console.Error.WriteLine($"host: argument error: {e}");
        Console.Error.WriteLine(HostArgs.HelpText);
        return 2;
    }

    // ── 2. Single-instance guard (B.1.2) — fail-fast, never silent ──────────
    using var guard = AcquireSingleInstanceGuard();
    if (!guard.Acquired)
    {
        Console.Error.WriteLine(
            $"host: another instance already holds {SingleInstanceGuard.DefaultMutexName} — exiting (fail-fast)");
        return 1;
    }

    log($"host starting pid={Environment.ProcessId} args=[{string.Join(' ', args)}]");

    // ── 3. Boot-stage tracker (W21.3) — created BEFORE any startup work so
    //       even pre-Kestrel stages are captured for the splashscreen replay. ─
    var boot = new iCemDataMonitor.Host.Boot.BootStageTracker();

    // ── 4. station config (canonical config.ini search order or explicit --ini) ──
    boot.Begin("ini", "конфигурация");
    HostBootContext bootContext;
    try
    {
        bootContext = HostAppFactory.LoadBootContext(
            new HostBuildSpec(args, parsed.IniPath, parsed.Port), log);
        boot.Complete("ini", $"port={bootContext.Ini.WebPort} modbus={bootContext.Ini.ModbusOn}");
    }
    catch (Exception ex)
    {
        log($"config FAILED: {ex.Message}");
        Console.Error.WriteLine($"host: config.ini invalid — {ex.Message}");
        return 1; // stage "ini" stays UNCHECKED — honest splash on failed boot
    }

    // ── 5. Build the full composition (Kestrel + DI + endpoints) ────────────
    boot.Begin("composition", "композиция");
    WebApplication app;
    try
    {
        app = HostAppFactory.Build(bootContext, log, boot);
        boot.Complete("composition");
    }
    catch (Exception ex)
    {
        log($"composition build FAILED: {ex}");
        throw; // fail-fast: a half-composed host must not run
    }

    // ── 6. Start + layers + READY marker + tray (A.2.4 / E.2) ───────────────
    HostTray? tray = null;
    HostCommandListeners? commandListeners = null;
    try
    {
        boot.Begin("web", "веб-сервер");
        await app.StartAsync();
        // 2026-08-27 dispatch-order fix (owner decree): ConsoleLifetime
        // registered its OWN SetConsoleCtrlHandler (→ StopApplication) while
        // the host started (WaitForStartAsync — i.e. BEFORE our re-registration
        // here), AFTER our Install() at the top of ProgramMain. Windows
        // dispatches console handlers in REVERSE registration order, so
        // ConsoleLifetime would see CTRL_CLOSE first and kill the host on X.
        // Re-adding OUR handler NOW moves it to the HEAD of the dispatch list:
        // it swallows CTRL_CLOSE (returns true) before ConsoleLifetime runs.
        // Evidence line lands in startup.log when the native call succeeds.
        ConsoleCloseGuard.Reinstall();
        var boundPort = HostAppFactory.GetBoundPort(app);
        boot.Complete("web", $"http://localhost:{boundPort}");

        var layers = LayerFlags.From(bootContext.Ini);
        boot.Begin("layers", "слои");
        await HostComposition.StartLayersAsync(app.Services, layers, log,
            app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        boot.Complete("layers", $"modbus={layers.Modbus} sniffer={layers.LanSniffer} opcda={layers.OpcDa}");

        var publisher = app.Services.GetService<iCemDataMonitor.Sdk.IModbusPublisher>();
        var busState = app.Services.GetRequiredService<iCemDataMonitor.Data.Bus.ILatestBusState>().Current;
        var marker = StartupLog.ReadyMarker(
            modbusConfigured: layers.Modbus,
            modbusBound: publisher?.IsBound ?? false,
            webPort: boundPort,
            busOpen: busState.IsOpen);
        log(marker);
        boot.Begin("ready", "готовность");
        boot.Complete("ready", marker);

        ApplyConsoleMode(parsed, log, app.Services.GetRequiredService<ConsoleVisibilityService>());
        tray = StartTray(app, boundPort, args, log, out commandListeners);

        await app.WaitForShutdownAsync();
    }
    catch (Exception ex)
    {
        log($"host runtime FAILED: {ex}");
        throw;
    }
    finally
    {
        // Ghost-icon lesson (Windows 11): the tray icon is disposed FIRST,
        // before any other teardown — otherwise a dead icon lingers in the
        // tray corner until the operator hovers it.
        try { tray?.Dispose(); } catch { /* best effort */ }

        try { commandListeners?.Dispose(); } catch (Exception ex) { log($"host command listener teardown: {ex}"); }

        IHost host = app;
        try { await host.StopAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        host.Dispose();
        log("host stopped");
    }

    return 0;
}

/// <summary>
/// Own single-instance acquisition with a bounded self-restart grace: under
/// RestartPlan.GraceEnvVar the successor retries while the predecessor drains
/// and releases the mutex. WITHOUT the env-var the first failed acquisition
/// stays an honest operator error (fail-fast, no silent retry).
/// </summary>
static SingleInstanceGuard AcquireSingleInstanceGuard()
{
    if (Environment.GetEnvironmentVariable(RestartPlan.GraceEnvVar) != "1")
        return new SingleInstanceGuard();

    var deadlineTicks = Environment.TickCount64 + (long)RestartPlan.MutexGraceWindow.TotalMilliseconds;
    while (Environment.TickCount64 < deadlineTicks)
    {
        var attempt = new SingleInstanceGuard();
        if (attempt.Acquired)
            return attempt;
        attempt.Dispose();
        Thread.Sleep(500);
    }
    return new SingleInstanceGuard(); // final attempt reports honestly via Acquired=false
}

/// <summary>
/// E.2.4 console contract at boot (see ConsoleWindow.Decide decision table).
/// 2026-08-28 OWNER LAW: the --autostart hide goes through the SAME
/// ConsoleVisibilityService as the tray toggle and the SetConsoleVisible RPC
/// (one service — the owner's «команды из трея не привязаны к общим командам
/// host» fix); the boot evidence lines are unchanged so the startup.log keeps
/// its comparability across sessions.
/// </summary>
static void ApplyConsoleMode(HostArgs parsed, Action<string> log, ConsoleVisibilityService console)
{
    switch (ConsoleWindow.Decide(hasConsoleWindow: ConsoleWindow.Handle != IntPtr.Zero, autostartRequested: parsed.Autostart))
    {
        case ConsoleLaunchMode.HiddenSpawn:
            log("console: none (spawned with CreateNoWindow)");
            break;

        case ConsoleLaunchMode.AutostartHide:
            console.Hide(); // one service: policy table + [console] evidence line
            log("console: hidden (--autostart; reopen via the tray menu)");
            break;

        case ConsoleLaunchMode.ManualVisible:
            ConsoleWindow.WriteBanner(); // ONE banner text, shared with the demand-attach path
            log("console: visible (manual run — diagnostics)");
            break;
    }
}

/// <summary>
/// E.2.2/E.2.3: wire the tray menu to live services. [Перезапуск] spawns the
/// successor BEFORE draining (RestartPlan); [Закрыть] and [Перезапуск] are the
/// ONLY two commands that stop the host — both go through the EXISTING graceful
/// StopApplication path. 2026-08-27 RESTORED: «Открыть Web» is back between
/// [Открыть панель] and the console toggle; «Статус» stays removed. The
/// tooltip no longer carries an engine pid (engine supervision deleted — the
/// emulator is a separate independent app).
/// </summary>
static HostTray StartTray(WebApplication app, int webPort, string[] args, Action<string> log,
    out HostCommandListeners commandListeners)
{
    var health = app.Services.GetRequiredService<StationHealthState>();
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    // 2026-08-28 OWNER LAW (ONE console service): the tray console toggle and
    // the SetConsoleVisible RPC resolve to the SAME ConsoleVisibilityService
    // instance — one policy table, one evidence log, no second command path.
    var console = app.Services.GetRequiredService<ConsoleVisibilityService>();

    // P3 2026-08-27 + 2026-08-31 (owner decree «единый механизм запуска/
    // рестарта/закрытия»): ONE lifecycle coordinator — a miniature SCM. The
    // tray menu AND the shell's named-event pokes call the SAME HostLifecycle
    // verbs; the gate/restart/close logic no longer lives in local closures
    // (a mechanism as a code fact, not a convention). E.2.7 intact.
    var lifecycle = new HostLifecycle(args, lifetime, log);

    commandListeners = new HostCommandListeners(
        CrossProcessNames.HostRestartEvent, CrossProcessNames.HostShutdownEvent,
        lifetime.ApplicationStopping,
        restart: () =>
        {
            log("[restart] shell signalled the restart event — running the tray [Перезапуск] path");
            lifecycle.RequestRestart();
        },
        close: () =>
        {
            log("[close] cli signalled the shutdown event — running the tray [Закрыть] path");
            lifecycle.RequestClose();
        });

    var tray = new HostTray(
        tooltipProvider: () => TrayTooltipFormatter.Format(
            busOpen: health.Current.BusOpen,
            heartbeat: health.Current.Heartbeat,
            webPort: webPort),
        openPanel: () => OpenPanel(log),
        openWeb: () => OpenWeb(webPort, log),
        showConsole: () => console.Show(),
        hideConsole: () => console.Hide(),
        // 2026-08-31 (единый механизм): both terminal verbs live on the ONE
        // HostLifecycle coordinator — the menu never implements logic itself.
        restart: lifecycle.RequestRestart,
        close: lifecycle.RequestClose,
        log: log);

    // 2026-08-31 (blunt-restart decree): hand the tray to the lifecycle
    // coordinator (the restart verb is captured BY the tray at construction,
    // so the coordinator cannot reference the tray variable directly) — its
    // pre-exit chore is the icon hide (ghost-icon lesson).
    lifecycle.Tray = tray;

    try
    {
        tray.Start();
        log("tray icon active (E.2)");
        return tray;
    }
    catch
    {
        tray.Dispose();
        throw;
    }
}

/// <summary>[Открыть панель]: signal-or-spawn per PanelOpenDecision table.</summary>
static void OpenPanel(Action<string> log)
{    var taken = MutexProbe.IsTaken(CrossProcessNames.ShellMutex);
    var exe = taken ? null : ShellExeResolver.ResolveShellExePath(AppContext.BaseDirectory);
    var decision = PanelOpenPolicy.Decide(taken, exe);
    log($"[panel] {decision.LogLine}");

    switch (decision.Action)
    {
        case PanelOpenAction.SignalShowEvent:
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(CrossProcessNames.ShellShowWindowEvent);
                showEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                log("[panel] show-event not found — shell died between probes; nothing signalled");
            }
            break;

        case PanelOpenAction.SpawnShell:
            // UseShellExecute=false + explicit WorkingDirectory (exe folder):
            // the child must never inherit the host's cwd (P2 hygiene, same
            // doctrine as RestartPlan.Build). The unpackaged WinUI shell
            // resolves everything from AppContext.BaseDirectory anyway.
            Process.Start(new ProcessStartInfo(exe!)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe!),
            });
            break;

        case PanelOpenAction.ReportError:
            Console.Error.WriteLine($"host: {decision.LogLine}");
            break;
    }
}

/// <summary>
/// [Открыть Web] (RESTORED 2026-08-27, corrected decree): open the SPA
/// dashboard in the default browser. UseShellExecute=true — the URL goes to
/// the OS shell association, never to the host process. The :web_port endpoint
/// is ALWAYS listened on (the web_on flag gates only the SPA surface), so the
/// click is safe regardless of the flag.
/// </summary>
static void OpenWeb(int webPort, Action<string> log)
{
    var url = $"http://localhost:{webPort}";
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        log($"[web] {url} — opened in the default browser");
    }
    catch (Exception ex)
    {
        // A missing/broken browser association must not kill the tray pump
        // (Guarded already isolates the pump, but the log must carry the truth).
        log($"[web] open FAILED for {url}: {ex.Message}");
        Console.Error.WriteLine($"host: [Открыть Web] failed: {ex.Message}");
    }
}

// The mutex liveness probe moved to Tray.MutexProbe (W21.3) — shared by the
// [Открыть панель] signal-or-spawn path AND the NavigateUi RPC.

/// <summary>Makes the entry point visible to WebApplicationFactory-style tests.</summary>
public partial class Program;
