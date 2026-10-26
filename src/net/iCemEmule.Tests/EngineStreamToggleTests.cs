// EngineStreamToggleTests — P4 2026-08-27, owner decree
// «нужно иметь возможность включать\выключать поток данных»: pins the
// engine's RUNTIME stream on/off against the REAL EmuEngineHost with FAKE
// emitters (no hardware, no disk writes), while the passive launch (P2,
// EngineDefaultStateTests) stays intact. Wire forms mirror the WinUI exactly
// (JSON grammar, CommandProcessor) — no parity pin is weakened here.

using System.Text.Json;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Emitters;
using Xunit;

namespace iCemEmule.Tests;

public class EngineStreamToggleTests
{
    // ── fakes (the engine's own emitter contracts, no hardware) ──────────
    private sealed class FakeComEmitter : IComEmitter
    {
        public bool IsOpen { get; private set; }
        public string? PortName { get; private set; }
        public long LinesSent { get; private set; }
        public string? OpenError;
        public List<string> OpenCalls { get; } = new();
        public int CloseCalls { get; private set; }

        // interface contract; the engine under test never subscribes
        public event Action<string>? StatusChanged { add { } remove { } }

        public string Open(string portName)
        {
            OpenCalls.Add(portName);
            if (OpenError is not null)
                return OpenError;
            IsOpen = true;
            PortName = portName;
            return "";
        }

        public void SendLine(string line) => LinesSent++;
        public void Close() { CloseCalls++; IsOpen = false; PortName = null; }
        public void Dispose() => Close();
    }

    private sealed class FakeLanEmitter : IAceLanEmitter
    {
        public bool IsOpen { get; private set; }
        public string? AdapterName { get; private set; }
        public long FramesSent { get; private set; }
        public List<string> OpenCalls { get; } = new();
        public int CloseCalls { get; private set; }

        // interface contract; the engine under test never subscribes
        public event Action<string>? StatusChanged { add { } remove { } }

        public static IReadOnlyList<string> ListAdapters() => new[] { "fake0" };

        public string Open(string adapterDescriptionOrName)
        {
            OpenCalls.Add(adapterDescriptionOrName);
            IsOpen = true;
            AdapterName = adapterDescriptionOrName;
            return "";
        }

        public void SendSuperframe(IReadOnlyList<AceVectorData> vectors, uint equipmentNumber, string srcMac)
            => FramesSent += vectors.Count;

        public void Close() { CloseCalls++; IsOpen = false; AdapterName = null; }
        public void Dispose() => Close();
    }

    private static (EmuEngineHost Host, EmuSettings Settings, FakeComEmitter Com, FakeLanEmitter Lan)
        Build(EmuSettings? settings = null)
    {
        var s = settings ?? new EmuSettings();
        var com = new FakeComEmitter();
        var lan = new FakeLanEmitter();
        return (new EmuEngineHost(s, com, lan), s, com, lan);
    }

    private static JsonElement Reply(EmuEngineHost host, string command)
    {
        var r = CommandProcessor.ProcessLine(command, host);
        var doc = JsonDocument.Parse(r.Json);
        return doc.RootElement.Clone();
    }

    private static JsonElement Ok(EmuEngineHost host, string command)
    {
        var root = Reply(host, command);
        Assert.True(root.GetProperty("ok").GetBoolean(), $"command '{command}' refused: {root.GetRawText()}");
        return root;
    }

    private static JsonElement Refused(EmuEngineHost host, string command)
    {
        var root = Reply(host, command);
        Assert.False(root.GetProperty("ok").GetBoolean(), $"command '{command}' unexpectedly succeeded: {root.GetRawText()}");
        return root;
    }

    private static Dictionary<string, object?> ChannelStatus(EmuEngineHost host, string channel)
        => (Dictionary<string, object?>)((Dictionary<string, object?>)host.GetStatus()[channel]!)!;

    // ── (a) passive launch: nothing streams, nothing was ever opened ─────

