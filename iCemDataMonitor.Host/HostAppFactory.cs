using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Grpc.AspNetCore.Web;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Data.Telemetry;
using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Host.Grpc;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Host;

/// <summary>
/// D.3' build pipeline of the headless host, factored out of Program so tests
/// can exercise the FULL composition (Kestrel + DI + endpoints) in-process
/// without the single-instance mutex and without touching production ini.
///
/// 2026-08-27 (owner decree): engine supervision is DELETED — the emulator is
/// now a SEPARATE INDEPENDENT app (own exe, own UI+CLI, own folder); the host
/// never launches or supervises it. No /api/engine.
///
/// 2026-08-27 RESTORED (corrected decree «Web - удали (контрол sim)» = remove
/// the sim control from the dashboard page, not the page): the web/SPA layer
/// is BACK — when web_on=1 the pipeline wires, IN ORDER: gRPC-Web middleware
/// (DefaultEnabled — browser clients speak gRPC-Web on the SAME socket), the
/// static SPA (UseDefaultFiles + UseStaticFiles over the deployed wwwroot),
/// then the gRPC service + frozen /api/* surface, and MapFallbackToFile
/// (index.html) LAST so the SPA router owns every non-API path. With web_on=0
/// none of that is wired — the surface stays pure gRPC (pipe + TCP) + /api/*.
/// </summary>
public sealed record HostBuildSpec(
    string[] Args,
    string? IniPath = null,
    int? PortOverride = null,
    string? ContentRootOverride = null);

/// <summary>One startup config load, its actual persistence target and binding decision.</summary>
public sealed class HostBootContext
{
    internal HostBootContext(HostBuildSpec spec, IniOptions ini, string iniPath, KestrelBindPlan bindPlan)
    {
        Spec = spec;
        Ini = ini;
        IniPath = iniPath;
        BindPlan = bindPlan;
    }

    internal HostBuildSpec Spec { get; }
    public IniOptions Ini { get; }
    public string IniPath { get; }
    public KestrelBindPlan BindPlan { get; }
}

public static class HostAppFactory
{
    /// <summary>2026-08-31 (owner decree «ускорить перезапуск — сохранение
    /// данных не нужно, через 1 секунду будут новые от dll»): the graceful-
    /// drain budget. The host is STATELESS across restarts — the bus is
    /// re-fed by the AddIn at ~1 Hz and every client reconnects by design —
    /// so there is NOTHING to drain for: Kestrel listeners (pipe, :502) are
    /// released at the very START of the stop, and the only thing the cap
    /// bounds is how long the process lingers on connections the shell never
    /// closes. 1 s ⇒ the successor acquires the single-instance mutex ~1.5 s
    /// after the click and its pipe answers at ~3 s — the metrics FSM's
    /// first backoff retry (+3 s) lands on an ALREADY-serving host.
    /// Autopsy 2026-08-31 (why not the 30 s framework default): the default
    /// OUTLIVED the successor's 15 s mutex grace (RestartPlan.
    /// MutexGraceWindow) — self-restart 10:35:44Z → «host stopped» 10:36:14Z,
    /// every restart successor died on the single-instance guard and the
    /// stand ended with NO host.</summary>
    public static readonly TimeSpan ShutdownDrainBudget = TimeSpan.FromSeconds(1);

    /// <summary>Load the station config per spec (explicit path or the
    /// canonical search order). Fail-fast on violations. A rename-migration
    /// event (legacy station.ini → canonical config.ini, or an honest copy
    /// failure) is emitted on the startup log — evidence, ¬ молчание.</summary>
    public static IniOptions LoadIni(HostBuildSpec spec, Action<string>? log = null)
        => LoadIniWithPath(spec, log).Ini;

    private static (IniOptions Ini, string Path) LoadIniWithPath(HostBuildSpec spec, Action<string>? log)
    {
        bool searched = string.IsNullOrWhiteSpace(spec.IniPath);
        var ini = searched ? IniConfig.Load() : IniConfig.Load(spec.IniPath!);
        // Capture before logging or building: another load may change the global active path.
        var path = Path.GetFullPath(searched ? IniConfig.ActivePath : spec.IniPath!);
        if (searched && IniConfig.LastLoadNote is { } note)
            log?.Invoke(note);
        return (ini, path);
    }

    /// <summary>Read config once and select the bind plan without changing persisted options.</summary>
    public static HostBootContext LoadBootContext(HostBuildSpec spec, Action<string>? log = null)
    {
        var (ini, path) = LoadIniWithPath(spec, log);
        var bind = KestrelBinding.Plan(spec.PortOverride ?? ini.WebPort, ini.WebRemoteOn == 1);
        return new HostBootContext(spec, ini, path, bind);
    }

    /// <summary>
    /// Build (NOT start) the host application: loopback Kestrel endpoint,
    /// shared composition services, layer controller, frozen ops surface.
    /// The caller starts it; Program re-registers the console close guard
    /// after StartAsync (ConsoleLifetime's own handler is registered during
    /// Build and would otherwise dispatch first — see ConsoleCloseGuard).
    /// </summary>
    public static WebApplication Build(HostBuildSpec spec, Action<string>? log = null,
        Boot.BootStageTracker? bootStages = null)
        => Build(LoadBootContext(spec, log), log, bootStages);

