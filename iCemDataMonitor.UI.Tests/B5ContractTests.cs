using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;
using iCemDataMonitor.Transport;
using iCemDataMonitor.Transport.Models;
using iCemDataMonitor.Host.Core.Endpoints;

namespace iCemDataMonitor.UI.Tests;

public class B5ContractTests : IAsyncLifetime
{
    private readonly Mock<ISnifferService> _snifferMock = new();
    private readonly Mock<ITeeService> _teeMock = new();
    private readonly Mock<ICom0ComMonitor> _com0comMock = new();
    private readonly Mock<IConfigStore> _configMock = new();
    private readonly Mock<IModbusPublisher> _modbusMock = new();
    private readonly Mock<ITimeseriesStore> _timeseriesMock = new();
    private HttpClient _client = null!;
    // W20 F.2: the old Tray wwwroot\mock fixtures path is GONE with the Tray
    // project; the FixturesDir field was write-only (never read) and is removed.

    public async Task InitializeAsync()
    {
        SetupDefaultMocks();
        _client = await CreateClientAsync();
    }

    private void SetupDefaultMocks()
    {
        // ─── ISnifferService defaults ───
        _snifferMock.Setup(s => s.GetStatus()).Returns(new AceSnifferStatus
        {
            Adapter = "Intel(R) Ethernet Connection I219-LM",
            MacAddress = "00:1A:2B:3C:4D:5E",
            Up = true,
            FramesPerSec = 6.24,
            LastFrameAgeSec = 0.31,
            TotalFrames = 152340,
            DecodeErrors = 3,
            PrefixCounts = new Dictionary<string, int> { ["030308"] = 148920, ["030309"] = 3420 },
            Equipment = new List<AceEquipmentStatus>
            {
                new() { EquipmentNumber = 11075398, MacAddress = "00:A8:6F:46:00:01", VectorCount = 6, TotalFrameRate = 6.0 }
            },
        });

        _snifferMock.Setup(s => s.GetServiceVectors()).Returns(new List<AceServiceVectorInfo>
        {
            new() { VectorId = 19, Name = "Lnd Mix Section", IsUnknown = false, FrameRate = 1.0, LastSeenSec = 0.3, DecodedSignalCount = 23 },
            new() { VectorId = 21, Name = "Lnd Pump Sect", IsUnknown = false, FrameRate = 1.0, LastSeenSec = 0.5, DecodedSignalCount = 18 },
            new() { VectorId = 169, Name = "", IsUnknown = true, FrameRate = 0.33, LastSeenSec = 2.1, DecodedSignalCount = 0 },
        });

        _snifferMock.Setup(s => s.GetSignalPreview(It.IsAny<int>())).Returns(new List<AceSignalPreview>
        {
            new() { Name = "Pump A Pressure", Value = 126.3, Uom = "psi", Sentinel = false },
            new() { Name = "Pump B Pressure", Value = -9999.0, Uom = "psi", Sentinel = true },
        });

        _snifferMock.Setup(s => s.GetCaptureStatus()).Returns(new AceCaptureStatus { Capturing = false });

        // ─── ITeeService defaults ───
        _teeMock.Setup(t => t.GetStatus()).Returns(new ComTeeStatus
        {
            PhysicalPort = "COM2",
            Baud = 9600,
            TemplateName = "flecs_ver4p53_ppg_bpm_psi",
            Up = true,
            BytesForwardedPerSec = 12400.0,
            BytesParsedPerSec = 34.1,
            ForwardErrors = 0,
            LastForwardAgeSec = 0.08,
            LastParseAgeSec = 0.12,
            Com0comPairOk = true,
            PhysicalPortBusy = false,
        });

        // ─── ICom0ComMonitor defaults ───
        _com0comMock.Setup(c => c.GetStatus()).Returns(new Com0ComStatus
        {
            DriverInstalled = true,
            DriverRunning = true,
            DriverVersion = "3.0.0.0",
            InstallDir = @"C:\Program Files (x86)\com0com",
            Pairs = new List<Com0ComPair>
            {
                new()
                {
                    BusId = 0, BusNameA = "CNCA0", BusNameB = "CNCB0",
                    PortNameA = "COM1", PortNameB = "COM2",
                    AttrsA = new Dictionary<string, string> { ["PortName"] = "COM1", ["dsr"] = "on", ["dcd"] = "on" },
                    AttrsB = new Dictionary<string, string> { ["PortName"] = "COM2", ["dsr"] = "on", ["dcd"] = "on" },
                }
            },
            TopologyMatch = new TopologyMatchStatus { Matched = true },
        });

        _com0comMock.Setup(c => c.LaunchConfigTool(It.Is<string>(s =>
                s == "setup" || s == "setupg" || s == "setupc")))
            .Returns(new LaunchResult(true, "launched", 9999));

        _com0comMock.Setup(c => c.LaunchConfigTool(It.Is<string>(s =>
                s != "setup" && s != "setupg" && s != "setupc")))
            .Returns(new LaunchResult(false, "not whitelisted"));

        _com0comMock.Setup(c => c.RefreshAsync()).ReturnsAsync(() => _com0comMock.Object.GetStatus());

        // ─── IConfigStore defaults ───
        var families = new List<DeviceFamily>
        {
            new("flecs", new List<DeviceConfig>
            {
                new("flecs_ver4p53_ppg_bpm_psi", new List<VariableDefinition>
                {
                    new("Time & Date", "Main Time & Date", "", "#888888", 1, false, "ssf"),
                    new("Unit Num", "Unit Number", "", "#888888", 2, false, "ssf"),
                }),
            }),
        };
        _configMock.Setup(c => c.GetAllFamilies()).Returns(families);
        _configMock.Setup(c => c.GetActiveVariables()).Returns(families[0].Configs[0].Variables);
    }

