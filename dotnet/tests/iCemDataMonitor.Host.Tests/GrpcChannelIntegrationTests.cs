using System.Net;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Host;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace iCemDataMonitor.Host.Tests;

/// <summary>
/// Serializes all tests that start a REAL Kestrel instance: every such app
/// binds the named-pipe endpoint (HostChannelNames.PipeName), and two
/// concurrent listeners on one pipe name would collide.
/// </summary>
[CollectionDefinition(Name)]
public class HostKestrelSerialCollection
{
    public const string Name = "host-kestrel-serial";
}

/// <summary>
/// ONE real-Kestrel app per test CLASS: the named-pipe endpoint must be bound
/// exactly once for the whole class (per-test apps raced on pipe disposal).
/// web_on is pinned OFF here (2026-08-28: the ini DEFAULT is 1 — the pure
/// gRPC + /api/* composition is explicit; subclasses flip webOn:true).
/// </summary>
public class HostAppFixture : IDisposable, IAsyncDisposable
{
    private readonly string _dir;

    public WebApplication App { get; }
    public int Port { get; }

    public HostAppFixture() : this(webOn: false) { }

    protected HostAppFixture(bool webOn)
    {
        _dir = Path.Combine(Path.GetTempPath(), $"hostgrpc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        var iniPath = Path.Combine(_dir, "station.ini");
        // All layers off: composition + transport only, NO Modbus bind /
        // sniffer in tests. (2026-08-27: emule_on is a removed unknown key —
        // the default template no longer carries it.)
        var iniText = IniConfig.DefaultContent
            .Replace("modbus_on=1", "modbus_on=0")
            // 2026-08-28: web_on now DEFAULTS to 1 (decree) — the pure-gRPC
            // fixture must pin it OFF explicitly (webOn:true flips it back on).
            .Replace("web_on=1", "web_on=0")
            // 2026-09-01: lansniffer_on now DEFAULTS to 1 (decree) — the
            // transport-only fixture must pin it OFF explicitly.
            .Replace("lansniffer_on=1", "lansniffer_on=0");
        if (webOn)
        {
            iniText = iniText.Replace("web_on=0", "web_on=1");
            var wwwroot = Path.Combine(_dir, "wwwroot");
            Directory.CreateDirectory(wwwroot);
            File.WriteAllText(Path.Combine(wwwroot, "index.html"),
                "<html><head><title>iCem</title></head><body>iCem grpc-web fixture</body></html>");
        }
        File.WriteAllText(iniPath, iniText);
        _ = IniConfig.Parse(iniText); // fail fast if the template drifted

        Port = GetFreePort();
        App = HostAppFactory.Build(new HostBuildSpec(
            Args: [], IniPath: iniPath, PortOverride: Port, ContentRootOverride: _dir));
        App.StartAsync().GetAwaiter().GetResult();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public void Dispose()
    {
        try { App.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).GetAwaiter().GetResult(); }
        catch { /* best effort */ }
        App.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { await App.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); } catch { }
        await App.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// web_on=1 variant of the fixture: the SPA pipeline (gRPC-Web middleware +
/// static files + fallback) is wired on top of the SAME composition. Own app
/// instance — the serial collection keeps the named-pipe binding exclusive.
/// </summary>
public sealed class HostWebAppFixture : HostAppFixture
{
    public HostWebAppFixture() : base(webOn: true) { }
}

/// <summary>
/// D.2/E.1 integration over REAL Kestrel: EVERY call is ANONYMOUS (owner
/// decree 2026-08-24 — authorization = closed-contour isolation, the token
/// interceptor was removed). 2026-08-27 RESTORED (corrected decree): the TCP
/// gRPC-Web channel variants are BACK over the web_on=1 composition
/// (GrpcWebHandler = the browser-equivalent client stack against the server's
/// Grpc.AspNetCore.Web middleware). The covered channels: native gRPC over the
/// named pipe (HTTP/2 — the WinUI/CLI production path) and gRPC-Web over
/// loopback TCP (the browser production path).
/// </summary>
[Collection(HostKestrelSerialCollection.Name)]
public class GrpcChannelIntegrationTests : IClassFixture<HostAppFixture>
{
    private readonly HostAppFixture _fx;

    public GrpcChannelIntegrationTests(HostAppFixture fx) => _fx = fx;

    // ── named-pipe channel (native gRPC over HTTP/2) ────────────────────────

    [Fact]
    public async Task Pipe_NativeGrpc_GetStatus_Anonymous_Succeeds()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var pipe = new System.IO.Pipes.NamedPipeClientStream(
                        ".", HostChannelNames.PipeName,
                        System.IO.Pipes.PipeDirection.InOut,
                        System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.WriteThrough);
                    await pipe.ConnectAsync(5_000, ct);
                    return pipe;
                },
            },
        });
        var client = new TelemetryService.TelemetryServiceClient(channel);

        // NO metadata of any kind — every channel is anonymous since E.1.
        var status = await client.GetStatusAsync(new HostStatusRequest(),
            deadline: DateTime.UtcNow.AddSeconds(15)).ResponseAsync;
        Assert.NotNull(status.UpdatedAtUtc);
    }

    /// <summary>
    /// 2026-08-27: SetAutostart is testable over the REAL host surface too —
    /// but it must NEVER touch the real HKCU Run key in a test. The
    /// HostAppFixture host uses the DEFAULT AutostartManager (real Run key),
    /// so this test only exercises the read-only truth probe side:
    /// GetStatus must return a BOOLEAN autostart_enabled matching the real
    /// registry (whatever it is), never an exception. The write side is
    /// covered by AutostartManagerTests with throwaway keys.
    /// </summary>
    [Fact]
    public async Task Pipe_NativeGrpc_GetStatus_CarriesAutostartTruth()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var pipe = new System.IO.Pipes.NamedPipeClientStream(
                        ".", HostChannelNames.PipeName,
                        System.IO.Pipes.PipeDirection.InOut,
                        System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.WriteThrough);
                    await pipe.ConnectAsync(5_000, ct);
                    return pipe;
                },
            },
        });
        var client = new TelemetryService.TelemetryServiceClient(channel);

        var status = await client.GetStatusAsync(new HostStatusRequest(),
            deadline: DateTime.UtcNow.AddSeconds(15)).ResponseAsync;

        // The truth probe must agree with a DIRECT registry read (no cache).
        // READ-ONLY here: this test never writes the real Run key.
        var expected = DirectRunKeyProbe();
        Assert.Equal(expected, status.AutostartEnabled);
    }

    /// <summary>Read-only direct probe of the REAL HKCU Run key state
    /// (mirrors AutostartManager.IsEnabled semantics — missing key = false).
    /// Used only to cross-check the RPC truth; never writes.</summary>
    private static bool DirectRunKeyProbe()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            iCemDataMonitor.Host.Tray.AutostartManager.DefaultRunKeyPath);
        var data = key?.GetValue(iCemDataMonitor.Host.Tray.AutostartManager.DefaultValueName) as string;
        var exe = Environment.ProcessPath ?? "";
        return string.Equals(data, $"\"{exe}\" --autostart", StringComparison.Ordinal);
    }

    // ═══ 2026-08-31 (owner decree «даже смена темы — тоже CLI»): GetUiPrefs
    // over the REAL pipe — READ-ONLY truth check, same law as the autostart
    // truth test above: the RPC boolean must match a direct read of the REAL
    // ProgramData ui-theme file (whatever the live station currently holds);
    // the WRITE side is covered by TelemetryServiceImplTests with throwaway
    // §15.7(a) paths — a test must never write the owner's station bool.

    [Fact]
    public async Task Pipe_NativeGrpc_GetUiPrefs_CarriesStationThemeTruth()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var pipe = new System.IO.Pipes.NamedPipeClientStream(
                        ".", HostChannelNames.PipeName,
                        System.IO.Pipes.PipeDirection.InOut,
                        System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.WriteThrough);
                    await pipe.ConnectAsync(5_000, ct);
                    return pipe;
                },
            },
        });
        var client = new TelemetryService.TelemetryServiceClient(channel);

        var prefs = await client.GetUiPrefsAsync(new UiPrefsRequest(),
            deadline: DateTime.UtcNow.AddSeconds(15)).ResponseAsync;

        Assert.Equal(ThemeTruthProbes.DirectFile(), prefs.ThemeLight);
    }
}

