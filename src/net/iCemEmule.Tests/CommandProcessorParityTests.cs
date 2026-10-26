// CommandProcessorParityTests — P1 2026-08-26 («переключатели не работают»).
// Pins the EXACT JSON command lines that iCemDataMonitor.Emulator.WinUI
// (EmulatorViewModel / EmuPipeClient) puts on the wire against the engine's
// CommandProcessor, so UI↔engine protocol drift breaks the BUILD, not the
// operator's switches. Every test replays a literal from the UI source:
//   ToggleCom/ToggleLan   → {"cmd":"start|stop","channel":"com|lan"}
//   SetFirmwareCommand    → {"cmd":"setfirmware","value":1.0|0.0}
//   SetEmulateCommand     → {"cmd":"setemulate","channel":..,"value":1.0|0.0}
//   SetEquipmentCommand   → {"cmd":"setequipment","value":<double uint32>}
//   SetPresetCommand      → {"cmd":"setpreset","value":<double 0..3>}
//   SetUnitCommand        → {"cmd":"setunit","channel":<group>,"name":<unit>?}
//   SetProfileCommand     → {"cmd":"profile","name":..}
//   SetVar/ClearVar       → {"cmd":"set|clear","channel":..,"name":..,"value"?:..,"sv"?:"0x.."}
//   RefreshEndpoints      → {"cmd":"ports"} / {"cmd":"adapters"}
//   RefreshVars           → {"cmd":"vars"}
// plus the reply envelope law: {"ok":bool,"cmd":str} + "data"|"error".
// CLI text forms (--ctl) are covered alongside — same dispatcher.

using System.Text.Json;
using Halliburton.iCemEmule.Control;
using Xunit;

namespace iCemEmule.Tests;

public class CommandProcessorParityTests
{
    // ── recording fake (mirrors the engine contract, no hardware) ────────
    private sealed class RecordingControl : IEmuControl
    {
        public List<(string Channel, bool On)> EmulationCalls = new();
        public List<bool> FirmwareCalls = new();
        public List<uint> EquipmentCalls = new();
        public List<int> PresetCalls = new();
        public List<(string Group, string? Unit)> UnitCalls = new();
        public List<(string? DeviceType, string Name)> ProfileCalls = new();
        public List<(string? Channel, string Name, double Value, uint? Sv)> SetCalls = new();
        public List<(string? Channel, string Name, uint? Sv)> ClearCalls = new();
        public List<string?> StartCalls = new();
        public List<string?> StopCalls = new();

        private bool _comOn, _lanOn;

        public Dictionary<string, object?> GetStatus() => new()
        {
            ["uptimeSec"] = 1.0,
            ["pid"] = 1234,
            ["selfTest"] = null,
            ["com"] = new Dictionary<string, object?>
            {
                // P2 2026-08-26: fixtures mirror the engine's passive startup —
                // streaming/emulating OFF until the operator flips them.
                ["streaming"] = _comOn, ["emulating"] = false, ["portOpen"] = _comOn,
                ["port"] = "COM2", ["baud"] = 9600, ["linesSent"] = 0L, ["linesPerSec"] = 0.0,
                ["deviceType"] = "fake", ["template"] = "fake", ["variableCount"] = 0,
                ["values"] = new Dictionary<string, object?>()
            },
            ["lan"] = new Dictionary<string, object?>
            {
                ["streaming"] = _lanOn, ["emulating"] = false, ["adapterOpen"] = _lanOn,
                ["adapter"] = "fake0", ["framesSent"] = 0L, ["framesPerSec"] = 0.0,
                ["equipment"] = "0x00A86F46", ["svCount"] = 1, ["firmwareSv"] = false,
                ["svs"] = new List<object?>(), ["targets"] = new List<object?>()
            },
            ["units"] = new Dictionary<string, object?>
            {
                ["preset"] = "LanNative",
                ["flowOverride"] = null, ["temperatureOverride"] = null,
                ["densityOverride"] = null, ["volumeOverride"] = null,
                ["levelOverride"] = null, ["pressureOverride"] = null
            },
            ["overrides"] = new List<object?>()
        };