    [Fact]
    public void PassiveLaunch_StatusReportsBothChannelsOff_AndEmittersUntouched()
    {
        var (host, _, com, lan) = Build();

        var root = Ok(host, "{\"cmd\":\"status\"}");
        var data = root.GetProperty("data");
        var comEl = data.GetProperty("com");
        var lanEl = data.GetProperty("lan");

        Assert.False(comEl.GetProperty("streaming").GetBoolean(), "COM must not stream at launch");
        Assert.False(comEl.GetProperty("portOpen").GetBoolean(), "no COM port may be bound at launch");
        Assert.False(comEl.GetProperty("emulating").GetBoolean(), "COM emulation must be OFF at launch");
        Assert.False(lanEl.GetProperty("streaming").GetBoolean(), "LAN must not stream at launch");
        Assert.False(lanEl.GetProperty("adapterOpen").GetBoolean(), "no LAN adapter may be bound at launch");
        Assert.False(lanEl.GetProperty("emulating").GetBoolean(), "LAN emulation must be OFF at launch");

        // P4: passive means the engine never even TRIES to open an endpoint.
        Assert.Empty(com.OpenCalls);
        Assert.Empty(lan.OpenCalls);
    }

    // ── (b) start after a configured endpoint actually begins streaming ──

    [Fact]
    public void StartCom_AfterConfiguredPort_BeginsStreaming()
    {
        // ComPort pre-set = the engine-visible result of `setport COM9`
        // (SetPort writes the same settings field; the setport wire path is
        // pinned by CommandProcessorParityTests).
        var (host, settings, com, lan) = Build();
        settings.ComPort = "COM9";

        var root = Ok(host, "{\"cmd\":\"start\",\"channel\":\"com\"}");
        var data = root.GetProperty("data");
        Assert.True(data.GetProperty("streaming").GetBoolean());
        Assert.Equal("COM9", data.GetProperty("port").GetString());

        Assert.True(com.IsOpen, "engine must open the configured COM port on start");
        Assert.Equal("COM9", Assert.Single(com.OpenCalls));

        var st = ChannelStatus(host, "com");
        Assert.True((bool)st["streaming"]!, "status must honestly report streaming=true after start");
        Assert.True((bool)st["portOpen"]!);
    }

    [Fact]
    public void StartLan_AfterConfiguredAdapter_BeginsStreaming()
    {
        var (host, settings, _, lan) = Build();
        settings.LanAdapter = "fake0";

        var root = Ok(host, "{\"cmd\":\"start\",\"channel\":\"lan\"}");
        Assert.True(root.GetProperty("data").GetProperty("streaming").GetBoolean());

        Assert.True(lan.IsOpen, "engine must open the configured LAN adapter on start");
        Assert.Equal("fake0", Assert.Single(lan.OpenCalls));

        var st = ChannelStatus(host, "lan");
        Assert.True((bool)st["streaming"]!, "status must honestly report streaming=true after start");
        Assert.True((bool)st["adapterOpen"]!);
    }

    // ── (c) stop stops it and closes the endpoint ────────────────────────

    [Fact]
    public void StopCom_StopsStreaming_AndClosesPort()
    {
        var (host, settings, com, _) = Build();
        settings.ComPort = "COM9";
        Ok(host, "{\"cmd\":\"start\",\"channel\":\"com\"}");

        var root = Ok(host, "{\"cmd\":\"stop\",\"channel\":\"com\"}");
        var data = root.GetProperty("data");
        Assert.False(data.GetProperty("streaming").GetBoolean());
        Assert.True(data.GetProperty("wasStreaming").GetBoolean());

        Assert.False(com.IsOpen, "stop must close the COM port");
        Assert.Equal(1, com.CloseCalls);

        var st = ChannelStatus(host, "com");
        Assert.False((bool)st["streaming"]!, "status must honestly report streaming=false after stop");
        Assert.False((bool)st["portOpen"]!);
    }

