using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Xunit;

namespace iCemDataMonitor.Contracts.Tests;

public sealed class TelemetryContractTests
{
    private static string GetProtoPath()
    {
        // The golden hash file is copied next to the test assembly; the proto is in the referenced project.
        var assemblyDir = Path.GetDirectoryName(typeof(TelemetryContractTests).Assembly.Location)!;
        var candidate = Path.Combine(assemblyDir, @"..\..\..\..\iCemDataMonitor.Contracts\Protos\telemetry.proto");
        return Path.GetFullPath(candidate);
    }

    private static string ReadGoldenHash()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(TelemetryContractTests).Assembly.Location)!;
        var path = Path.Combine(assemblyDir, "telemetry.proto.sha256");
        return File.ReadAllText(path).Trim();
    }

    [Fact]
    public void Proto_GoldenHash_MustMatch()
    {
        var protoPath = GetProtoPath();
        Assert.True(File.Exists(protoPath), $"proto file not found: {protoPath}");

        var bytes = File.ReadAllBytes(protoPath);
        var hash = BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", "");
        var golden = ReadGoldenHash();

        Assert.Equal(golden, hash);
    }

    [Fact]
    public void Proto_AdditiveSurface_MustBeSupersetOfGolden()
    {
        var assembly = typeof(HostStatus).Assembly;

        var goldenPath = Path.Combine(
            Path.GetDirectoryName(typeof(TelemetryContractTests).Assembly.Location)!,
            "telemetry.proto.fields.json");
        var golden = JsonSerializer.Deserialize<List<FieldIdentity>>(File.ReadAllText(goldenPath))!;

        var generated = new HashSet<FieldIdentity>();
        foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
        {
            var descriptorProp = type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
            if (descriptorProp?.GetValue(null) is not MessageDescriptor descriptor)
                continue;

            foreach (var field in descriptor.Fields.InFieldNumberOrder())
            {
                generated.Add(new FieldIdentity(type.Name, field.Name, field.FieldNumber));
            }
        }

        var missing = golden.Where(g => !generated.Contains(g)).ToList();
        if (missing.Count != 0)
        {
            var names = string.Join(", ", missing.Select(m => $"{m.Message}.{m.Field}#{m.Number}"));
            Assert.Fail($"Golden fields missing or renumbered in generated code: {names}");
        }

        // New fields are allowed (generated is a superset). Re-numbering is caught because the
        // golden identity with the old number no longer exists.
    }

    [Fact]
    public void Messages_RoundTrip()
    {
        var status = new HostStatus
        {
            BusOpen = true,
            AddinAlive = true,
            Heartbeat = 123,
            ComPort = 1,
            CaseState = 2,
            CaseLanState = 4,
            UpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            AutostartEnabled = true,
            Layers = new LayerState
            {
                Modbus = true,
                ModbusBound = true,
                ModbusPort = 502,
                Sniffer = false,
                SnifferAvailable = true,
                OpcDa = false,
                SetAsDefault = true,
                Busy = false,
                LastError = ""
            }
        };
        status.Zones.Add(new ZoneHealth { Kind = BusZoneKind.BusZoneCom, Active = true, AgeMs = 12, WriteCount = 7 });

        var statusBytes = status.ToByteArray();
        var statusBack = HostStatus.Parser.ParseFrom(statusBytes);
        Assert.Equal(status.ToByteArray(), statusBack.ToByteArray());

        var metrics = new MetricsSample
        {
            TimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Heartbeat = 77,
            ComPortActive = true,
            CaseState = 2,
            IntervalClamped = true
        };
        metrics.Deltas.Add(new RegisterDelta { Addr = 6, FloatValue = 3.14f });
        metrics.Deltas.Add(new RegisterDelta { Addr = 4, Int32Value = 1700000000 });

        var metricsBytes = metrics.ToByteArray();
        var metricsBack = MetricsSample.Parser.ParseFrom(metricsBytes);
        Assert.Equal(metrics.ToByteArray(), metricsBack.ToByteArray());

        var layer = new LayerState { Modbus = false, ModbusPort = 503, Busy = true, LastError = "busy" };
        var layerBytes = layer.ToByteArray();
        var layerBack = LayerState.Parser.ParseFrom(layerBytes);
        Assert.Equal(layer.ToByteArray(), layerBack.ToByteArray());

        // W20 F.1: GetRegisters messages — both oneof branches of RegisterValue.
        var registers = new RegistersReply
        {
            TimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Heartbeat = 555,
        };
        registers.Values.Add(new RegisterValue { Addr = 0, Int32Value = 3 });          // header word
        registers.Values.Add(new RegisterValue { Addr = 4, Int32Value = 1700000000 }); // Unix ts raw bits
        registers.Values.Add(new RegisterValue { Addr = 200, FloatValue = 1.5f });     // CDAB float pair
        registers.Values.Add(new RegisterValue { Addr = 598, FloatValue = -2.25f });   // last live pair

        var registersBytes = registers.ToByteArray();
        var registersBack = RegistersReply.Parser.ParseFrom(registersBytes);
        Assert.Equal(registers.ToByteArray(), registersBack.ToByteArray());
        Assert.Equal(4, registersBack.Values.Count);
        Assert.True(registersBack.Values[0].ValueCase == RegisterValue.ValueOneofCase.Int32Value);
        Assert.True(registersBack.Values[2].ValueCase == RegisterValue.ValueOneofCase.FloatValue);

        var registersRequest = new RegistersRequest { Zone = BusZoneKind.BusZoneLan };
        var requestBack = RegistersRequest.Parser.ParseFrom(registersRequest.ToByteArray());
        Assert.Equal(registersRequest, requestBack);
    }

    // ── W21.1: boot stages, console visibility, UI navigation ──────────────

    [Fact]
    public void W21_BootStage_RoundTrip()
    {
        var stage = new BootStage
        {
            Ordinal = 3,
            Id = "composition",
            Title = "композиция хоста",
            Completed = true,
            Detail = "composition build OK"
        };

        var back = BootStage.Parser.ParseFrom(stage.ToByteArray());

        Assert.Equal(stage, back);
        Assert.Equal(5, BootStage.Descriptor.Fields.InFieldNumberOrder().Count());
    }

    [Fact]
    public void W21_HostStatus_ConsoleVisible_IsField11()
    {
        var field = HostStatus.Descriptor.FindFieldByName("console_visible");
        Assert.NotNull(field);
        Assert.Equal(11, field.FieldNumber);

        var status = new HostStatus { ConsoleVisible = true };
        var back = HostStatus.Parser.ParseFrom(status.ToByteArray());
        Assert.True(back.ConsoleVisible);
    }

    [Fact]
    public void W21_SetConsoleVisible_RoundTrip()
    {
        var request = new SetConsoleVisibleRequest { Visible = false };
        var back = SetConsoleVisibleRequest.Parser.ParseFrom(request.ToByteArray());
        Assert.False(back.Visible);
        Assert.Single(SetConsoleVisibleRequest.Descriptor.Fields.InFieldNumberOrder());
    }

    [Fact]
    public void W21_NavigateUi_RoundTrip()
    {
        var request = new NavigateUiRequest { Section = UiSection.Status };
        var response = new NavigateUiResponse { UiAlive = false, Pending = UiSection.Status };

        var requestBack = NavigateUiRequest.Parser.ParseFrom(request.ToByteArray());
        var responseBack = NavigateUiResponse.Parser.ParseFrom(response.ToByteArray());

        Assert.Equal(UiSection.Status, requestBack.Section);
        Assert.False(responseBack.UiAlive);
        Assert.Equal(UiSection.Status, responseBack.Pending);
    }

    [Fact]
    public void W21_UiSection_Values_AreClosedContract()
    {
        // Manifest-value seam: the section list mirrors NavigationView tags in
        // MainWindow.xaml exactly and is CLOSED. Adding a shell section is a
        // schema-delta, not a silent enum extension. 2026-08-27: the web SPA
        // section («Панель») RETURNED — the owner decree «Web - удали
        // (контрол sim)» meant removing the sim control FROM the page, not
        // the page itself. The emulator section stays removed (separate
        // independent app).
        var names = Enum.GetNames<UiSection>();
        Assert.Equal(
            new[] { "Unspecified", "Web", "Status" },
            names);
    }

    [Fact]
    public void W21_ServiceSurface_NewRpcsPresent_RemovedRpcsAbsent()
    {
        // Assert via the client surface which Grpc generates per rpc.
        var clientMethods = typeof(TelemetryService.TelemetryServiceClient).GetMethods();

        Assert.Contains(clientMethods, m => m.Name == "SubscribeBootStages");
        Assert.Contains(clientMethods, m => m.Name == "SetConsoleVisible");
        Assert.Contains(clientMethods, m => m.Name == "NavigateUi");
        Assert.Contains(clientMethods, m => m.Name == "SetAutostart");
        // 2026-08-31: the theme bool's machine-layer RPCs must exist on the
        // client surface (the web SPA / WinUI shell consume them).
        Assert.Contains(clientMethods, m => m.Name == "GetUiPrefs");
        Assert.Contains(clientMethods, m => m.Name == "SetUiTheme");

        // Tombstone law (W19 E.2.7): no shutdown RPC may ever return.
        Assert.DoesNotContain(clientMethods, m => m.Name.Contains("Shutdown"));

        // Tombstone (2026-08-27): the emulator is a separate independent app —
        // the host no longer controls it, so ControlEngine may never return.
        Assert.DoesNotContain(clientMethods, m => m.Name.Contains("ControlEngine"));
    }

    [Fact]
    public void Autostart_HostStatus_Field12_RoundTrip()
    {
        // 2026-08-27: HKCU Run-key truth (value name iCemDataMonitorHost,
        // mirrors tools\install-autostart.ps1).
        var field = HostStatus.Descriptor.FindFieldByName("autostart_enabled");
        Assert.NotNull(field);
        Assert.Equal(12, field.FieldNumber);

        var status = new HostStatus { AutostartEnabled = true };
        var back = HostStatus.Parser.ParseFrom(status.ToByteArray());
        Assert.True(back.AutostartEnabled);
    }

    [Fact]
    public void Autostart_SetAutostartRequest_RoundTrip()
    {
        var request = new SetAutostartRequest { Enabled = false };
        var back = SetAutostartRequest.Parser.ParseFrom(request.ToByteArray());
        Assert.False(back.Enabled);
        Assert.Single(SetAutostartRequest.Descriptor.Fields.InFieldNumberOrder());
    }

    // ═══ 2026-08-31 (owner decree «даже смена темы — тоже CLI»): station-wide
    // theme bool as machine-layer RPCs (GetUiPrefs / SetUiTheme). Additive
    // tier: brand-new messages + field 1; no existing number touched, all
    // tombstone reserved blocks untouched. ═══

    [Fact]
    public void UiTheme_UiPrefs_Field1_RoundTrip()
    {
        var field = UiPrefs.Descriptor.FindFieldByName("theme_light");
        Assert.NotNull(field);
        Assert.Equal(1, field.FieldNumber);

        // theme_light=true round-trips; note proto3 omits the FALSE default,
        // so the false-case check is a parse-to-default (not byte-equality).
        var light = new UiPrefs { ThemeLight = true };
        Assert.True(UiPrefs.Parser.ParseFrom(light.ToByteArray()).ThemeLight);

        var dark = new UiPrefs { ThemeLight = false };
        Assert.False(UiPrefs.Parser.ParseFrom(dark.ToByteArray()).ThemeLight);
        Assert.Empty(dark.ToByteArray()); // proto3 default omission — the SPA must check trailers, not message size
    }

    [Fact]
    public void UiTheme_SetUiThemeRequest_RoundTrip()
    {
        var request = new SetUiThemeRequest { ThemeLight = true };
        var back = SetUiThemeRequest.Parser.ParseFrom(request.ToByteArray());
        Assert.True(back.ThemeLight);
        Assert.Single(SetUiThemeRequest.Descriptor.Fields.InFieldNumberOrder());

        Assert.Empty(new UiPrefsRequest().ToByteArray()); // empty request arm — same shape as HostStatusRequest
    }

    private sealed record FieldIdentity(string Message, string Field, int Number);
}
