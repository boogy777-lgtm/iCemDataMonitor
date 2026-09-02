using Grpc.Core;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Data.Telemetry;
using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Host.Core.Health;
using iCemDataMonitor.Sdk.Bus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HealthZone = iCemDataMonitor.Host.Core.Health.ZoneHealth;

namespace iCemDataMonitor.Host.Grpc;

/// <summary>
/// D.2 server-side implementation of the frozen TelemetryService contract.
///
/// CHANNEL LAW (D.0): this service NEVER inspects the calling channel — no
/// transport, no remote address, no port. Authorization = closed-contour
/// isolation (E.1, owner decree 2026-08-24: every call is anonymous);
/// wire shape lives in telemetry.proto.
/// LIFECYCLE LAW (E.2.7, owner decree 2026-08-24): there is NO shutdown RPC —
/// the host closes ONLY from its own tray ([Закрыть], zone core). This service
/// holds no IHostApplicationLifetime and cannot stop the process.
/// This class is a pure adapter: proto messages ↔ host services
/// (StationHealthState / LayerRuntimeController / AutostartManager / MetricsSampler).
/// 2026-08-27: engine supervision REMOVED (the emulator is a separate
/// independent app) — no EmuleState, no ControlEngine. SetAutostart ADDED:
/// HKCU Run-key truth/toggle, response = fresh authoritative HostStatus.
/// 2026-08-31 (owner decree «даже смена темы — тоже CLI»): GetUiPrefs +
/// SetUiTheme ADDED — the station-wide ui-theme bool (UiPrefsStore): the host
/// writes the file + pokes the shell-owned event; the response is the fresh
/// authoritative UiPrefs (sensor-at-send, idempotent command).
/// </summary>
public sealed class TelemetryServiceImpl : TelemetryService.TelemetryServiceBase
{
    private readonly StationHealthState _health;
    private readonly BusReader _reader;
    private readonly MetricsSampler _sampler;
    private readonly Tray.AutostartManager _autostart;
    private readonly Tray.ConsoleVisibilityService _console;
    private readonly Tray.UiPrefsStore _uiPrefs;
    private readonly IServiceProvider _services;
    private readonly ILogger<TelemetryServiceImpl> _logger;

    public TelemetryServiceImpl(
        StationHealthState health,
        BusReader reader,
        MetricsSampler sampler,
        Tray.AutostartManager autostart,
        Tray.ConsoleVisibilityService console,
        Tray.UiPrefsStore uiPrefs,
        IServiceProvider services,
        ILogger<TelemetryServiceImpl> logger)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _autostart = autostart ?? throw new ArgumentNullException(nameof(autostart));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _uiPrefs = uiPrefs ?? throw new ArgumentNullException(nameof(uiPrefs));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── GetStatus ───────────────────────────────────────────────────────────

    public override Task<HostStatus> GetStatus(HostStatusRequest request, ServerCallContext context) =>
        Task.FromResult(BuildStatus());

    /// <summary>Single composition point for the status snapshot — GetStatus,
    /// SetConsoleVisible AND SetAutostart (one round trip gives the checkbox
    /// its fresh authoritative value) all project through here.</summary>
    private HostStatus BuildStatus()
    {
        var s = _health.Current;
        var controller = _services.GetService<LayerRuntimeController>();

        var status = new HostStatus
        {
            BusOpen = s.BusOpen,
            AddinAlive = s.AddInAlive,
            Heartbeat = s.Heartbeat,
            ComPort = s.ComPort,
            CaseState = ResolveCaseState(s),
            // 2026-08-28 dishonest-surface fix: case_lan_state = header reg 2
            // (tap-exposed LAN/cRIO state) — was s.LanStatus = header reg 8,
            // which is a com_stream float-pair low word in v5 (garbage).
            CaseLanState = s.CaseLanState,
            Layers = BuildLayerState(controller),
            UpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                DateTime.SpecifyKind(s.UpdatedAtUtc, DateTimeKind.Utc)),
            // W21.1: console visibility is the HOST's truth (the WinUI Status-tab
            // checkbox mirrors this and toggles it via SetConsoleVisible).
            ConsoleVisible = Tray.ConsoleWindow.Visible,
            // 2026-08-27: autostart = HKCU Run-key truth AT CALL TIME (the
            // AutostartManager reads the registry sensor-at-send — no cache).
            AutostartEnabled = _autostart.IsEnabled(),
        };

        status.Zones.AddRange(new[]
        {
            Zone(BusZoneKind.BusZoneCom, s.Com),
            Zone(BusZoneKind.BusZoneLan, s.Lan),
            Zone(BusZoneKind.BusZoneRaw, s.Raw),
        });

