using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Host;
using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Host.Tray;

namespace iCemDataMonitor.Host.Tests;

// D.3' (4): binding decision table — pure unit on the plan, NO real AnyIp
// socket is opened by tests. 2026-08-27 (corrected decree): web_remote_on is
// BACK — the plan is loopback for 0 and ListenAnyIp for 1 (closed contour).
public class KestrelBindingTests
{
    [Theory]
    [InlineData(8090, false, "127.0.0.1")]
    [InlineData(8090, true, "0.0.0.0")]
    public void Plan_Table_WebRemoteOn_PicksAddress(int port, bool webRemoteOn, string expectedAddress)
    {
        var plan = KestrelBinding.Plan(port, webRemoteOn);
        Assert.Equal(IPAddress.Parse(expectedAddress), plan.Address);
        Assert.Equal(port, plan.Port);
        // Both arms keep Http1AndHttp2 (/api/* JSON + gRPC on one socket).
        Assert.Equal(
            Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2,
            plan.Protocols);
    }

    [Fact]
    public void Plan_Default_RemoteOff_IsLoopback()
    {
        // Fail-safe arm: web_remote_on absent/0 ⇒ loopback-only.
        var plan = KestrelBinding.Plan(KestrelBinding.DefaultPort, webRemoteOn: false);
        Assert.Equal(IPAddress.Loopback, plan.Address);
        Assert.Equal(8090, plan.Port);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    [InlineData(-1, true)]
    [InlineData(65536, true)]
    public void Plan_RejectsOutOfRangePort(int port, bool webRemoteOn)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KestrelBinding.Plan(port, webRemoteOn));
    }
}

public class ReadyMarkerTests
{
    [Fact]
    public void Marker_Format_Matches_A24_Spec()
    {
        Assert.Equal("READY modbus=bound web=:8090 bus=open",
            StartupLog.ReadyMarker(modbusConfigured: true, modbusBound: true, webPort: 8090, busOpen: true));
        Assert.Equal("READY modbus=off web=:8090 bus=closed",
            StartupLog.ReadyMarker(modbusConfigured: false, modbusBound: false, webPort: 8090, busOpen: false));
        Assert.Equal("READY modbus=failed web=:8091 bus=open",
            StartupLog.ReadyMarker(modbusConfigured: true, modbusBound: false, webPort: 8091, busOpen: true));
    }
}

