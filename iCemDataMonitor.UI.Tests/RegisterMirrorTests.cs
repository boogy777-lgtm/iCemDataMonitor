using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using iCemDataMonitor.Host.Core.Endpoints;
using Xunit.Abstractions;

namespace iCemDataMonitor.UI.Tests;

/// <summary>
/// Tests for the W6 register-file mirror endpoint:
/// GET /api/regs — full mirror 0..599, zones, names, CDAB decode.
/// GET /api/regs/history — RESTORED 2026-08-27 (corrected decree): INV-31
/// addr-keyed ring samples {ts,v}; validation odd/out-of-range/missing ⇒ 400;
/// ring absent (web_on=0) ⇒ 503. Synthetic bus + direct ring ingestion —
/// no real web pipeline needed (TestServer over MapRegisterMirror).
/// </summary>
public class RegisterMirrorTests : IAsyncLifetime
{
    private readonly string _dir;
    private readonly string _path;
    private readonly string _mapName;
    private readonly string _mutexName;

    private readonly List<HttpClient> _clients = new();
    private readonly List<WebApplication> _apps = new();
    private readonly List<BusReader> _readers = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly ITestOutputHelper _output;
    // Match the existing HttpClient default request budget; no timeout increase.
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(100);

    public RegisterMirrorTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "regs_mirror_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "test_bus.dat");
        _mapName = $"RegMirrorTest_{Guid.NewGuid():N}";
        _mutexName = $"Global\\RegMirrorMutex_{Guid.NewGuid():N}";
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            foreach (var client in _clients) client.Dispose();
            foreach (var app in _apps) await StopAndDisposeAsync(app);
        }
        finally
        {
            foreach (var reader in _readers) reader.Dispose();
            Directory.Delete(_dir, recursive: true);
        }
    }

    private BusReader CreateReader()
    {
        var reader = new BusReader(_path, _mapName, _mutexName);
        _readers.Add(reader);
        return reader;
    }

    private async Task StopAndDisposeAsync(WebApplication app)
    {
        try
        {
            LogPhase("stop started");
            using var deadline = new CancellationTokenSource(OperationBudget);
            await app.StopAsync(deadline.Token);
            LogPhase("stop completed");
        }
        finally { await app.DisposeAsync(); }
    }

    private void LogPhase(string phase) =>
        _output.WriteLine($"{phase}; elapsedMs={_elapsed.ElapsedMilliseconds}");

    private void LogLiveness(Dictionary<string, JsonElement> json) =>
        LogPhase($"liveness={json["liveness"].GetRawText()}");

    private async Task<HttpResponseMessage> GetAsync(HttpClient client, string url)
    {
        LogPhase($"request started {url}");
        try { return await client.GetAsync(url); }
        finally { LogPhase($"request finished {url}"); }
    }

    [Fact]
    public async Task GetRegs_ReturnsZones_And_DecodedFloats()
    {
        // Seed bus (raw file seeding — the host is strictly read-only, INV-39):
        // COM idx0 = 1.0f (but regs 4..5 are unix int32 — write idx1 UnitNum),
        // LAN idx0 = 15.79f, RAW idx0 = -42.0f, mark LAN active.
        SeedBusFile();
        SeedFloat(6, 123.456f);   // com idx1 (UnitNum)
        SeedFloat(200, 15.79f);   // lan idx0
        SeedFloat(400, -42.0f);   // raw idx0
        MarkLanActiveWithFutureClock();

        var reader = CreateReader();
        reader.TryOpen(out _);

        var client = await CreateClientAsync(reader, history: null);

        using var response = await GetAsync(client, "/api/regs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        Assert.NotNull(json);
        LogLiveness(json);

        var zones = json["zones"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        Assert.True(zones.ContainsKey("com"));
        Assert.True(zones.ContainsKey("lan"));
        Assert.True(zones.ContainsKey("raw"));

        var comPairs = zones["com"].GetProperty("pairs").EnumerateArray().ToList();
        Assert.True(comPairs.Count >= 2);
        var unitNum = comPairs.First(p => p.GetProperty("addr").GetInt32() == 6);
        Assert.Equal("float", unitNum.GetProperty("kind").GetString());
        Assert.Equal(123.456f, unitNum.GetProperty("value").GetSingle(), 3);

        var lanPairs = zones["lan"].GetProperty("pairs").EnumerateArray().ToList();
        var lan0 = lanPairs.First(p => p.GetProperty("addr").GetInt32() == 200);
        Assert.Equal(15.79f, lan0.GetProperty("value").GetSingle(), 2);

        var rawPairs = zones["raw"].GetProperty("pairs").EnumerateArray().ToList();
        var raw0 = rawPairs.First(p => p.GetProperty("addr").GetInt32() == 400);
        Assert.Equal(-42.0f, raw0.GetProperty("value").GetSingle(), 2);

        var liveness = json["liveness"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        Assert.True(liveness["lan"].GetProperty("active").GetBoolean());
        Assert.True(liveness["lan"].GetProperty("producerFlag").GetBoolean());
        Assert.Equal(0L, liveness["lan"].GetProperty("ageMs").GetInt64());
        Assert.Equal(1u, liveness["lan"].GetProperty("writeCount").GetUInt32());
    }

    // ── 2026-08-28 dishonest-surface fix: /api/regs liveness is AGE-GATED ──
    // The web zones-table badge renders live/stale straight from this object
    // (main.js), so an ungated raw activeFlag could show «live» on a dead bus.

    [Fact]
    public async Task GetRegs_Liveness_IsAgeGated_RawFlagExposedAsProducerFlag()
    {
        // LAN: the writer DIED 6 s ago with its flag frozen at 1 — the raw
        // claim is stale; the verdict must be inactive (pre-fix: active=true
        // straight from the raw flag ⇒ a web «live» badge on a dead bus).
        SeedBusFile();
        SeedFloat(200, 15.79f);
        WriteZoneHeader(ZoneKind.Lan, DateTime.UtcNow.AddMilliseconds(-6_000).Ticks, active: true, count: 42);
        // Synthetic future clock: force the production future-age clamp to 0,
        // independent of TestServer startup duration; not an ordinary fresh producer.
        WriteZoneHeader(ZoneKind.ComMain, DateTime.MaxValue.Ticks, active: true, count: 1);

        var reader = CreateReader();
        reader.TryOpen(out _);
        var client = await CreateClientAsync(reader, history: null);

        using var response = await GetAsync(client, "/api/regs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        Assert.NotNull(json);

        LogLiveness(json!);
        var liveness = json!["liveness"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        // THE GATE: frozen flag + 6 s tick ⇒ NOT live — same verdict the
        // /api/status/summary lamps get (BusZoneInfo.IsActive, ONE law).
        Assert.False(liveness["lan"].GetProperty("active").GetBoolean());
        // The raw writer claim is exposed SEPARATELY as a diagnostic field.
        Assert.True(liveness["lan"].GetProperty("producerFlag").GetBoolean());
        Assert.True(liveness["lan"].GetProperty("ageMs").GetInt64() > BusZoneInfo.MaxZoneAgeMs);
        Assert.Equal(42u, liveness["lan"].GetProperty("writeCount").GetUInt32());

        // Future-clock synthetic zone: active with clamped age 0 and raw claim.
        Assert.True(liveness["com"].GetProperty("active").GetBoolean());
        Assert.True(liveness["com"].GetProperty("producerFlag").GetBoolean());
        Assert.Equal(0L, liveness["com"].GetProperty("ageMs").GetInt64());
        Assert.Equal(1u, liveness["com"].GetProperty("writeCount").GetUInt32());

        // RAW: never written (flag 0, tick 0) ⇒ inactive both ways.
        Assert.False(liveness["raw"].GetProperty("active").GetBoolean());
        Assert.False(liveness["raw"].GetProperty("producerFlag").GetBoolean());
    }

    // ── INV-31: GET /api/regs/history (RESTORED 2026-08-27) ─────────────────

    [Fact]
    public async Task GetHistory_AddrKeyedSamples_OldestToNewest()
    {
        // Synthetic snapshot → ring (two ticks, value changed between them).
        var history = new RingHistory();
        var sampler = new BusAddrRingService(history);
        var tick1 = new DateTimeOffset(2026, 8, 27, 18, 57, 0, TimeSpan.Zero);
        var tick2 = tick1.AddSeconds(1);

        sampler.Ingest(SyntheticSnapshot(firstFloat: 1.5f), tick1);
        sampler.Ingest(SyntheticSnapshot(firstFloat: 123.456f), tick2);

        var reader = CreateReader();
        reader.TryOpen(out _);
        var client = await CreateClientAsync(reader, history);

        using var response = await GetAsync(client, "/api/regs/history?addr=6");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        Assert.True(json!.ContainsKey("6"));
        var points = json["6"].EnumerateArray().ToList();
        Assert.Equal(2, points.Count);                       // oldest → newest
        Assert.Equal(1.5f, points[0].GetProperty("v").GetSingle(), 3);
        Assert.Equal(123.456f, points[1].GetProperty("v").GetSingle(), 3);
        Assert.Equal(tick1.ToString("O"), points[0].GetProperty("ts").GetString());
        Assert.Equal(tick2.ToString("O"), points[1].GetProperty("ts").GetString());
    }

    [Fact]
    public async Task GetHistory_MultipleAddrs_KeyedResponse()
    {
        var history = new RingHistory();
        var sampler = new BusAddrRingService(history);
        sampler.Ingest(SyntheticSnapshot(firstFloat: 7.25f), DateTimeOffset.UtcNow);

        var reader = CreateReader();
        reader.TryOpen(out _);
        var client = await CreateClientAsync(reader, history);

        using var response = await GetAsync(client, "/api/regs/history?addr=6&addr=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        Assert.True(json!.ContainsKey("6"));
        Assert.True(json.ContainsKey("200"));
        Assert.Single(json["6"].EnumerateArray());           // both pairs ringed by the sampler
        Assert.Single(json["200"].EnumerateArray());
    }

    [Fact]
    public async Task GetHistory_ValidButUnringedAddr_EmptyArrayNeverNull()
    {
        var history = new RingHistory();
        var reader = CreateReader();
        reader.TryOpen(out _);
        var client = await CreateClientAsync(reader, history);

        using var response = await GetAsync(client, "/api/regs/history?addr=400");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        Assert.Equal(0, json!["400"].GetArrayLength());
    }

    [Theory]
    [InlineData("/api/regs/history")]                     // missing addr
    [InlineData("/api/regs/history?addr=5")]              // odd
    [InlineData("/api/regs/history?addr=3")]              // below range
    [InlineData("/api/regs/history?addr=600")]            // at exclusive end
    [InlineData("/api/regs/history?addr=4&addr=7")]       // one odd among valid
    [InlineData("/api/regs/history?addr=abc")]            // non-numeric
    public async Task GetHistory_BadAddr_400(string url)
    {
        var history = new RingHistory();
        var reader = CreateReader();
        reader.TryOpen(out _);
        var client = await CreateClientAsync(reader, history);

        using var response = await GetAsync(client, url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetHistory_RingNotRegistered_WebOff_503()
    {
        // web_on=0: composition registers NOTHING of the ring trio — the
        // endpoint must answer 503 «history disabled» (Э2 line 47), not 500.
        var reader = CreateReader();
        reader.TryOpen(out _);
        var client = await CreateClientAsync(reader, history: null);

        using var response = await GetAsync(client, "/api/regs/history?addr=4");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>Synthetic bus snapshot carrying ONE float pair at addr 6 —
    /// BusAddrRingService only reads snapshot.Words (registry-free, INV-32).</summary>
    private static BusSnapshot SyntheticSnapshot(float firstFloat)
    {
        var words = new ushort[600]; // live body 0..599
        var (low, high) = CdabCodec.Split(firstFloat);
        words[6] = low;
        words[7] = high;
        return new BusSnapshot(
            Magic: 0, Version: 1, LayoutVersion: 5, RegCount: 10001, Words: words,
            ComMain: new BusZoneInfo(ZoneKind.ComMain, 0, false, 0),
            Lan: new BusZoneInfo(ZoneKind.Lan, 0, false, 0),
            RawReserve: new BusZoneInfo(ZoneKind.RawReserve, 0, false, 0),
            Wits: new BusZoneInfo(ZoneKind.Wits, 0, false, 0),
            Stale: false, CapturedAtTickMs: 0);
    }

    [Fact]
    public async Task GetRegs_ScalarLabels_DecodedFromHeaderStateBlock()
    {
        SeedBusFile();
        SeedFloat(6, 1.0f);

        // Header STATE block (regs 0..3) is AddIn-owned — patch the body words
        // directly: reg0=1 (COM active), reg1=2 (InProgress), reg2=4 (Recording).
        PatchHeaderReg(0, 1);
        PatchHeaderReg(1, 2);
        PatchHeaderReg(2, 4);

        var reader = CreateReader();
        reader.TryOpen(out _);
        var client = await CreateClientAsync(reader, history: null);

        using var response = await GetAsync(client, "/api/regs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        Assert.NotNull(json);

        var scalars = json["system"].GetProperty("scalars").EnumerateArray().ToList();
        Assert.Equal(4, scalars.Count);
        Assert.Equal("COM active", scalars[0].GetProperty("label").GetString());
        Assert.Equal("InProgress", scalars[1].GetProperty("label").GetString());
        Assert.Equal("Recording", scalars[2].GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, scalars[3].GetProperty("label").ValueKind); // heartbeat: no semantic label
    }

    [Fact]
    public async Task GetRegs_ScalarLabelOutsideVendorEnum_IsInvalid()
    {
        SeedBusFile();
        SeedFloat(6, 1.0f);

        PatchHeaderReg(1, 99);  // RealtimeState ∉ {0..5} — invented value
        PatchHeaderReg(2, 3);   // ComPortStates composite {1,2} — banned combination

        var reader = CreateReader();
        reader.TryOpen(out _);
        var client = await CreateClientAsync(reader, history: null);

        using var response = await GetAsync(client, "/api/regs");
        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        var scalars = json!["system"].GetProperty("scalars").EnumerateArray().ToList();

        Assert.Equal("invalid", scalars[1].GetProperty("label").GetString());
        Assert.Equal("invalid", scalars[2].GetProperty("label").GetString());
    }

    /// <summary>Write one BE u16 word at body offset reg*2 (header STATE block patch).</summary>
    private void PatchHeaderReg(int reg, ushort value)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Write);
        fs.Position = BusLayout.BodyOffset + reg * 2;
        fs.WriteByte((byte)(value >> 8));
        fs.WriteByte((byte)(value & 0xFF));
    }

    /// <summary>
    /// Create the bus file as a zero-filled v5 identity shape (magic 'STNB',
    /// version=1, layoutVer=5, regCount=10001 LE, 20066 B total). Raw file
    /// seeding only — the host is strictly read-only (INV-39); BusReader opens
    /// its own read-only section and validates the header.
    /// </summary>
    private void SeedBusFile()
    {
        using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write);
        var bytes = new byte[BusLayout.TotalFileBytes]; // 64 B header + 20002 B body, all zero
        BusLayout.MagicBytes.CopyTo(bytes, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(bytes, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(BusLayout.LayoutVersion).CopyTo(bytes, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCount).CopyTo(bytes, BusLayout.HeaderRegCountOffset);
        fs.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Write one float32 as a 4-byte CDAB pair (low word BE, high word BE) at body offset reg*2.</summary>
    private void SeedFloat(int reg, float value)
    {
        var (low, high) = CdabCodec.Split(value);
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Write);
        fs.Position = BusLayout.BodyOffset + reg * 2;
        fs.WriteByte((byte)(low >> 8));
        fs.WriteByte((byte)(low & 0xFF));
        fs.WriteByte((byte)(high >> 8));
        fs.WriteByte((byte)(high & 0xFF));
    }

    /// <summary>Synthetic future-clock fixture: age clamps to 0 regardless of startup
    /// scheduling. This is not an ordinary fresh-producer/freshness-boundary test.</summary>
    private void MarkLanActiveWithFutureClock() =>
        WriteZoneHeader(ZoneKind.Lan, DateTime.MaxValue.Ticks, active: true, count: 1);

    /// <summary>Zone liveness header block (lastWriteTick u64 LE, activeFlag u8, writeCount u32 LE)
    /// for ANY zone at ANY age — the age-gated liveness tests seed OLD ticks deliberately.</summary>
    private void WriteZoneHeader(ZoneKind kind, long tick, bool active, uint count)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Write);
        fs.Position = BusLayout.ZoneLastWriteTickOffset(kind);
        var t = BitConverter.GetBytes(tick);
        fs.Write(t, 0, t.Length);
        fs.Position = BusLayout.ZoneActiveFlagOffset(kind);
        fs.WriteByte(active ? (byte)1 : (byte)0);
        fs.Position = BusLayout.ZoneWriteCountOffset(kind);
        var c = BitConverter.GetBytes(count);
        fs.Write(c, 0, c.Length);
    }

    private async Task<HttpClient> CreateClientAsync(BusReader reader, RingHistory? history)
    {
        // W22: WebHostBuilder is obsolete on net10 (ASPDEPR004) — minimal
        // hosting + UseTestServer, same shape as B5ContractTests.
        // history=null models web_on=0 (nothing of the ring trio registered —
        // the endpoint must degrade to 503, Э2 line 47).
        LogPhase("startup started");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton(reader);
        if (history is not null)
        {
            builder.Services.AddSingleton(history);
            builder.Services.AddSingleton(sp => new BusAddrRingService(sp.GetRequiredService<RingHistory>()));
        }

        var app = builder.Build();
        try
        {
            app.MapRegisterMirror();
            using var deadline = new CancellationTokenSource(OperationBudget);
            await app.StartAsync(deadline.Token);
            var server = (TestServer)app.Services.GetRequiredService<IServer>();
            var client = server.CreateClient();
            _apps.Add(app);
            _clients.Add(client);
            LogPhase("startup completed");
            return client;
        }
        catch
        {
            LogPhase("startup failed");
            await StopAndDisposeAsync(app);
            throw;
        }
    }
}