        public ControlResult StartChannel(string channel)
        {
            StartCalls.Add(channel);
            if (channel == "com") _comOn = true; else _lanOn = true;
            return ControlResult.Success(new Dictionary<string, object?>
            {
                ["channel"] = channel, ["streaming"] = true
            });
        }

        public ControlResult StopChannel(string channel)
        {
            StopCalls.Add(channel);
            if (channel == "com") _comOn = false; else _lanOn = false;
            return ControlResult.Success(new Dictionary<string, object?>
            {
                ["channel"] = channel, ["wasStreaming"] = true, ["streaming"] = false
            });
        }

        public ControlResult SetVariable(string? channel, string name, double value, uint? sv)
        {
            SetCalls.Add((channel, name, value, sv));
            return ControlResult.Success(new Dictionary<string, object?> { ["applied"] = new List<object?>() });
        }

        public ControlResult ClearVariable(string? channel, string name, uint? sv)
        {
            ClearCalls.Add((channel, name, sv));
            return ControlResult.Success(new Dictionary<string, object?> { ["cleared"] = new List<object?>() });
        }

        public ControlResult GetVariable(string? channel, string name, uint? sv) =>
            ControlResult.Success(new Dictionary<string, object?> { ["name"] = name, ["value"] = 1.0 });

        public ControlResult GetProfiles() =>
            ControlResult.Success(new Dictionary<string, object?>
            {
                ["profiles"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["deviceType"] = "t", ["name"] = "n", ["variableCount"] = 1, ["active"] = true }
                }
            });

        public ControlResult SetProfile(string? deviceType, string name)
        {
            ProfileCalls.Add((deviceType, name));
            return ControlResult.Success(new Dictionary<string, object?> { ["deviceType"] = deviceType, ["name"] = name });
        }