/// <summary>2026-08-31 (theme decree): READ-ONLY truth probes shared by both
/// integration classes (pipe + gRPC-Web): a direct read of the REAL
/// ProgramData ui-theme file (mirrors UiPrefsStore.IsThemeLight semantics —
/// missing/unreadable = dark). Never writes; the write side lives in
/// TelemetryServiceImplTests with throwaway §15.7(a) paths.</summary>
internal static class ThemeTruthProbes
{
    internal static bool DirectFile()
    {
        try
        {
            return File.ReadAllText(iCemDataMonitor.Host.Tray.UiPrefsStore.DefaultFilePath).Trim() == "light";
        }
        catch
        {
            return false; // absent/unreadable ⇒ honest dark default
        }
    }
}

/// <summary>
/// RESTORED 2026-08-27 (corrected decree): the TCP gRPC-Web channel variants,
/// re-anchored on the web_on=1 composition. GrpcWebHandler speaks the
/// browser-side gRPC-Web protocol — the exact production path of the SPA
/// dashboard (main.js) against the server's Grpc.AspNetCore.Web middleware.
/// Anonymous law holds here too: no metadata of any kind.
/// </summary>
[Collection(HostKestrelSerialCollection.Name)]
public class GrpcWebChannelIntegrationTests : IClassFixture<HostWebAppFixture>
{
    private readonly HostWebAppFixture _fx;

