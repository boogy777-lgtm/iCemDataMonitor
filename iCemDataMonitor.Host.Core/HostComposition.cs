using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Data;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Host.Core.Endpoints;
using iCemDataMonitor.Host.Core.Health;
using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Bus;
using iCemDataMonitor.Transport;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Host.Core;

/// <summary>
/// D.3': shared service composition between the WinUI shell and the headless
/// console host (user process, W18: Windows Service REJECTED). The shell
/// delegates here to avoid a mirrored copy of the registration wiring.
/// </summary>
public static class HostComposition
{
    /// <summary>Load the unified variable registry from the deployment layout.</summary>
    public static UnifiedVariableRegistry? LoadRegistry(Action<string> log)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "templates", "variable_registry.json");
        if (!File.Exists(path))
        {
            log($"registry v2 NOT FOUND: {path} — registry-dependent layers degrade");
            return null;
        }
        try
        {
            var registry = UnifiedVariableRegistry.Load(path);
            log($"registry v2 loaded: {path}");
            return registry;
        }
        catch (Exception ex)
        {
            log($"registry v2 load FAILED: {ex.Message} — registry-dependent layers degrade");
            return null;
        }
    }

    /// <summary>
    /// Register the shared Core/Transport/Data services. The layer controller
    /// and actuator are NOT registered here because the actuator depends on
    /// services that may differ between hosts (and to keep UI/shell toggle
    /// wiring explicit).
    /// </summary>
    public static void RegisterCoreServices(
        IServiceCollection services,
        IniOptions ini,
        LayerFlags layers,
        UnifiedVariableRegistry? registry,
        Action<string> log)
    {
        services.AddSingleton(ini);
        services.AddSingleton(_ => new BusReader(ini.MmfPath, ini.MmfName, mutexName: null));
        services.AddSingleton(sp => new BusAcquisitionService(sp.GetRequiredService<BusReader>(),
            TimeSpan.FromMilliseconds(Math.Min(ini.ModbusTickMs, iCemDataMonitor.Data.Telemetry.MetricsSampler.MinIntervalMs))));
        services.AddSingleton<ILatestBusState>(sp => sp.GetRequiredService<BusAcquisitionService>());
        services.AddHostedService(sp => sp.GetRequiredService<BusAcquisitionService>());
        services.AddSingleton<StationHealthState>();
        services.AddHostedService<BusHealthMonitor>();

        if (registry is not null)
            services.AddSingleton(registry);

        services.AddSingleton(new ModbusOptions
        {
            Port = ini.ModbusPort,
            Total = ini.Total,
            TickMs = ini.ModbusTickMs,
        });
        services.AddSingleton<IModbusPublisher>(sp =>
            new ModbusTcpPublisher(
                sp.GetRequiredService<ILatestBusState>(),
                sp.GetRequiredService<ModbusOptions>()));
        log("modbus publisher registered (runtime-toggleable)");

        if (registry is not null)
        {
            // PRODUCER-OWNED BUS (INV-39, 2026-08-27): the host is a pure
            // READER — the BusZoneWriter/LanBusSink redirect is REMOVED. The
            // ACE sniffer stays as a wire reader (runtime-toggleable); its
            // decoded frames no longer reach the bus (the AddIn is the only
            // Data.dat writer).
            services.AddSingleton(new AceSnifferOptions { Enabled = true });
            services.AddSingleton<AceSnifferService>();
            log("lansniffer registered (runtime-toggleable; wire reader only — no bus redirect)");
        }
        else
        {
            log("registry v2 missing — LAN sniffer NOT registered (no variable catalog)");
        }
        // 2026-08-27 (corrected decree «Web - удали (контрол sim)»): the web
        // branch is BACK — the ring-history trio is registered ONLY when
        // web_on=1 (Э4 INV-32: the sampler runs when web_on=1). With web off
        // NOTHING is registered and GET /api/regs/history answers 503
        // «history disabled» (Э2 web_endpoint_regs_history contract).
        if (layers.Web)
        {
            services.AddSingleton<RingHistory>();
            services.AddSingleton(sp => new BusAddrRingService(sp.GetRequiredService<RingHistory>()));
            services.AddHostedService(sp => new BusRingService(
                sp.GetRequiredService<ILatestBusState>(),
                sp.GetRequiredService<BusAddrRingService>()));
            log("web layer registered (ring history 600 s @ 1 Hz + SPA dashboard on :web_port)");
        }
        else
        {
            log("web_on=0 — web layer NOT registered (history endpoint answers 503, no static files)");
        }
    }

    /// <summary>Register the shared layer toggle controller. The actuator is
    /// resolved from DI at first resolution (it depends on services registered
    /// by <see cref="RegisterCoreServices"/>).</summary>
    public static void RegisterLayerController(
        IServiceCollection services,
        IniOptions ini,
        LayerFlags layers,
        string iniPath)
    {
        services.AddSingleton<LayerRuntimeController.ILayerActuator>(sp =>
            new DefaultLayerActuator(
                sp.GetService<IModbusPublisher>(),
                sp.GetService<AceSnifferService>()));
        services.AddSingleton(sp => new LayerRuntimeController(
            ini,
            TrayToggleRules.Layers.From(layers),
            sp.GetService<AceSnifferService>() is not null,
            iniPath,
            sp.GetRequiredService<LayerRuntimeController.ILayerActuator>())
        {
            ModbusBoundProbe = () => sp.GetService<IModbusPublisher>()?.IsBound ?? false,
            SnifferRunningProbe = () => sp.GetService<AceSnifferService>()?.IsRunning ?? false,
        });
    }

    /// <summary>
    /// Create a WebApplicationBuilder with ContentRoot pinned to the exe dir
    /// and a single Kestrel endpoint per the D.3.4 bind plan. 2026-08-27
    /// (corrected decree): web_remote_on is BACK — the plan is loopback-only
    /// for web_remote_on=0 and ListenAnyIp for web_remote_on=1 (closed
    /// security contour).
    /// </summary>
    public static WebApplicationBuilder CreateWebApplicationBuilder(
        string[] args,
        KestrelBindPlan bind,
        string? contentRootOverride = null)
    {
        var contentRoot = contentRootOverride ?? AppContext.BaseDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => KestrelBinding.Apply(options, bind));
        return builder;
    }

    /// <summary>
    /// Map the frozen read-only ops surface required by the headless host:
    /// /api/status, /api/status/summary, /api/regs, /api/regs/history.
    /// (2026-08-27 corrected decree: the web/SPA layer is restored when
    /// web_on=1 — static wwwroot + gRPC-Web ride the host pipeline wired in
    /// HostAppFactory; /api/regs/history answers 503 when the ring is not
    /// registered. /api/engine stays dead with engine supervision.) The WinUI
    /// shell maps the same surface; call MapRegisterMirror() directly if you
    /// only need register mirroring.
    /// </summary>
    public static IEndpointRouteBuilder MapFrozenOpsSurface(
        this IEndpointRouteBuilder app,
        IniOptions ini,
        LayerFlags layers)
    {
        app.MapGet("/api/status", (StationHealthState h) =>
        {
            var s = h.Current;
            return Results.Ok(new
            {
                busOpen = s.BusOpen,
                addInAlive = s.AddInAlive,
                heartbeat = s.Heartbeat,
                comPort = s.ComPort,
                // 2026-08-28 dishonest-surface fix: the VALUE is header reg 2
                // (caseLanState, tap-exposed) — the legacy JSON key stays for
                // read-surface compat, but it previously carried reg 8 (a
                // com_stream float-pair low word = garbage as LAN state).
                lanStatus = s.CaseLanState,
                zones = new
                {
                    com = new { active = s.Com.Active, ageMs = s.Com.AgeMs },
                    lan = new { active = s.Lan.Active, ageMs = s.Lan.AgeMs },
                    raw = new { active = s.Raw.Active, ageMs = s.Raw.AgeMs },
                },
                updatedAtUtc = s.UpdatedAtUtc,
            });
        });

        app.MapGet("/api/status/summary", (StationHealthState h, AceSnifferService? sniffer, LayerRuntimeController? controller) =>
        {
            var s = h.Current;
            var runtime = controller?.CaptureStatus();
            return Results.Ok(new
            {
                busOpen = s.BusOpen,
                addInAlive = s.AddInAlive,
                heartbeat = s.Heartbeat,
                zones = new
                {
                    com = new { active = s.Com.Active, ageMs = s.Com.AgeMs },
                    lan = new { active = s.Lan.Active, ageMs = s.Lan.AgeMs },
                    raw = new { active = s.Raw.Active, ageMs = s.Raw.AgeMs },
                },
                layers = GetLayerStatusShape(runtime, ini, layers),
                // Additive witness surface (2026-08-30): sniffer frame counters ride
                // the same status path that surfaces IsRunning. The `sniffer` bool
                // above stays for read-surface compat.
                snifferStats = sniffer is null
                    ? null
                    : GetSnifferStatsShape(sniffer.GetStatus()),
                updatedAtUtc = s.UpdatedAtUtc,
            });
        });

        app.MapRegisterMirror();

        return app;
    }

    internal static object GetLayerStatusShape(LayerRuntimeStatus? status, IniOptions ini, LayerFlags layers) => new
    {
        modbus = status?.ModbusBound ?? false,
        modbusPort = status?.Ini.ModbusPort ?? ini.ModbusPort,
        sniffer = status?.SnifferRunning ?? false,
        opcDa = status?.OpcDa ?? layers.OpcDa,
        setAsDefault = status?.SetAsDefault ?? (ini.UiSetAsDefault == 1),
    };

    /// <summary>Anonymous shape of the additive /api/status/summary "snifferStats"
    /// witness block (2026-08-30). Kept as a named helper so the shape is pinned
    /// in one place and guard-testable.</summary>
    internal static object GetSnifferStatsShape(AceSnifferStatus status) => new
    {
        up = status.Up,
        state = status.State,
        totalFrames = status.TotalFrames,
        aceFrames = status.AceFrames,
        framesPerSec = status.FramesPerSec,
        lastFrameAgeSec = status.LastFrameAgeSec,
        distinctMacs = status.DistinctMacs,
    };

    /// <summary>
    /// Wire and start runtime-toggleable layers after the host has started:
    /// ACE LAN sniffer and Modbus TCP. Matches ShellHost startup sequencing.
    /// </summary>
    public static async Task StartLayersAsync(
        IServiceProvider services,
        LayerFlags layers,
        Action<string> log,
        CancellationToken ct = default)
    {
        var sniffer = services.GetService<AceSnifferService>();
        if (sniffer is not null)
        {
            await sniffer.StartAsync(ct);
            log("ACE LAN sniffer started (runtime-toggleable; wire reader only)");
        }

        var publisher = services.GetService<IModbusPublisher>();
        if (layers.Modbus && publisher is not null)
        {
            var ini = services.GetRequiredService<IniOptions>();
            await StartModbusWithPortRetryAsync(publisher, ini.ModbusPort, log, ct).ConfigureAwait(false);
        }
    }

    internal static async Task StartModbusWithPortRetryAsync(
        IModbusPublisher publisher, int port, Action<string> log, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        const int maxAttempts = 5;
        var retryDelay = TimeSpan.FromSeconds(5);
        delay ??= Task.Delay;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await publisher.StartAsync(ct).ConfigureAwait(false);
                log($"Modbus bound :{port} on attempt {attempt}/{maxAttempts}");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ModbusBindException ex) when (
                ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse && attempt < maxAttempts)
            {
                log($"Modbus port :{ex.Port} busy (attempt {attempt}/{maxAttempts}) — retrying in {retryDelay.TotalSeconds}s: {ex.Message}");
                await delay(retryDelay, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log($"Modbus StartAsync FAILED after {attempt} attempt(s): {ex}");
                throw new InvalidOperationException(
                    $"Failed to bind port {port} after {attempt} attempt(s). Last error: {ex.Message}", ex);
            }
        }
    }
}