        public ControlResult GetVariables(string? channel) =>
            ControlResult.Success(new Dictionary<string, object?>
            {
                ["com"] = new Dictionary<string, object?>
                {
                    ["template"] = "fake",
                    ["variables"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["name"] = "V", ["uom"] = "psi", ["value"] = 1.0, ["overridden"] = false }
                    }
                },
                ["lan"] = new Dictionary<string, object?>
                {
                    ["variables"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["sv"] = "0x00000001", ["name"] = "S", ["uom"] = "gpm", ["value"] = 2.0, ["overridden"] = false }
                    }
                }
            });

        public ControlResult GetPorts() =>
            ControlResult.Success(new Dictionary<string, object?>
            {
                ["ports"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "COM2", ["open"] = false } }
            });

        public ControlResult GetAdapters() =>
            ControlResult.Success(new Dictionary<string, object?>
            {
                ["adapters"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "fake0", ["open"] = false } }
            });

        public ControlResult SetPort(string port) => ControlResult.Success(new Dictionary<string, object?> { ["port"] = port });
        public ControlResult SetAdapter(string adapter) => ControlResult.Success(new Dictionary<string, object?> { ["adapter"] = adapter });
        public ControlResult SetEquipment(uint equipment)
        {
            EquipmentCalls.Add(equipment);
            return ControlResult.Success(new Dictionary<string, object?> { ["equipment"] = $"0x{equipment:X8}" });
        }

        public ControlResult SetFirmware(bool on)
        {
            FirmwareCalls.Add(on);
            return ControlResult.Success(new Dictionary<string, object?> { ["firmwareSv"] = on });
        }

        public ControlResult SetEmulation(string channel, bool on)
        {
            EmulationCalls.Add((channel, on));
            return ControlResult.Success(new Dictionary<string, object?> { ["channel"] = channel, ["emulating"] = on });
        }

        public ControlResult SetPreset(int presetIndex)
        {
            PresetCalls.Add(presetIndex);
            return ControlResult.Success(new Dictionary<string, object?> { ["preset"] = presetIndex.ToString() });
        }

        public ControlResult SetUnit(string group, string? unit)
        {
            UnitCalls.Add((group, unit));
            return ControlResult.Success(new Dictionary<string, object?> { ["group"] = group, ["unit"] = unit });
        }

        public void RequestQuit() { }
    }

    private static JsonElement Parse(ControlReply reply)
    {
        var doc = JsonDocument.Parse(reply.Json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        return doc.RootElement.Clone(); // clone: doc disposed on return
    }

    private static void AssertEnvelopeOk(JsonElement root, string expectedCmd)
    {
        Assert.True(root.TryGetProperty("ok", out var ok) && ok.GetBoolean(), $"reply not ok: {root.GetRawText()}");
        Assert.True(root.TryGetProperty("cmd", out var cmd) && cmd.GetString() == expectedCmd,
            $"reply cmd mismatch: {root.GetRawText()}");
        Assert.True(root.TryGetProperty("data", out _), $"ok reply must carry data: {root.GetRawText()}");
        Assert.False(root.TryGetProperty("error", out _), $"ok reply must NOT carry error: {root.GetRawText()}");
    }

    private static void AssertEnvelopeError(JsonElement root)
    {
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.True(root.TryGetProperty("error", out var err) && !string.IsNullOrEmpty(err.GetString()));
        Assert.False(root.TryGetProperty("data", out _), "error reply must NOT carry data");
    }

    // ── switches: start/stop per channel (UI ToggleCom/ToggleLan) ─────────

    [Theory]
    [InlineData("start", "com")]
    [InlineData("stop", "com")]
    [InlineData("start", "lan")]
    [InlineData("stop", "lan")]
    public void Toggle_Commands_Parse_Channel_And_Flip(string cmd, string channel)
    {
        var ctl = new RecordingControl();
        var root = Parse(CommandProcessor.ProcessLine($"{{\"cmd\":\"{cmd}\",\"channel\":\"{channel}\"}}", ctl));

        AssertEnvelopeOk(root, cmd);
        if (cmd == "start") Assert.Equal(channel, Assert.Single(ctl.StartCalls));
        else
        {
            Assert.Equal(channel, Assert.Single(ctl.StopCalls));
            // P1 additive: stop replies carry the resulting state for the UI.
            Assert.False(root.GetProperty("data").GetProperty("streaming").GetBoolean());
        }
    }

    [Fact]
    public void Toggle_TextForm_Cli_Parity_StartCom()
    {
        var ctl = new RecordingControl();
        AssertEnvelopeOk(Parse(CommandProcessor.ProcessLine("start com", ctl)), "start");
        Assert.Equal("com", Assert.Single(ctl.StartCalls));
    }

    [Fact]
    public void Toggle_BadChannel_IsHonestError_NotSilent()
    {
        var ctl = new RecordingControl();
        AssertEnvelopeError(Parse(CommandProcessor.ProcessLine("{\"cmd\":\"start\",\"channel\":\"udp\"}", ctl)));
        Assert.Empty(ctl.StartCalls);
    }

    // ── firmware switch (SetFirmwareCommand) ─────────────────────────────

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.0, false)]
    public void SetFirmware_UiWireForm_ReachesEngine(double value, bool expected)
    {
        var ctl = new RecordingControl();
        var root = Parse(CommandProcessor.ProcessLine($"{{\"cmd\":\"setfirmware\",\"value\":{value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}}}", ctl));
        AssertEnvelopeOk(root, "setfirmware");
        Assert.Equal(expected, Assert.Single(ctl.FirmwareCalls));
        Assert.Equal(expected, root.GetProperty("data").GetProperty("firmwareSv").GetBoolean());
    }

    // ── emulation gates (SetEmulateCommand, W14 freeze) ───────────────────

    [Theory]
    [InlineData("com", 1.0, "com", true)]
    [InlineData("lan", 0.0, "lan", false)]
    public void SetEmulate_UiWireForm_ReachesEngine(string channel, double value, string expChannel, bool expOn)
    {
        var ctl = new RecordingControl();
        var root = Parse(CommandProcessor.ProcessLine(
            "{\"cmd\":\"setemulate\",\"channel\":\"" + channel + "\",\"value\":" +
            value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "}", ctl));
        AssertEnvelopeOk(root, "setemulate");
        Assert.Equal((expChannel, expOn), Assert.Single(ctl.EmulationCalls));
    }

    // ── equipment / preset / units (LAN tab + Units tab) ──────────────────

    [Fact]
    public void SetEquipment_DoubleEncodedUint_Accepted()
    {
        var ctl = new RecordingControl();
        var root = Parse(CommandProcessor.ProcessLine("{\"cmd\":\"setequipment\",\"value\":1107296256.0}", ctl));
        AssertEnvelopeOk(root, "setequipment");
        Assert.Equal(1107296256u, Assert.Single(ctl.EquipmentCalls));
    }

    [Fact]
    public void SetPreset_IndexAsDouble_Accepted()
    {
        var ctl = new RecordingControl();
        AssertEnvelopeOk(Parse(CommandProcessor.ProcessLine("{\"cmd\":\"setpreset\",\"value\":2.0}", ctl)), "setpreset");
        Assert.Equal(2, Assert.Single(ctl.PresetCalls));
    }

    [Fact]
    public void SetUnit_WithAndWithoutName_MatchesUiForms()
    {
        var ctl = new RecordingControl();
        AssertEnvelopeOk(Parse(CommandProcessor.ProcessLine("{\"cmd\":\"setunit\",\"channel\":\"flow\",\"name\":\"lpm\"}", ctl)), "setunit");
        Assert.Equal(("flow", "lpm"), Assert.Single(ctl.UnitCalls));

        // clear form: UI omits "name" entirely ⇒ engine sees null ⇒ follow preset
        AssertEnvelopeOk(Parse(CommandProcessor.ProcessLine("{\"cmd\":\"setunit\",\"channel\":\"flow\"}", ctl)), "setunit");
        Assert.Equal(2, ctl.UnitCalls.Count);
        Assert.Null(ctl.UnitCalls[1].Unit);
    }

    // ── profile / vars / ports / adapters (lists the UI renders) ──────────

    [Fact]
    public void Profile_Command_CarriesName()
    {
        var ctl = new RecordingControl();
        AssertEnvelopeOk(Parse(CommandProcessor.ProcessLine("{\"cmd\":\"profile\",\"name\":\"cms\"}", ctl)), "profile");
        Assert.Equal((null, "cms"), Assert.Single(ctl.ProfileCalls));
    }

    [Fact]
    public void Vars_Shape_HasComAndLanVariablesArrays_ForUiParsing()
    {
        var ctl = new RecordingControl();
        var root = Parse(CommandProcessor.ProcessLine("{\"cmd\":\"vars\"}", ctl));
        AssertEnvelopeOk(root, "vars");
        var data = root.GetProperty("data");
        Assert.True(data.TryGetProperty("com", out var com), "vars data missing 'com'");
        Assert.True(com.TryGetProperty("variables", out var cvars), "vars.com missing 'variables'");
        Assert.True(data.TryGetProperty("lan", out var lan), "vars data missing 'lan'");
        Assert.True(lan.TryGetProperty("variables", out var lvars), "vars.lan missing 'variables'");
        Assert.Equal("V", cvars[0].GetProperty("name").GetString());
        Assert.Equal("0x00000001", lvars[0].GetProperty("sv").GetString());
    }

    [Fact]
    public void Ports_Adapters_Shapes_HaveNameField_ForUiParsing()
    {
        var ctl = new RecordingControl();

        var ports = Parse(CommandProcessor.ProcessLine("{\"cmd\":\"ports\"}", ctl));
        AssertEnvelopeOk(ports, "ports");
        Assert.Equal("COM2", ports.GetProperty("data").GetProperty("ports")[0].GetProperty("name").GetString());

        var adapters = Parse(CommandProcessor.ProcessLine("{\"cmd\":\"adapters\"}", ctl));
        AssertEnvelopeOk(adapters, "adapters");
        Assert.Equal("fake0", adapters.GetProperty("data").GetProperty("adapters")[0].GetProperty("name").GetString());
    }

    // ── variable edit (SetVar/ClearVar with LAN sv disambiguation) ────────

    [Fact]
    public void Set_WithSv_StringHex_ParsedAsUint()
    {
        var ctl = new RecordingControl();
        var root = Parse(CommandProcessor.ProcessLine(
            "{\"cmd\":\"set\",\"channel\":\"lan\",\"name\":\"S\",\"value\":1.5,\"sv\":\"0x00000001\"}", ctl));
        AssertEnvelopeOk(root, "set");
        Assert.Equal(("lan", "S", 1.5, 1u), Assert.Single(ctl.SetCalls));
    }

    [Fact]
    public void Clear_WithoutSv_PassesNull()
    {
        var ctl = new RecordingControl();
        AssertEnvelopeOk(Parse(CommandProcessor.ProcessLine(
            "{\"cmd\":\"clear\",\"channel\":\"com\",\"name\":\"V\"}", ctl)), "clear");
        Assert.Equal(("com", "V", null), Assert.Single(ctl.ClearCalls));
    }

    // ── status (the 1 Hz poll payload the snapshot parser consumes) ───────

    [Fact]
    public void Status_Shape_MatchesEmuSnapshotParser()
    {
        var ctl = new RecordingControl();
        var root = Parse(CommandProcessor.ProcessLine("{\"cmd\":\"status\"}", ctl));
        AssertEnvelopeOk(root, "status");

        var data = root.GetProperty("data");
        Assert.True(data.TryGetProperty("proto", out var proto) && proto.GetInt32() >= 1,
            "W11.10: status must report the pipe protocol version");

        // keys FetchStatus() reads:
        foreach (var k in new[] { "uptimeSec", "pid", "com", "lan", "units" })
            Assert.True(data.TryGetProperty(k, out _), $"status data missing '{k}'");

        var com = data.GetProperty("com");
        foreach (var k in new[] { "streaming", "emulating", "port", "template", "linesSent", "linesPerSec" })
            Assert.True(com.TryGetProperty(k, out _), $"status.com missing '{k}'");

        var lan = data.GetProperty("lan");
        foreach (var k in new[] { "streaming", "emulating", "adapter", "equipment", "framesSent", "framesPerSec", "svCount", "firmwareSv" })
            Assert.True(lan.TryGetProperty(k, out _), $"status.lan missing '{k}'");

        var units = data.GetProperty("units");
        foreach (var k in new[] { "preset", "flowOverride", "temperatureOverride", "densityOverride", "volumeOverride", "levelOverride", "pressureOverride" })
            Assert.True(units.TryGetProperty(k, out _), $"status.units missing '{k}'");

        // P2 2026-08-26 (owner decree «дефолт — всё выключено»): a freshly
        // started engine reports BOTH channels fully passive — the UI renders
        // every switch OFF until the operator acts.
        Assert.False(com.GetProperty("streaming").GetBoolean(), "default streaming.com must be OFF");
        Assert.False(com.GetProperty("emulating").GetBoolean(), "default emulating.com must be OFF");
        Assert.False(lan.GetProperty("streaming").GetBoolean(), "default streaming.lan must be OFF");
        Assert.False(lan.GetProperty("emulating").GetBoolean(), "default emulating.lan must be OFF");
    }

    // ── malformed input never throws (stand law) ──────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{\"value\":1}")]
    [InlineData("frobnicate")]
    public void Malformed_Always_YieldsOkFalseEnvelope(string line)
    {
        var root = Parse(CommandProcessor.ProcessLine(line, new RecordingControl()));
        AssertEnvelopeError(root);
    }
}