    public GrpcWebChannelIntegrationTests(HostWebAppFixture fx) => _fx = fx;

    private GrpcChannel CreateWebChannel() =>
        GrpcChannel.ForAddress($"http://127.0.0.1:{_fx.Port}", new GrpcChannelOptions
        {
            // gRPC-Web over HTTP/1.1 — the exact wire shape of a browser fetch
            // without h2c (browsers never speak prior-knowledge HTTP/2 to a
            // plaintext origin). GrpcChannel would otherwise demand h2c, which
            // Kestrel's plaintext Http1AndHttp2 endpoint answers with GOAWAY
            // HTTP_1_1_REQUIRED; the handler pins the request to HTTP/1.1.
            HttpHandler = new Http11Handler(new GrpcWebHandler(GrpcWebMode.GrpcWeb, new SocketsHttpHandler())),
        });

    /// <summary>Pins every outgoing request to HTTP/1.1 exact (browser parity
    /// for plaintext gRPC-Web; GrpcChannelOptions has no version knob).</summary>
    private sealed class Http11Handler : DelegatingHandler
    {
        public Http11Handler(HttpMessageHandler inner) : base(inner) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = System.Net.HttpVersion.Version11;
            request.VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Raw browser-parity gRPC-Web unary call: HTTP/1.1 POST with
    /// application/grpc-web+proto framing (0x00 data frame + 0x80 trailer
    /// frame). No Grpc.Net.Client stack involved — the wire contract of the
    /// SPA dashboard's fetch() calls against the Grpc.AspNetCore.Web
    /// middleware, pinned end-to-end.</summary>
    [Fact]
    public async Task Tcp_GrpcWebRawHttp11_GetStatus_BrowserParity()
    {
        using var http = new HttpClient
        {
            DefaultRequestVersion = System.Net.HttpVersion.Version11,
            DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact,
        };
        using var content = new ByteArrayContent(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 }); // empty HostStatusRequest frame
        content.Headers.TryAddWithoutValidation("Content-Type", "application/grpc-web+proto");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var response = await http.PostAsync(
            $"http://127.0.0.1:{_fx.Port}/icemdatamonitor.v1.TelemetryService/GetStatus",
            content, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The middleware answers the binary variant with the FAMILY type
        // «application/grpc-web» (both +proto and base types carry identical
        // binary framing) — pin the family, not the exact suffix.
        Assert.StartsWith("application/grpc-web",
            response.Content.Headers.ContentType?.ToString() ?? "", StringComparison.Ordinal);

        var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        // Frame layout: [0x00][len BE32][HostStatus bytes][0x80][len BE32][trailers]
        Assert.True(body.Length > 6, "gRPC-Web response must carry a data frame + a trailer frame");
        Assert.Equal(0x00, body[0]);
        int msgLen = (body[1] << 24) | (body[2] << 16) | (body[3] << 8) | body[4];
        Assert.True(msgLen > 0);

        var status = HostStatus.Parser.ParseFrom(body, 5, msgLen);
        Assert.NotNull(status.UpdatedAtUtc);
        Assert.True(status.Layers.Web); // web_on=1 fixture — the plan is client-visible

        // Trailer frame: grpc-status: 0 (unary success, browser parity).
        int trailerAt = 5 + msgLen;
        Assert.Equal(0x80, body[trailerAt]);
        var trailers = System.Text.Encoding.ASCII.GetString(body, trailerAt + 5, body.Length - trailerAt - 5);
        Assert.Contains("grpc-status: 0", trailers);
    }