    [Fact]
    public void StopLan_StopsStreaming_AndClosesAdapter()
    {
        var (host, settings, _, lan) = Build();
        settings.LanAdapter = "fake0";
        Ok(host, "{\"cmd\":\"start\",\"channel\":\"lan\"}");

        var root = Ok(host, "{\"cmd\":\"stop\",\"channel\":\"lan\"}");
        Assert.False(root.GetProperty("data").GetProperty("streaming").GetBoolean());

        Assert.False(lan.IsOpen, "stop must close the LAN adapter");
        Assert.Equal(1, lan.CloseCalls);

        Assert.False((bool)ChannelStatus(host, "lan")["streaming"]!);
    }

    // ── (d) start without an endpoint → the EXACT mapped refusal text ────

    [Fact]
    public void Start_WithoutEndpoint_RefusesWithExactMappedText()
    {
        var (host, _, com, lan) = Build();   // fresh settings: no port, no adapter

        var comReply = Refused(host, "{\"cmd\":\"start\",\"channel\":\"com\"}");
        Assert.Equal(EmuEngineHost.ErrNoComPort, comReply.GetProperty("error").GetString());

        var lanReply = Refused(host, "{\"cmd\":\"start\",\"channel\":\"lan\"}");
        Assert.Equal(EmuEngineHost.ErrNoLanAdapter, lanReply.GetProperty("error").GetString());

        // a refused start must not leave any gate or endpoint behind
        Assert.Empty(com.OpenCalls);
        Assert.Empty(lan.OpenCalls);
        Assert.False((bool)ChannelStatus(host, "com")["streaming"]!);
        Assert.False((bool)ChannelStatus(host, "lan")["streaming"]!);
    }

    [Fact]
    public void Start_FailedEmitterOpen_RefusalPassthrough_GateStaysOff()
    {
        var (host, settings, com, _) = Build();
        settings.ComPort = "COM9";
        com.OpenError = "Access denied to COM9. Port may be in use by another application.";

        var root = Refused(host, "{\"cmd\":\"start\",\"channel\":\"com\"}");
        // vendor/OS open failures are passthrough (the UI renders them as the
        // generic note) — the gate must NOT be raised on a failed start.
        Assert.Equal("Access denied to COM9. Port may be in use by another application.",
            root.GetProperty("error").GetString());
        Assert.False(settings.ComEnabled, "a failed start must not raise the persisted gate");
        Assert.False(com.IsOpen);
        Assert.False((bool)ChannelStatus(host, "com")["streaming"]!);
    }

    // ── master-switch semantics: `start` raises the runtime gate; launch
    //    defaults stay OFF; the persisted flag is a mirror ───────────────

    [Fact]
    public void Start_RaisesRuntimeGateAndPersistedFlag_StopLowersThem()
    {
        var (host, settings, _, _) = Build();
        Assert.False(settings.ComEnabled);
        Assert.False(settings.LanEnabled);

        settings.ComPort = "COM9";
        settings.LanAdapter = "fake0";

        Ok(host, "{\"cmd\":\"start\",\"channel\":\"com\"}");
        Assert.True(settings.ComEnabled, "start com must raise the persisted ComEnabled mirror");

        Ok(host, "{\"cmd\":\"start\",\"channel\":\"lan\"}");
        Assert.True(settings.LanEnabled, "start lan must raise the persisted LanEnabled mirror");

        Ok(host, "{\"cmd\":\"stop\",\"channel\":\"com\"}");
        Assert.False(settings.ComEnabled, "stop com must lower the persisted mirror");

        Ok(host, "{\"cmd\":\"stop\",\"channel\":\"lan\"}");
        Assert.False(settings.LanEnabled, "stop lan must lower the persisted mirror");
    }

    [Fact]
    public void Start_WhileStreaming_IsIdempotent_AlreadyReply()
    {
        var (host, settings, com, _) = Build();
        settings.ComPort = "COM9";

        Ok(host, "{\"cmd\":\"start\",\"channel\":\"com\"}");
        var root = Ok(host, "{\"cmd\":\"start\",\"channel\":\"com\"}");

        Assert.True(root.GetProperty("data").GetProperty("already").GetBoolean(),
            "second start must report already=true");
        Assert.True(root.GetProperty("data").GetProperty("streaming").GetBoolean());
        Assert.Single(com.OpenCalls);   // the port is opened once, not twice
    }
}
