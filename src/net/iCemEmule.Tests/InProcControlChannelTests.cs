// InProcControlChannelTests — P5 2026-08-28, the ROOT-CAUSE fix for the
// owner's «НЕ РАБОТАЮТ ПЕРЕКЛЮЧАТЕЛИ» (2026-08-28): with the legacy
// iCemEmule.exe TrayApp ALSO running, TWO servers listen on
// \\.\pipe\iCemEmule.Control and a client connection lands on a RANDOM
// engine (live stand repro: 8 CLI `status` calls split 4/4 between two pids).
// The merged app now routes UI commands through ControlPipeClient.
// LocalExecutor — the SAME JSON wire line executed against the IN-PROC
// engine. Pins:
//   (1) LocalExecutor routing: the wire line reaches the executor, the reply
//       JSON is parsed by the same envelope parser (VM contract unchanged);
//   (2) LocalExecutor precedence over the pipe name (in-proc IS the target);
//   (3) degradation law: a throwing executor ⇒ null, never an exception;
//   (4) with no executor and an unreachable pipe ⇒ null (existing contract);
//   (5) GRAMMAR PARITY: the EXACT JSON the WinUI (EmulatorViewModel) builds
//       per switch, executed in-proc through the shared seam, produces the
//       same engine-visible effects as the CLI text forms — i.e. the UI and
//       the CLI drive ONE engine through ONE command language. This is the
//       contract pin the owner asked for («VM-команды вызывают те же методы
//       движка, что CLI»).
// NOTE: tests here call persisting commands (setport/setemulate/...) ⇒ they
// MUST pin EmuSettings.SettingsPathOverride to a temp path (never the
// stand's emule_config.xml; 2026-08-31: canonical path moved from the legacy
// %LOCALAPPDATA% settings.xml to ProgramData). Both SettingsPathOverride and
// ControlPipeClient.LocalExecutor are PROCESS-GLOBAL statics — every test
// class touching them must live in the SAME (serialized) xunit collection,
// otherwise a parallel class's SendCommand gets routed to this class's
// executor (first observed as a flaky EmulatorAppTests failure).

using System.Text.Json;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Core;
using Xunit;

namespace iCemEmule.Tests;

