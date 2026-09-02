using iCemDataMonitor.Contracts;

namespace iCemDataMonitor.HostClient;

/// <summary>
/// Abstraction over the host TelemetryService surface (D.5.1). Test seam per
/// §15.7 (a): unit tests substitute a fake API + fake metrics stream instead
/// of a real named-pipe channel.
/// </summary>
public interface IHostApi : IDisposable
{
    Task<HostStatus> GetStatusAsync(CancellationToken ct = default);
    IMetricsStream SubscribeMetrics(int intervalMs);
    Task<LayerState> SetLayerAsync(LayerKind layer, bool desiredState, CancellationToken ct = default);

    // TOMBSTONE (2026-08-27): ControlEngineAsync REMOVED — the emulator is a
    // separate independent app; the host no longer spawns/controls the engine.

    /// <summary>
    /// Unary register snapshot (W20 F.1): bus reads AS RPC for thin clients.
    /// <paramref name="zone"/> = Unspecified → all live words 0..599; a concrete
    /// zone → only that zone's range (header state regs belong to no data zone).
    /// </summary>
    Task<RegistersReply> GetRegistersAsync(BusZoneKind zone, CancellationToken ct = default);

    // ── W21.4 (UI wave): boot mirror, console checkbox, pending navigation ──

    /// <summary>Show/hide the HOST console (Status-tab checkbox). The response
    /// is the fresh HostStatus — one round trip re-arms the checkbox.</summary>
    Task<HostStatus> SetConsoleVisibleAsync(bool visible, CancellationToken ct = default);

    /// <summary>Autostart (HKCU Run-key) toggle — the Status-tab «Автозагрузка»
    /// checkbox (2026-08-27). Same shape as SetConsoleVisibleAsync: the response
    /// is the fresh authoritative HostStatus (autostart_enabled mirrors the
    /// HKCU Run key iCemDataMonitorHost).</summary>
    Task<HostStatus> SetAutostartAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Pending-navigation coordinator. UNSPECIFIED = query arm:
    /// read-and-clear the section the host wants the shell to open (tray
    /// [Статус] / CLI stored it while the shell was away).</summary>
    Task<NavigateUiResponse> NavigateUiAsync(UiSection section, CancellationToken ct = default);

    // ── 2026-08-31 (owner decree «даже смена темы — тоже CLI»): station-wide
    //    theme bool — the machine-layer prefs surface ─────────────────────────

    /// <summary>Unary UI-preferences snapshot (the station-wide theme bool).
    /// Missing/foreign ui-theme file ⇒ dark. The shell uses this only for
    /// diagnostics today — the instant-flip channel is the file+event
    /// listener; the SPA applies the truth at page load.</summary>
    Task<UiPrefs> GetUiPrefsAsync(CancellationToken ct = default);

    /// <summary>Idempotent theme command: the HOST writes the shared
    /// ui-theme file + pokes the shell-owned event (a live WinUI flips
    /// instantly), then answers the fresh authoritative UiPrefs — same
    /// fresh-response shape law as SetAutostartAsync.</summary>
    Task<UiPrefs> SetUiThemeAsync(bool themeLight, CancellationToken ct = default);

    /// <summary>Server-streaming boot-stage mirror for the splashscreen:
    /// replay of all stages recorded since process start, then live updates.</summary>
    IBootStageStream SubscribeBootStages();

    // TOMBSTONE (E.2.7): ShutdownHostAsync REMOVED — owner law: the host
    // closes ONLY from its own tray; no client can stop it over the wire.
}

/// <summary>
/// One live SubscribeBootStages server-stream (W21.4). Same event shape as
/// <see cref="IMetricsStream"/>: the transport layer must not depend on Rx.
/// </summary>
public interface IBootStageStream : IDisposable
{
    /// <summary>Fired for every BootStage pushed by the host (replay + live).</summary>
    event Action<BootStage>? Stage;

    /// <summary>Fired when the stream ends for ANY reason.</summary>
    event Action<Exception>? Failed;
}

/// <summary>
/// One live SubscribeMetrics server-stream. Events (not IObservable) on
/// purpose: the transport layer must not depend on Rx; HostLink adapts.
/// </summary>
public interface IMetricsStream : IDisposable
{
    /// <summary>Fired for every MetricsSample pushed by the host sampler.</summary>
    event Action<MetricsSample>? Sample;

    /// <summary>Fired when the stream ends for ANY reason (error OR clean
    /// completion — a completed stream IS a lost link for the FSM).</summary>
    event Action<Exception>? Failed;
}

/// <summary>Control-plane subset used by the status view-model commands.</summary>
public interface IHostControl
{
    Task<LayerState> SetLayerAsync(LayerKind layer, bool desiredState, CancellationToken ct = default);

    // TOMBSTONE (2026-08-27): ControlEngineAsync REMOVED — emulator is a
    // separate independent app; the shell has no engine control channel.
}