        return status;
    }

    /// <summary>Reg1 is not part of StationHealth — read it sensor-at-send from
    /// the bus snapshot; 0 when no live snapshot (honest zero, ¬fake).</summary>
    private short ResolveCaseState(Core.Health.StationHealth s)
    {
        var snap = _reader.Snapshot();
        return snap?.CaseState ?? 0;
    }

    private static Contracts.ZoneHealth Zone(BusZoneKind kind, HealthZone z) => new()
    {
        Kind = kind,
        Active = z.Active,   // truth: activeFlag && age < 5 s (computed by BusHealthMonitor)
        AgeMs = z.AgeMs,
    };

    private static LayerState BuildLayerState(LayerRuntimeController? c) => new()
    {
        Modbus = c?.ActualModbusRunning ?? false,
        ModbusBound = c?.ActualModbusRunning ?? false,
        ModbusPort = c?.Ini.ModbusPort ?? 0,
        // RESTORED 2026-08-27 (corrected decree): web truth = the persist-only
        // layer plan (LayerState.web=4 exists again in telemetry.proto).
        Web = c?.Layers.Web ?? false,
        Sniffer = c?.ActualLanSnifferRunning ?? false,
        SnifferAvailable = c?.SnifferAvailable ?? false,
        OpcDa = c?.Layers.OpcDa ?? false,
        SetAsDefault = c?.SetAsDefault ?? false,
        Busy = c?.Busy ?? false,
        LastError = c?.LastError ?? string.Empty,
    };

    // ── SubscribeMetrics ────────────────────────────────────────────────────

    public override async Task SubscribeMetrics(
        SubscribeRequest request,
        IServerStreamWriter<MetricsSample> responseStream,
        ServerCallContext context)
    {
        var subscription = _sampler.Subscribe(request.IntervalMs, context.CancellationToken);
        try
        {
            await foreach (var sample in subscription.WithCancellation(context.CancellationToken)
                                 .ConfigureAwait(false))
            {
                await responseStream.WriteAsync(sample).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            // Sampler fail-fast (bus closed / no snapshot): end the stream with an
            // explicit UNAVAILABLE + meaningful detail — never silent completion.
            _logger.LogWarning("[telemetry] metrics stream {Method} aborted: {Message}",
                context.Method, ex.Message);
            throw new RpcException(new Status(StatusCode.Unavailable,
                "metrics stream aborted: the bus is not open or stopped responding"), ex.Message);
        }
        // OperationCanceledException (client disconnect / shutdown) propagates as
        // cancellation — that IS the normal stream termination path.
    }

    // ── GetRegisters ────────────────────────────────────────────────────────

    /// <summary>
    /// Unary register snapshot (W20 F.1): bus reads AS RPC for thin clients
    /// (CLI forwarder). SENSOR-AT-SEND law: the snapshot is taken AT CALL
    /// TIME — no cache, no pre-computed state. Bus closed ⇒ UNAVAILABLE with a
    /// meaningful detail (¬молчание, ¬пустой ответ).
    ///
    /// Address semantics = the shared decode law (RegisterDecoder, the SAME
    /// helper MetricsSampler.ComputeDeltas uses): absolute 0..599; header words
    /// 0..3 as int32; pair 4..5 Unix timestamp int32 raw bits CDAB; all other
    /// even-aligned pairs float32 CDAB. One RegisterValue per PAIR at its base
    /// address — identical to the streaming projection.
    /// </summary>
    public override Task<RegistersReply> GetRegisters(RegistersRequest request, ServerCallContext context)
    {
        var snap = _reader.Snapshot();
        if (snap is null || !_reader.IsOpen)
        {
            _logger.LogWarning("[telemetry] GetRegisters rejected: bus closed ({Error})", _reader.LastError);
            throw new RpcException(new Status(StatusCode.Unavailable,
                "bus is not open or no snapshot is available " +
                $"(reader state: {_reader.LastError ?? "unknown"}). " +
                "Ensure the iCem AddIn has created the bus file."));
        }

        var (startAddr, endExclusive) = ResolveRange(request.Zone);

        var reply = new RegistersReply
        {
            TimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Heartbeat = snap.Heartbeat,
        };

        ReadOnlySpan<ushort> words = snap.Words.AsSpan();

        // Header state regs 0..3 belong to NO data zone (CONTRACT_BusV2 v5):
        // they are included ONLY in the UNSPECIFIED full sweep, as int32 words.
        int headerEnd = Math.Min(endExclusive, BusLayout.HeaderRegCount);
        for (int addr = startAddr; addr < headerEnd && addr < words.Length; addr++)
        {
            reply.Values.Add(new RegisterValue { Addr = (uint)addr, Int32Value = words[addr] });
        }

        // Data-zone CDAB pairs (pair-aligned bases 4/200/400 — v5 zone consts).
        // Bounds-guarded like ComputeDeltas: only pairs whose BOTH words exist
        // in the snapshot are emitted (smaller dictionaries degrade honestly).
        int pairStart = Math.Max(startAddr, BusLayout.HeaderRegCount);
        if ((pairStart & 1) != 0) pairStart++; // defensive alignment; v5 zones are already even-based
        for (int addr = pairStart; addr + 1 < endExclusive && addr + 1 < words.Length; addr += 2)
        {
            var (isInt32, intValue, floatValue) = RegisterDecoder.DecodePair(words, addr);
            reply.Values.Add(isInt32
                ? new RegisterValue { Addr = (uint)addr, Int32Value = intValue }
                : new RegisterValue { Addr = (uint)addr, FloatValue = floatValue });
        }

        return Task.FromResult(reply);
    }

    /// <summary>Zone → absolute address range from the ACTUAL BusLayout v5 consts
    /// (¬ magic numbers): COM=com_stream 4..199, LAN=lan_stream_crio 200..399,
    /// RAW=raw_com_line 400..599, UNSPECIFIED=all live words 0..599 (incl. header).</summary>
    private static (int Start, int EndExclusive) ResolveRange(BusZoneKind zone) => zone switch
    {
        BusZoneKind.BusZoneUnspecified => (0, RegisterDecoder.LiveEndExclusive),
        BusZoneKind.BusZoneCom => (BusLayout.ComStreamBaseReg, BusLayout.ComStreamBaseReg + BusLayout.ComStreamRegCount),
        BusZoneKind.BusZoneLan => (BusLayout.LanStreamCrioBaseReg, BusLayout.LanStreamCrioBaseReg + BusLayout.LanStreamCrioRegCount),
        BusZoneKind.BusZoneRaw => (BusLayout.RawComLineBaseReg, BusLayout.RawComLineBaseReg + BusLayout.RawComLineRegCount),
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument, $"unknown bus zone: {zone}")),
    };

    // ── SetLayer ────────────────────────────────────────────────────────────

    public override Task<LayerState> SetLayer(LayerControlRequest request, ServerCallContext context)
    {
        // Validate the request FIRST: InvalidArgument outranks availability.
        if (request.Layer == LayerKind.Unspecified)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "layer must be specified"));

        var controller = _services.GetService<LayerRuntimeController>()
            ?? throw new RpcException(new Status(StatusCode.Unavailable, "layer controller is not registered"));

        var kind = MapLayer(request.Layer); // 1:1 mapping, no semantic drift
        var result = Task.Run(() => controller.Toggle(kind), context.CancellationToken).GetAwaiter().GetResult();
        _logger.LogInformation("[telemetry] SetLayer {Layer} -> {Result}", request.Layer, result);

        return Task.FromResult(BuildLayerState(controller));
    }

    public static TrayToggleRules.LayerKind MapLayer(LayerKind layer) => layer switch
    {
        LayerKind.Modbus => TrayToggleRules.LayerKind.Modbus,
        LayerKind.LanSniffer => TrayToggleRules.LayerKind.LanSniffer,
        LayerKind.OpcDa => TrayToggleRules.LayerKind.OpcDa,
        // RESTORED 2026-08-27: LAYER_KIND_WEB=2 is live again in telemetry.proto
        // (persist-only toggle — the controller owns the «applies after restart»
        // semantics; no actuator exists for web).
        LayerKind.Web => TrayToggleRules.LayerKind.Web,
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument, $"unknown layer: {layer}")),
    };

    // TOMBSTONE (2026-08-27): ControlEngine(EngineControlRequest) returns
    // (EmuleState) was REMOVED — the emulator is now a SEPARATE INDEPENDENT
    // app with built-in UI + CLI; the host no longer spawns/controls it.
    // The override and BuildEngineState/RestartEngine helpers died with the
    // EngineSupervisor class (Host.Core). Numbers RESERVED in telemetry.proto,
    // never reuse.

    // ── SubscribeBootStages (W21.3) ─────────────────────────────────────────

    /// <summary>Server-streaming boot-stage mirror for the UI splashscreen:
    /// REPLAY of every stage recorded since PROCESS START (including pre-Kestrel
    /// ones — the tracker instance is created before any startup work), then
    /// live updates. Checkbox = BootStage.Completed. Cancellation (client
    /// disconnect / host shutdown) is the normal stream termination path.</summary>
    public override async Task SubscribeBootStages(
        BootStreamRequest request,
        IServerStreamWriter<BootStage> responseStream,
        ServerCallContext context)
    {
        var tracker = _services.GetService<Host.Boot.BootStageTracker>()
            ?? throw new RpcException(new Status(StatusCode.Unavailable,
                "boot stage tracker is not registered (test composition?)"));

        await foreach (var stage in tracker.Subscribe(context.CancellationToken)
                             .ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(stage).ConfigureAwait(false);
        }
    }

    // ── SetConsoleVisible (W21.3) ───────────────────────────────────────────

    /// <summary>Status-tab checkbox command: show/hide the HOST console. The
    /// response is the fresh HostStatus — one round trip re-arms the checkbox
    /// with the authoritative value. 2026-08-28 OWNER LAW: this RPC and the
    /// tray toggle are the SAME command — both delegate to the single
    /// ConsoleVisibilityService (policy table + evidence log), ¬ direct
    /// ConsoleWindow calls.</summary>
    public override Task<HostStatus> SetConsoleVisible(SetConsoleVisibleRequest request, ServerCallContext context)
    {
        _console.SetVisible(request.Visible);
        _logger.LogInformation("[telemetry] SetConsoleVisible({Visible})", request.Visible);
        return Task.FromResult(BuildStatus());
    }

    // ── SetAutostart (2026-08-27) ───────────────────────────────────────────

    /// <summary>Status-tab «Автозагрузка» checkbox command: write/remove the
    /// HKCU Run key value iCemDataMonitorHost (the SAME key as
    /// scripts\add-autostart.ps1 / tools\install-autostart.ps1). The response
    /// is the fresh HostStatus whose autostart_enabled mirrors the registry
    /// TRUTH at call time.</summary>
    public override Task<HostStatus> SetAutostart(SetAutostartRequest request, ServerCallContext context)
    {
        _autostart.SetEnabled(request.Enabled);
        _logger.LogInformation("[telemetry] SetAutostart({Enabled})", request.Enabled);
        return Task.FromResult(BuildStatus());
    }

    // ── NavigateUi (W21.3) ──────────────────────────────────────────────────

    /// <summary>Hierarchical CLI section commands land here (the tray [Статус]
    /// button was removed 2026-08-27). UNSPECIFIED is the QUERY arm (shell
    /// consumes its pending section on show) — see UiNavigationService for the
    /// closed-arm semantics.</summary>
    public override Task<NavigateUiResponse> NavigateUi(NavigateUiRequest request, ServerCallContext context)
    {
        var nav = _services.GetService<Tray.UiNavigationService>()
            ?? throw new RpcException(new Status(StatusCode.Unavailable,
                "ui navigation service is not registered (test composition?)"));
        return Task.FromResult(nav.Navigate(request.Section));
    }

    // ── GetUiPrefs / SetUiTheme (2026-08-31, owner decree «даже смена темы —
    //    тоже CLI: один bool для одновременного использования в web и winui») ─

    /// <summary>Unary UI-preferences snapshot. SENSOR-AT-SEND: the ui-theme
    /// file is read AT CALL TIME (UiPrefsStore.IsThemeLight) — missing file /
    /// foreign text ⇒ dark. Deliberately NOT a HostStatus field: prefs are
    /// read once at UI start, not polled at 1 Hz, and they are not station
    /// telemetry.</summary>
    public override Task<UiPrefs> GetUiPrefs(UiPrefsRequest request, ServerCallContext context) =>
        Task.FromResult(BuildUiPrefs());

    /// <summary>Idempotent theme command (SetConsoleVisible/SetAutostart law):
    /// the HOST writes the shared ui-theme file, pokes the shell-owned event
    /// (a live WinUI flips instantly via its StartThemeListener), and answers
    /// with the FRESH truth re-read from the file — never a projection of the
    /// request. A failed write is logged and still answers the current truth
    /// (¬ fake success).</summary>
    public override Task<UiPrefs> SetUiTheme(SetUiThemeRequest request, ServerCallContext context)
    {
        _uiPrefs.SetThemeLight(request.ThemeLight);
        _logger.LogInformation("[telemetry] SetUiTheme({ThemeLight})", request.ThemeLight);
        return Task.FromResult(BuildUiPrefs());
    }

    /// <summary>Single composition point for the prefs snapshot — GetUiPrefs
    /// AND SetUiTheme project through here (fresh truth, sensor-at-send).</summary>
    private UiPrefs BuildUiPrefs() => new() { ThemeLight = _uiPrefs.IsThemeLight() };

    // TOMBSTONE (E.2.7): the remote stop-RPC override lived here and called
    // IHostApplicationLifetime.StopApplication after a settle gap. REMOVED —
    // owner law: the host closes ONLY from its own tray; no RPC can stop it.
    // (The RPC name is banned verbatim in Host sources by the hygiene ratchet.)
}