[Collection("EmulatorControlStatics")]
public class InProcControlChannelTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), $"iCemEmule.Tests.{Guid.NewGuid():N}", "emule_config.xml");

    public InProcControlChannelTests()
    {
        EmuSettings.SettingsPathOverride = _settingsPath;
    }

    public void Dispose()
    {
        EmuSettings.SettingsPathOverride = null;
        try { Directory.Delete(Path.GetDirectoryName(_settingsPath)!, recursive: true); }
        catch { }
    }

    // ── (1) LocalExecutor routing ──────────────────────────────────────────

    [Fact]
    public void LocalExecutor_ReceivesTheWireLine_AndReplyParsesThroughTheSharedEnvelope()
    {
        var wireLines = new List<string>();
        ControlPipeClient.LocalExecutor = line =>
        {
            wireLines.Add(line);
            return CommandProcessor.ProcessLine(line, new FakeEchoControl());
        };
        try
        {
            using var reply = ControlPipeClient.Cmd("status");

            Assert.NotNull(reply);
            Assert.True(reply!.Ok);
            Assert.Equal("status", reply.Cmd);
            Assert.True(reply.Raw.RootElement.TryGetProperty("data", out _));

            // the SAME JSON the pipe server would have received
            var line = Assert.Single(wireLines);
            Assert.Equal("{\"cmd\":\"status\"}", line);
        }
        finally
        {
            ControlPipeClient.LocalExecutor = null;
        }
    }

    [Fact]
    public void LocalExecutor_TakesPrecedenceOverThePipeName()
    {
        // The in-proc engine IS the target: even with a bogus pipe name the
        // executor answers (this is what kills the two-server cross-talk).
        ControlPipeClient.LocalExecutor = line => CommandProcessor.ProcessLine(line, new FakeEchoControl());
        try
        {
            using var reply = ControlPipeClient.SendCommand(
                "{\"cmd\":\"status\"}", pipeName: "iCemEmule.Tests.NoSuchPipe");
            Assert.NotNull(reply);
            Assert.True(reply!.Ok);
        }
        finally
        {
            ControlPipeClient.LocalExecutor = null;
        }
    }

    // ── (3)/(4) degradation law ─────────────────────────────────────────────

    [Fact]
    public void ThrowingLocalExecutor_DegradesToNull_NeverThrows()
    {
        ControlPipeClient.LocalExecutor = _ => throw new InvalidOperationException("boom");
        try
        {
            Assert.Null(ControlPipeClient.Cmd("status"));
        }
        finally
        {
            ControlPipeClient.LocalExecutor = null;
        }
    }

    [Fact]
    public void NullLocalExecutor_UnreachablePipe_DegradesToNull()
    {
        ControlPipeClient.LocalExecutor = null;   // explicit: the pipe path
        using var reply = ControlPipeClient.SendCommand(
            "{\"cmd\":\"status\"}", pipeName: "iCemEmule.Tests.DefinitelyMissingPipe");
        Assert.Null(reply);
    }

    // ── (5) grammar parity: the VM's EXACT JSON, executed in-proc, drives
    //        the engine exactly like the CLI ────────────────────────────────

    private static (EmuEngineHost Host, EmuSettings Settings, FakeComEmitter Com, FakeLanEmitter Lan)
        Build()
    {
        var s = new EmuSettings();
        var com = new FakeComEmitter();
        var lan = new FakeLanEmitter();
        return (new EmuEngineHost(s, com, lan), s, com, lan);
    }

    private static ControlPipeClient.Reply? InProc(EmuEngineHost host, string wireLine)
    {
        // the wiring App.OnLaunched performs (the seam under test):
        ControlPipeClient.LocalExecutor = line => host.Execute(line);
        return ControlPipeClient.SendCommand(wireLine);
    }

    [Fact]
    public void VmToggleComJson_ThroughInProcExecutor_StreamsExactlyLikeTheCli()
    {
        var (host, settings, com, _) = Build();
        try
        {
            // the VM's SetPortCommand wire form, then ToggleComCommand (targetOn=true)
            using (var port = InProc(host, "{\"cmd\":\"setport\",\"name\":\"COM9\"}"))
                Assert.True(port!.Ok, port.Raw.RootElement.GetRawText());
            Assert.Equal("COM9", settings.ComPort);

            using (var start = InProc(host, "{\"cmd\":\"start\",\"channel\":\"com\"}"))
            {
                Assert.True(start!.Ok);
                var data = start.Raw.RootElement.GetProperty("data");
                Assert.True(data.GetProperty("streaming").GetBoolean());
            }
            Assert.True(com.IsOpen);
            Assert.True((bool)((Dictionary<string, object?>)host.GetStatus()["com"]!)["streaming"]!);

            // ToggleComCommand (targetOn=false)
            using (var stop = InProc(host, "{\"cmd\":\"stop\",\"channel\":\"com\"}"))
            {
                Assert.True(stop!.Ok);
                Assert.False(stop.Raw.RootElement.GetProperty("data").GetProperty("streaming").GetBoolean());
            }
            Assert.False(com.IsOpen);
        }
        finally
        {
            ControlPipeClient.LocalExecutor = null;
        }
    }

    [Fact]
    public void VmRefusals_SurfaceThroughTheInProcExecutor_WithTheStableText()
    {
        var (host, _, com, lan) = Build();   // no endpoint configured
        try
        {
            using var start = InProc(host, "{\"cmd\":\"start\",\"channel\":\"com\"}");
            Assert.False(start!.Ok);
            Assert.Equal(EmuEngineHost.ErrNoComPort, start.Error);

            using var lanStart = InProc(host, "{\"cmd\":\"start\",\"channel\":\"lan\"}");
            Assert.False(lanStart!.Ok);
            Assert.Equal(EmuEngineHost.ErrNoLanAdapter, lanStart.Error);

            Assert.Empty(com.OpenCalls);
            Assert.Empty(lan.OpenCalls);
        }
        finally
        {
            ControlPipeClient.LocalExecutor = null;
        }
    }

    [Fact]
    public void VmEndpointAndUnitsJson_ThroughInProcExecutor_PersistsEngineState()
    {
        var (host, settings, _, _) = Build();
        try
        {
            using (var r = InProc(host, "{\"cmd\":\"setadapter\",\"name\":\"fake0\"}"))
                Assert.True(r!.Ok, r.Raw.RootElement.GetRawText());
            Assert.Equal("fake0", settings.LanAdapter);

            using (var r = InProc(host, "{\"cmd\":\"setequipment\",\"value\":11038534}"))
                Assert.True(r!.Ok, r.Raw.RootElement.GetRawText());
            Assert.Equal(0x00A86F46u, settings.LanEquipment);

            using (var r = InProc(host, "{\"cmd\":\"setfirmware\",\"value\":1}"))
                Assert.True(r!.Ok, r.Raw.RootElement.GetRawText());
            Assert.True(settings.LanFirmwareSvEnabled);
            // NOTE: status.lan.firmwareSv mirrors _baseProvider.IncludeFirmwareTables,
            // which the engine refreshes on its 1 Hz Tick — the PERSISTED flag
            // (asserted above) is the immediate truth; the status follows on the
            // next tick (no tick runs in this test).

            using (var r = InProc(host, "{\"cmd\":\"setemulate\",\"channel\":\"com\",\"value\":1}"))
                Assert.True(r!.Ok, r.Raw.RootElement.GetRawText());
            Assert.True(settings.ComEmuleEnabled);
            Assert.True((bool)((Dictionary<string, object?>)host.GetStatus()["com"]!)["emulating"]!);
            using (var r = InProc(host, "{\"cmd\":\"setemulate\",\"channel\":\"lan\",\"value\":0}"))
                Assert.True(r!.Ok, r.Raw.RootElement.GetRawText());
            Assert.False(settings.LanEmuleEnabled);

            using (var r = InProc(host, "{\"cmd\":\"setpreset\",\"value\":2}"))
                Assert.True(r!.Ok, r.Raw.RootElement.GetRawText());
            Assert.Equal("Metric", ((Dictionary<string, object?>)host.GetStatus()["units"]!)["preset"]);

            using (var r = InProc(host, "{\"cmd\":\"setunit\",\"channel\":\"flow\",\"name\":\"gpm\"}"))
                Assert.True(r!.Ok, r.Raw.RootElement.GetRawText());
            Assert.Equal("gpm", settings.UnitFlowOverride);
            using (var r = InProc(host, "{\"cmd\":\"setunit\",\"channel\":\"flow\"}"))
                Assert.True(r!.Ok, r.Raw.RootElement.GetRawText());   // clear form (no name)
            Assert.Equal("", settings.UnitFlowOverride);

            using (var r = InProc(host, "{\"cmd\":\"profile\",\"name\":\"flecs 4.53\"}"))
                Assert.True(r!.Ok, r.Raw.RootElement.GetRawText());
            Assert.Equal("flecs 4.53", ((Dictionary<string, object?>)host.GetStatus()["com"]!)["template"]);
        }
        finally
        {
            ControlPipeClient.LocalExecutor = null;
        }
    }

    [Fact]
    public void VmVarEditJson_ThroughInProcExecutor_SetsAndClearsOverrides()
    {
        var (host, _, _, _) = Build();
        try
        {
            // EmuVarEdit(Channel="com", Sv=null, Name, Value) wire form.
            // Variable names on the wire = the flecs ALIASES (ground truth:
            // live `vars` — "Recirc Density" = registry "SlurryDensity").
            using var set = InProc(host, "{\"cmd\":\"set\",\"channel\":\"com\",\"name\":\"Recirc Density\",\"value\":15.5}");
            Assert.True(set!.Ok, set.Raw.RootElement.GetRawText());

            // The engine's AUTHORITATIVE reply echoes the applied override —
            // the exact payload the VM's command note renders. (status.com.values
            // fills only after the first engine tick seeds LastValues — no tick
            // runs in this test, so the status render is asserted elsewhere.)
            var applied = set.Raw.RootElement.GetProperty("data").GetProperty("applied");
            Assert.Equal("com", applied[0].GetProperty("channel").GetString());
            Assert.Equal("Recirc Density", applied[0].GetProperty("name").GetString());

            // EmuVarEdit clear form
            using var clear = InProc(host, "{\"cmd\":\"clear\",\"channel\":\"com\",\"name\":\"Recirc Density\"}");
            Assert.True(clear!.Ok, clear.Raw.RootElement.GetRawText());
        }
        finally
        {
            ControlPipeClient.LocalExecutor = null;
        }
    }

    [Fact]
    public void VmPollsJson_ThroughInProcExecutor_StreamsFromTheInProcEngine()
    {
        var (host, _, _, _) = Build();
        try
        {
            using var status = InProc(host, "{\"cmd\":\"status\"}");
            Assert.True(status!.Ok);
            var data = status.Raw.RootElement.GetProperty("data");
            Assert.Equal(Environment.ProcessId, data.GetProperty("pid").GetInt32());
            Assert.Equal(1, data.GetProperty("proto").GetInt32());
        }
        finally
        {
            ControlPipeClient.LocalExecutor = null;
        }
    }

    /// <summary>A minimal IEmuControl for the envelope-routing pins: honest
    /// status reply, no engine.</summary>
    private sealed class FakeEchoControl : IEmuControl
    {
        public Dictionary<string, object?> GetStatus() => new() { ["ok"] = true };
        public ControlResult StartChannel(string channel) => ControlResult.Fail("fake");
        public ControlResult StopChannel(string channel) => ControlResult.Success();
        public ControlResult SetVariable(string? channel, string name, double value, uint? sv) => ControlResult.Fail("fake");
        public ControlResult ClearVariable(string? channel, string name, uint? sv) => ControlResult.Fail("fake");
        public ControlResult GetVariable(string? channel, string name, uint? sv) => ControlResult.Fail("fake");
        public ControlResult GetProfiles() => ControlResult.Success();
        public ControlResult SetProfile(string? deviceType, string name) => ControlResult.Fail("fake");
        public ControlResult GetVariables(string? channel) => ControlResult.Success();
        public ControlResult GetPorts() => ControlResult.Success();
        public ControlResult GetAdapters() => ControlResult.Success();
        public ControlResult SetPort(string port) => ControlResult.Fail("fake");
        public ControlResult SetAdapter(string adapter) => ControlResult.Fail("fake");
        public ControlResult SetEquipment(uint equipment) => ControlResult.Fail("fake");
        public ControlResult SetFirmware(bool on) => ControlResult.Fail("fake");
        public ControlResult SetEmulation(string channel, bool on) => ControlResult.Fail("fake");
        public ControlResult SetPreset(int presetIndex) => ControlResult.Fail("fake");
        public ControlResult SetUnit(string group, string? unit) => ControlResult.Fail("fake");
        public void RequestQuit() { }
    }
}