    /// <summary>Build from the loaded startup context without reading INI again.</summary>
    public static WebApplication Build(HostBootContext context, Action<string>? log = null,
        Boot.BootStageTracker? bootStages = null)
    {
        var logLine = log ?? (_ => { });
        var spec = context.Spec;
        var ini = context.Ini;
        var layers = LayerFlags.From(ini);
        var registry = HostComposition.LoadRegistry(logLine);

        var bind = context.BindPlan;
        logLine($"config.ini OK — modbus={ini.ModbusOn} sniffer={ini.LansnifferOn} web={ini.WebOn} " +
                $"port={bind.Port} bind={bind.Address}");

        var builder = HostComposition.CreateWebApplicationBuilder(spec.Args, bind, spec.ContentRootOverride);

        // 2026-08-31 (owner decree): the graceful drain is bounded to
        // ShutdownDrainBudget — see the constant's autopsy note (30 s default
        // ⇒ dead restart successors on the single-instance guard).
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownDrainBudget);

        HostComposition.RegisterCoreServices(builder.Services, ini, layers, registry, logLine);
        HostComposition.RegisterLayerController(
            builder.Services, ini, layers,
            // 2026-08-28 active-path law: runtime persists MUST land on the file
            // Load() actually used (canonical after a migration, legacy on a
            // failed copy) — the old next-to-exe fallback silently LOST toggles
            // when the config was loaded from ProgramData.
            iniPath: context.IniPath);

        // === D.2 gRPC endpoint region (transport zone) ===
        // Telemetry sampler (D.4) over the shared BusReader — sensor-at-send.
        // E.1 (owner decree 2026-08-24, W19): ALL calls are ANONYMOUS —
        // authorization = closed-contour isolation. No interceptors, no
        // host.token (file neither minted nor read anymore; legacy file on a
        // stand is inert garbage — safe to delete manually).
        builder.Services.AddSingleton<MetricsSampler>();
        builder.Services.AddGrpc();

        // W21.3: the SAME tracker instance that recorded the pre-Kestrel stages
        // in ProgramMain — registering a fresh one would lose them.
        if (bootStages is not null)
            builder.Services.AddSingleton(bootStages);
        // Pending-UI-navigation coordinator (hierarchical CLI sections).
        builder.Services.AddSingleton<Tray.UiNavigationService>();
        // HKCU Run-key autostart truth/toggle (SetAutostart RPC, 2026-08-27).
        builder.Services.AddSingleton<Tray.AutostartManager>();
        // 2026-08-31 (owner decree «даже смена темы — тоже CLI»): station-wide
        // ui-theme truth behind the GetUiPrefs/SetUiTheme RPCs — the SAME file
        // the WinUI shell reads/writes; the host owns the WRITE in client mode.
        builder.Services.AddSingleton<Tray.UiPrefsStore>();
        // 2026-08-28 OWNER LAW: the ONE console-visibility service — the tray
        // toggle, the SetConsoleVisible RPC and the --autostart hide all route
        // through this instance (policy table + startup-log evidence).
        builder.Services.AddSingleton(new Tray.ConsoleVisibilityService(logLine));

        // Loopback TCP from KestrelBinding PLUS the local named-pipe channel
        // for WinUI (D.6.2 name from Contracts). Pipe is HTTP/2-only (gRPC
        // requirement); TCP keeps Http1AndHttp2 for /api/* JSON + local tools.
        builder.WebHost.ConfigureKestrel(options =>
            options.ListenNamedPipe(HostChannelNames.PipeName,
                listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));

        var app = builder.Build();

        // RESTORED 2026-08-27 (corrected decree): web_on=1 wires the SPA
        // pipeline. ORDER MATTERS: gRPC-Web middleware FIRST (it must wrap the
        // gRPC endpoints mapped below — DefaultEnabled makes every gRPC call
        // answer gRPC-Web for browser clients), then the static-file pair
        // (UseDefaultFiles rewrites / → /index.html, UseStaticFiles serves
        // wwwroot), all BEFORE the endpoint mapping so the middleware sits in
        // front of the terminal endpoints in the pipeline.
        if (layers.Web)
        {
            app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        app.MapGrpcService<TelemetryServiceImpl>();

        app.MapFrozenOpsSurface(ini, layers);

        // Fallback LAST: every path not matched by gRPC//api/* (and not served
        // by static files) renders the SPA shell. web_on=0 ⇒ no fallback —
        // unknown paths 404 honestly.
        if (layers.Web)
            app.MapFallbackToFile("index.html");

        return app;
    }

    /// <summary>Actual bound port after StartAsync (from server features).</summary>
    public static int GetBoundPort(WebApplication app)
    {
        var feature = ((IApplicationBuilder)app).ServerFeatures.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("server does not expose IServerAddressesFeature");
        foreach (var a in feature.Addresses)
            if (Uri.TryCreate(a, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        throw new InvalidOperationException("no bound http address reported by the server");
    }
}
