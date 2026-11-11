using System.IO.Pipes;
using Grpc.Core;
using Grpc.Net.Client;
using iCemDataMonitor.Contracts;

namespace iCemDataMonitor.HostClient;

/// <summary>
/// D.5.1 gRPC host client over the LOCAL NAMED PIPE channel:
/// GrpcChannel via SocketsHttpHandler.ConnectCallback → NamedPipeClientStream
/// (".", HostChannelNames.PipeName, InOut, Asynchronous | WriteThrough).
///
/// - Token is NOT required on the pipe (ACL = authorization, D.2.3);
/// - KeepAlive ping is DISABLED — over a named pipe a ping is noise, not a
///   liveness signal; liveness comes from the metrics stream + watchdog;
/// - pipe-name literal lives ONLY in Contracts.HostChannelNames (D.6.2
///   grep-guard in Host.Tests) — this file uses the constant.
/// </summary>
public sealed class GrpcHostClient : IHostApi, IHostControl
{
    /// <summary>Local machine only — the WinUI client never goes over TCP.</summary>
    public const string PipeServerName = ".";

    private readonly GrpcChannel _channel;
    private readonly TelemetryService.TelemetryServiceClient _client;

    public GrpcHostClient() : this(CreatePipeChannel()) { }

    internal GrpcHostClient(GrpcChannel channel)
    {
        _channel = channel;
        _client = new TelemetryService.TelemetryServiceClient(channel);
    }

    public static GrpcChannel CreatePipeChannel()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(
                    serverName: PipeServerName,
                    pipeName: HostChannelNames.PipeName, // single source of truth (D.6.2)
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);
                await pipe.ConnectAsync(ct).ConfigureAwait(false);
                return pipe;
            },
            // Named-pipe requirement (D.5.1): keep-alive pings OFF (the default
            // is already disabled; pinned explicitly so a future edit does not
            // silently introduce pings over a dead pipe).
            KeepAlivePingDelay = Timeout.InfiniteTimeSpan,
        };

        var options = new GrpcChannelOptions
        {
            HttpHandler = handler,
        };
        // Placeholder address ONLY (the ConnectCallback replaces the transport
        // entirely) — must still be a VALID URI, hence localhost, never ".".
        return GrpcChannel.ForAddress("http://localhost", options);
    }

    // ── IHostApi ──────────────────────────────────────────────────────────────

    public async Task<HostStatus> GetStatusAsync(CancellationToken ct = default) =>
        await _client.GetStatusAsync(new HostStatusRequest(), cancellationToken: ct)
            .ConfigureAwait(false);

    public IMetricsStream SubscribeMetrics(int intervalMs)
    {
        var call = _client.SubscribeMetrics(new SubscribeRequest { IntervalMs = intervalMs });
        return new MetricsStream(call);
    }

    public async Task<LayerState> SetLayerAsync(LayerKind layer, bool desiredState, CancellationToken ct = default) =>
        await _client.SetLayerAsync(
                new LayerControlRequest { Layer = layer, DesiredState = desiredState },
                cancellationToken: ct)
            .ConfigureAwait(false);

    // TOMBSTONE (2026-08-27): ControlEngineAsync REMOVED — the emulator is a
    // separate independent app now; the host no longer spawns/controls it.

    public async Task<RegistersReply> GetRegistersAsync(BusZoneKind zone, CancellationToken ct = default) =>
        await _client.GetRegistersAsync(
                new RegistersRequest { Zone = zone },
                cancellationToken: ct)
            .ConfigureAwait(false);

    // ── W21.4 (UI wave) ──────────────────────────────────────────────────────

    public async Task<HostStatus> SetConsoleVisibleAsync(bool visible, CancellationToken ct = default) =>
        await _client.SetConsoleVisibleAsync(
                new SetConsoleVisibleRequest { Visible = visible },
                cancellationToken: ct)
            .ConfigureAwait(false);

    // 2026-08-27: autostart (HKCU Run-key) toggle — mirrors the console
    // checkbox pattern: the response is the fresh authoritative HostStatus.
    public async Task<HostStatus> SetAutostartAsync(bool enabled, CancellationToken ct = default) =>
        await _client.SetAutostartAsync(
                new SetAutostartRequest { Enabled = enabled },
                cancellationToken: ct)
            .ConfigureAwait(false);

    public async Task<NavigateUiResponse> NavigateUiAsync(UiSection section, CancellationToken ct = default) =>
        await _client.NavigateUiAsync(
                new NavigateUiRequest { Section = section },
                cancellationToken: ct)
            .ConfigureAwait(false);

    // 2026-08-31 (owner decree «даже смена темы — тоже CLI»): station-wide
    // theme bool — mirrors SetAutostartAsync's shape: idempotent command, the
    // response is the fresh authoritative UiPrefs (host re-read the file).
    public async Task<UiPrefs> GetUiPrefsAsync(CancellationToken ct = default) =>
        await _client.GetUiPrefsAsync(new UiPrefsRequest(), cancellationToken: ct)
            .ConfigureAwait(false);

    public async Task<UiPrefs> SetUiThemeAsync(bool themeLight, CancellationToken ct = default) =>
        await _client.SetUiThemeAsync(
                new SetUiThemeRequest { ThemeLight = themeLight },
                cancellationToken: ct)
            .ConfigureAwait(false);

    public IBootStageStream SubscribeBootStages() =>
        new BootStageStream(_client.SubscribeBootStages(new BootStreamRequest()));

    // TOMBSTONE (E.2.7): ShutdownHostAsync REMOVED — the host closes ONLY from
    // its own tray ([Закрыть]); no client can stop it over the wire.

    // ── IHostControl (same surface, explicit re-exposure for the VM seam) ────

    Task<LayerState> IHostControl.SetLayerAsync(LayerKind layer, bool desiredState, CancellationToken ct) =>
        SetLayerAsync(layer, desiredState, ct);

    public void Dispose() => _channel.Dispose();
}