    private async Task<HttpClient> CreateClientAsync()
    {
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.WebHost.UseTestServer();
        appBuilder.Services.AddSingleton(_snifferMock.Object);
        appBuilder.Services.AddSingleton(_teeMock.Object);
        appBuilder.Services.AddSingleton(_com0comMock.Object);
        appBuilder.Services.AddSingleton(_configMock.Object);
        appBuilder.Services.AddSingleton(_modbusMock.Object);
        appBuilder.Services.AddSingleton(_timeseriesMock.Object);

        var app = appBuilder.Build();
        app.UseRouting();

        app.MapSnifferEndpoints();
        app.MapCom0ComEndpoints();
        app.MapModbusPreviewEndpoints();
        app.MapRegistryEndpoints();
        app.MapTemplatesEndpoints();

        await app.StartAsync();
        var server = (TestServer)app.Services.GetRequiredService<IServer>();
        return server.CreateClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ══════════════════════════════════════
    // Endpoint 1: GET /api/sniffer/status
    // ══════════════════════════════════════

    [Fact]
    public async Task SnifferStatus_ReturnsAceAndTee()
    {
        var response = await _client.GetAsync("/api/sniffer/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ace", out var ace));
        Assert.True(ace.TryGetProperty("adapter", out _));
        Assert.True(root.TryGetProperty("tee", out var tee));
        Assert.True(tee.TryGetProperty("physicalPort", out _));
    }

    // ══════════════════════════════════════
    // Endpoint 2: GET /api/sniffer/service-vectors
    // ══════════════════════════════════════

    [Fact]
    public async Task ServiceVectors_ReturnsArray()
    {
        var response = await _client.GetAsync("/api/sniffer/service-vectors");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(arr);

        var first = arr[0];
        Assert.True(first.TryGetProperty("vectorId", out _));
        Assert.True(first.TryGetProperty("isUnknown", out _));

        Assert.Contains(arr, e => e.TryGetProperty("isUnknown", out var u) && u.GetBoolean());
    }

    // ══════════════════════════════════════
    // Endpoint 3: GET /api/sniffer/signal-preview
    // ══════════════════════════════════════