// D.3' (composition smoke): full host pipeline on a FREE port —
// GET /api/status must return 200.
// D.2: serialized with other real-Kestrel tests — every app instance binds the
// named-pipe endpoint, and concurrent pipe listeners would collide.
[Collection(HostKestrelSerialCollection.Name)]
public class CompositionSmokeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _iniPath;

    public CompositionSmokeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"hostsmoke_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _iniPath = Path.Combine(_dir, "station.ini");
        // All layers off: smoke exercises composition only (no Modbus bind,
        // no sniffer, no web pipeline). Strict-parseable per IniConfig schema.
        // 2026-08-28: web_on now DEFAULTS to 1 (decree) — the off-fixture pins
        // it OFF explicitly (web off has its own contract test below).
        var iniText = IniConfig.DefaultContent
            .Replace("modbus_on=1", "modbus_on=0")
            .Replace("web_on=1", "web_on=0")
            // 2026-09-01: lansniffer_on now DEFAULTS to 1 (decree) — the
            // all-off smoke fixture must pin it OFF explicitly.
            .Replace("lansniffer_on=1", "lansniffer_on=0");
        File.WriteAllText(_iniPath, iniText);
        // Fail fast with a clear message if the template drifted:
        _ = IniConfig.Parse(iniText);
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task Build_SetsShortShutdownTimeout_ForFastRestartDrain()
    {
        // 2026-08-31 (owner decree «максимально простую и надежную»): the
        // framework default ShutdownTimeout (30 s) OUTLIVED the restart
        // successor's 15 s mutex grace — live autopsy: successors died on the
        // single-instance guard while the predecessor drained, the stand was
        // left with NO host. The drain budget is pinned to seconds; clients
        // reconnect by design, force-closing streams loses nothing.
        var app = HostAppFactory.Build(new HostBuildSpec(
            Args: [], IniPath: _iniPath, PortOverride: GetFreePort(), ContentRootOverride: _dir));
        await using (app)
        {
            var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>();
            Assert.Equal(HostAppFactory.ShutdownDrainBudget, options.Value.ShutdownTimeout);
            Assert.True(options.Value.ShutdownTimeout < RestartPlan.MutexGraceWindow,
                "drain budget must stay BELOW the successor's mutex grace window — " +
                "otherwise the successor dies on the single-instance guard during the drain");
        }
    }

    [Fact]
    public async Task Host_Starts_OnFreePort_ApiStatus_Returns200()
    {
        var port = GetFreePort();
        var app = HostAppFactory.Build(new HostBuildSpec(
            Args: [],
            IniPath: _iniPath,
            PortOverride: port,
            ContentRootOverride: _dir));

        try
        {
            await app.StartAsync();
            var bound = HostAppFactory.GetBoundPort(app);
            Assert.Equal(port, bound);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var status = await client.GetAsync("/api/status", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);

            var summary = await client.GetAsync("/api/status/summary", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        }
        finally
        {
            await app.StopAsync(TimeSpan.FromSeconds(5));
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Build_Binds_LoopbackOnly_WhenWebRemoteOff()
    {
        var port = GetFreePort();
        var app = HostAppFactory.Build(new HostBuildSpec(
            Args: [], IniPath: _iniPath, PortOverride: port, ContentRootOverride: _dir));
        try
        {
            await app.StartAsync();
            var feature = ((IApplicationBuilder)app).ServerFeatures.Get<IServerAddressesFeature>()!;
            // web_remote_on=0 in this fixture ini ⇒ loopback-only renders as
            // http://127.0.0.1:port — NEVER 0.0.0.0/* (the AnyIp arm is gated
            // by web_remote_on=1, pinned in KestrelBindingTests.Plan_Table).
            Assert.Contains(feature.Addresses, a => a.Contains($":{port}") && a.Contains("127.0.0.1"));
            Assert.DoesNotContain(feature.Addresses, a => a.Contains("0.0.0.0") || a.Contains("[::]"));
        }
        finally
        {
            await app.StopAsync(TimeSpan.FromSeconds(5));
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Host_WebOn_ServesStatic_Index_HistoryValidation()
    {
        // RESTORED 2026-08-27 smoke: web_on=1 wires the SPA pipeline + the ring.
        // Minimal wwwroot in the temp content root (ContentRootOverride), so
        // the static surface is fully under test control.
        var wwwroot = Path.Combine(_dir, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"),
            "<html><head><title>iCem</title></head><body>iCem SPA fixture</body></html>");

        var iniText = IniConfig.DefaultContent
            .Replace("modbus_on=1", "modbus_on=0")
            .Replace("web_on=0", "web_on=1");
        var webIniPath = Path.Combine(_dir, "station_web.ini");
        File.WriteAllText(webIniPath, iniText);
        _ = IniConfig.Parse(iniText); // fail fast if the template drifted

        var port = GetFreePort();
        var app = HostAppFactory.Build(new HostBuildSpec(
            Args: [], IniPath: webIniPath, PortOverride: port, ContentRootOverride: _dir));
        try
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // 1. Static SPA: / serves index.html (UseDefaultFiles + UseStaticFiles).
            var index = await client.GetAsync("/", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            var html = await index.Content.ReadAsStringAsync(timeout.Token);
            Assert.Contains("<html", html);

            // 2. History validation: no addr ⇒ 400 (Э2 line 47).
            var noAddr = await client.GetAsync("/api/regs/history", timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, noAddr.StatusCode);

            // 3. Valid addr (4, even, in 4..599) ⇒ 200; the bus file does not
            //    exist in this fixture ⇒ the ring never fills ⇒ an EMPTY array
            //    of points is the honest answer (never fake data).
            var withAddr = await client.GetAsync("/api/regs/history?addr=4", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, withAddr.StatusCode);
            var body = await withAddr.Content.ReadAsStringAsync(timeout.Token);
            Assert.Contains("\"4\"", body);
        }
        finally
        {
            await app.StopAsync(TimeSpan.FromSeconds(5));
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Host_WebOff_History_Answers503_AndNoStatic()
    {
        // web_on=0 (fixture default): NOTHING of the trio is registered — the
        // history endpoint must answer 503 «history disabled» (Э2 contract),
        // and / must NOT fall back to any SPA file (no static pipeline).
        var port = GetFreePort();
        var app = HostAppFactory.Build(new HostBuildSpec(
            Args: [], IniPath: _iniPath, PortOverride: port, ContentRootOverride: _dir));
        try
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var history = await client.GetAsync("/api/regs/history?addr=4", timeout.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, history.StatusCode);

            var root = await client.GetAsync("/", timeout.Token);
            Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        }
        finally
        {
            await app.StopAsync(TimeSpan.FromSeconds(5));
            await app.DisposeAsync();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