    [Fact]
    public async Task Tcp_GrpcWeb_GetStatus_Anonymous_Succeeds()
    {
        using var channel = CreateWebChannel();
        var client = new TelemetryService.TelemetryServiceClient(channel);

        // NO metadata — browser parity (a browser cannot attach arbitrary
        // auth headers either; authorization = the closed contour).
        var status = await client.GetStatusAsync(new HostStatusRequest(),
            deadline: DateTime.UtcNow.AddSeconds(15)).ResponseAsync;

        Assert.NotNull(status.UpdatedAtUtc);
        // The restored web truth rides the status: the fixture ini has
        // web_on=1, so the persist-only plan must be visible to clients.
        Assert.True(status.Layers.Web);
    }

    [Fact]
    public async Task Tcp_GrpcWeb_SetLayerWeb_PersistOnly_FlipsPlan()
    {
        using var channel = CreateWebChannel();
        var client = new TelemetryService.TelemetryServiceClient(channel);

        var before = await client.GetStatusAsync(new HostStatusRequest(),
            deadline: DateTime.UtcNow.AddSeconds(15)).ResponseAsync;

        var after = await client.SetLayerAsync(
            new LayerControlRequest { Layer = LayerKind.Web },
            deadline: DateTime.UtcNow.AddSeconds(15)).ResponseAsync;

        // Persist-only: the plan flips WITHOUT any actuator and the returned
        // authoritative LayerState carries the new web truth.
        Assert.Equal(!before.Layers.Web, after.Web);

        // STATE-INVARIANT for the shared class fixture: flip it back so the
        // other tests in this class see the fixture's web_on=1 plan (the
        // persist path also rewrites the fixture's own temp ini).
        var restored = await client.SetLayerAsync(
            new LayerControlRequest { Layer = LayerKind.Web },
            deadline: DateTime.UtcNow.AddSeconds(15)).ResponseAsync;
        Assert.Equal(before.Layers.Web, restored.Web);
    }

    /// <summary>2026-08-31 (theme decree): GetUiPrefs over RAW gRPC-Web
    /// framing — the browser-parity path the SPA's inline theme seam walks.
    /// READ-ONLY: the fixture's host uses the DEFAULT store (the real
    /// ProgramData ui-theme), so the assertion is the truth cross-check, and
    /// the empty-message subtlety is pinned: proto3 omits theme_light=false,
    /// so a zero-length data frame IS a valid SUCCESS (dark) — the SPA seam
    /// must read the trailer's grpc-status, never the message size.</summary>
    [Fact]
    public async Task Tcp_GrpcWebRawHttp11_GetUiPrefs_BrowserParity()
    {
        using var http = new HttpClient
        {
            DefaultRequestVersion = System.Net.HttpVersion.Version11,
            DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact,
        };
        using var content = new ByteArrayContent(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 }); // empty UiPrefsRequest frame
        content.Headers.TryAddWithoutValidation("Content-Type", "application/grpc-web+proto");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var response = await http.PostAsync(
            $"http://127.0.0.1:{_fx.Port}/icemdatamonitor.v1.TelemetryService/GetUiPrefs",
            content, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/grpc-web",
            response.Content.Headers.ContentType?.ToString() ?? "", StringComparison.Ordinal);

        var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        Assert.True(body.Length > 6, "gRPC-Web response must carry a data frame + a trailer frame");
        Assert.Equal(0x00, body[0]);
        int msgLen = (body[1] << 24) | (body[2] << 16) | (body[3] << 8) | body[4];
        Assert.True(msgLen >= 0); // 0 IS legal here: UiPrefs{theme_light=false} serializes EMPTY

        var prefs = UiPrefs.Parser.ParseFrom(body, 5, msgLen);
        Assert.Equal(ThemeTruthProbes.DirectFile(), prefs.ThemeLight);

        // Trailer frame: grpc-status: 0 (unary success, browser parity).
        int trailerAt = 5 + msgLen;
        Assert.Equal(0x80, body[trailerAt]);
        var trailers = System.Text.Encoding.ASCII.GetString(body, trailerAt + 5, body.Length - trailerAt - 5);
        Assert.Contains("grpc-status: 0", trailers);
    }
}