    [Fact]
    public async Task SignalPreview_ReturnsArrayWithSentinel()
    {
        var response = await _client.GetAsync("/api/sniffer/signal-preview?vectorId=21");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(arr);

        foreach (var sig in arr)
        {
            Assert.True(sig.TryGetProperty("name", out _));
            Assert.True(sig.TryGetProperty("value", out _));
            Assert.True(sig.TryGetProperty("uom", out _));
            Assert.True(sig.TryGetProperty("sentinel", out _));
        }

        Assert.Contains(arr, s =>
            s.TryGetProperty("sentinel", out var sn) && sn.GetBoolean() &&
            s.TryGetProperty("value", out var sv) && Math.Abs(sv.GetDouble() - (-9999.0)) < 0.01);
    }

    [Fact]
    public async Task SignalPreview_MissingVectorId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/sniffer/signal-preview");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ══════════════════════════════════════
    // Endpoint 4: POST /api/sniffer/control
    // ══════════════════════════════════════

    [Fact]
    public async Task SnifferControl_AceStart_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/api/sniffer/control",
            new { target = "ace", action = "start" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SnifferControl_InvalidTarget_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/sniffer/control",
            new { target = "invalid", action = "start" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ══════════════════════════════════════
    // Endpoint 5: POST /api/sniffer/capture
    // ══════════════════════════════════════

    [Fact]
    public async Task SnifferCapture_Start_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/api/sniffer/capture",
            new { action = "start", durationSec = 30 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SnifferCapture_Stop_ReturnsOk()
    {
        _snifferMock.Setup(s => s.GetCaptureStatus()).Returns(new AceCaptureStatus
        {
            Capturing = false, FramesCaptured = 120, ElapsedSec = 20.0,
            TargetPath = @"captures\ace_20260809_143000.pcapng",
        });
        _snifferMock.Setup(s => s.GetCaptureFilePath()).Returns(@"C:\captures\ace_20260809_143000.pcapng");

        var response = await _client.PostAsJsonAsync("/api/sniffer/capture", new { action = "stop" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ══════════════════════════════════════
    // Endpoint 6: GET /api/sniffer/capture/status
    // ══════════════════════════════════════

    [Fact]
    public async Task CaptureStatus_NoCapture_ReturnsNullActiveCapture()
    {
        _snifferMock.Setup(s => s.GetCaptureStatus()).Returns(new AceCaptureStatus { Capturing = false });

        var response = await _client.GetAsync("/api/sniffer/capture/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("activeCapture", out var ac));
        Assert.Equal(JsonValueKind.Null, ac.ValueKind);
    }

    // ══════════════════════════════════════
    // Endpoint 7: GET /api/sniffer/capture/download — security
    // ══════════════════════════════════════

    [Fact]
    public async Task CaptureDownload_NoCaptureId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/sniffer/capture/download");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CaptureDownload_PathTraversal_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/sniffer/capture/download?captureId=..%2F..%2Fwindows%2Fsystem32");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ══════════════════════════════════════
    // Endpoint 8: GET /api/com0com/status
    // ══════════════════════════════════════

    [Fact]
    public async Task Com0ComStatus_ReturnsFullShape()
    {
        var response = await _client.GetAsync("/api/com0com/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("driverInstalled", out _));
        Assert.True(root.TryGetProperty("driverRunning", out _));
        Assert.True(root.TryGetProperty("topologyMatch", out var tm));
        Assert.True(tm.TryGetProperty("matched", out _));
    }

    // ══════════════════════════════════════
    // Endpoint 9: POST /api/com0com/open-config — whitelist
    // ══════════════════════════════════════

    [Fact]
    public async Task Com0ComOpenConfig_ValidTool_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/api/com0com/open-config", new { tool = "setupc" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Com0ComOpenConfig_EvilTool_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/com0com/open-config", new { tool = "evil.exe" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Com0ComOpenConfig_PathTraversal_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/com0com/open-config",
            new { tool = "../windows/system32/cmd" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ══════════════════════════════════════
    // Endpoint 10: POST /api/com0com/refresh
    // ══════════════════════════════════════

    [Fact]
    public async Task Com0ComRefresh_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/api/com0com/refresh", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("status", out _));
    }

    // ══════════════════════════════════════
    // Endpoint 11: GET /api/modbus/preview
    // ══════════════════════════════════════

    [Fact]
    public async Task ModbusPreview_ReturnsEntries()
    {
        var response = await _client.GetAsync("/api/modbus/preview?family=flecs&configName=flecs_ver4p53_ppg_bpm_psi");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("baseOffset", out _));
        Assert.True(root.TryGetProperty("entries", out var entries));
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);

        if (entries.GetArrayLength() > 0)
        {
            var first = entries[0];
            Assert.True(first.TryGetProperty("variableName", out _));
            Assert.True(first.TryGetProperty("registerAddress", out _));
        }
    }

    // ══════════════════════════════════════
    // Endpoint 12: GET /api/registry + mapping
    // ══════════════════════════════════════

    [Fact]
    public async Task Registry_ReturnsFamiliesAndVariables()
    {
        var response = await _client.GetAsync("/api/registry");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegistryMapping_ReturnsMappings()
    {
        var response = await _client.GetAsync("/api/registry/mapping?templateFamily=flecs&templateName=flecs_ver4p53_ppg_bpm_psi");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ══════════════════════════════════════
    // Endpoint 13: Templates
    // ══════════════════════════════════════

    [Fact]
    public async Task TemplatesFiles_ReturnsArray()
    {
        var response = await _client.GetAsync("/api/templates/files");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(arr);

        var first = arr[0];
        Assert.True(first.TryGetProperty("family", out _));
        Assert.True(first.TryGetProperty("configs", out _));
    }

    [Fact]
    public async Task TemplateDetail_ReturnsVariables()
    {
        var response = await _client.GetAsync("/api/templates/detail?family=flecs&name=flecs_ver4p53_ppg_bpm_psi");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TemplateDetail_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/templates/detail?family=flecs&name=nonexistent_template");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TemplateSave_ReturnsOk()
    {
        var body = new
        {
            family = "flecs",
            name = "test_template_b5",
            variables = new[]
            {
                new { name = "TestVar1", description = "Test", uom = "psi", color = "#000", position = 1, isSpare = false, source = "ssf" },
            }
        };
        var response = await _client.PostAsJsonAsync("/api/templates/save", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TemplateDelete_ReturnsOk()
    {
        await _client.PostAsJsonAsync("/api/templates/save", new
        {
            family = "test", name = "test_delete_b5",
            variables = new[] { new { name = "X", description = "X", uom = "", color = "#000", position = 1, isSpare = false, source = "ssf" } }
        });

        var response = await _client.PostAsJsonAsync("/api/templates/delete", new { family = "test", name = "test_delete_b5" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ══════════════════════════════════════
    // Bonus: tee-status + tee-control
    // ══════════════════════════════════════

    [Fact]
    public async Task TeeStatus_ReturnsTeeFields()
    {
        var response = await _client.GetAsync("/api/tee/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("physicalPort", out _));
        Assert.True(root.TryGetProperty("baud", out _));
        Assert.True(root.TryGetProperty("up", out _));
    }

    [Fact]
    public async Task TeeControl_Start_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/api/tee/control", new { action = "start" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ══════════════════════════════════════
    // com0com degradation (no driver)
    // ══════════════════════════════════════

    [Fact]
    public async Task Com0Com_Degraded_WithoutDriver()
    {
        var degraded = new Mock<ICom0ComMonitor>();
        degraded.Setup(m => m.GetStatus()).Returns(new Com0ComStatus
        {
            DriverInstalled = false,
            DriverRunning = false,
            LastError = "Service 'com0com' not installed",
            TopologyMatch = new TopologyMatchStatus { Matched = false },
        });
        degraded.Setup(m => m.RefreshAsync()).ReturnsAsync(() => degraded.Object.GetStatus());
        degraded.Setup(m => m.LaunchConfigTool(It.IsAny<string>()))
            .Returns(new LaunchResult(false, "com0com not found"));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(degraded.Object);

        var app = builder.Build();
        app.UseRouting();
        app.MapCom0ComEndpoints();
        await app.StartAsync();
        var server = (TestServer)app.Services.GetRequiredService<IServer>();

        var client = server.CreateClient();
        var response = await client.GetAsync("/api/com0com/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("driverInstalled").GetBoolean());
        Assert.False(root.GetProperty("topologyMatch").GetProperty("matched").GetBoolean());
    }
}
