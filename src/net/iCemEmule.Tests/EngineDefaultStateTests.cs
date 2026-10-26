// EngineDefaultStateTests — P2 2026-08-26, owner decree
// «дефолт у эмулятора — всё выключено lan\com»: the engine starts PASSIVE.
// Pins, at three layers:
//   1) EmuSettings fresh-object defaults (fresh install / missing elements);
//   2) EmuEngineHost construction state (GetStatus dictionary);
//   3) the `status` pipe reply as the UI/CLI actually receive it.
// The host is CONSTRUCTED only — no RunInternal, no timer, no pipe bind,
// no ports touched (ctor starts nothing by design).

using System.Text.Json;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Core;
using Xunit;

namespace iCemEmule.Tests;

public class EngineDefaultStateTests
{
    [Fact]
    public void FreshSettings_Default_ToEverythingOff()
    {
        var s = new EmuSettings();
        Assert.False(s.ComEnabled);
        Assert.False(s.LanEnabled);
        Assert.False(s.ComEmuleEnabled, "COM emulation must default OFF (owner decree)");
        Assert.False(s.LanEmuleEnabled, "LAN emulation must default OFF (owner decree)");
    }

    [Fact]
    public void ConstructedEngine_IsFullyPassive_StatusDictionaryAllOff()
    {
        // Fresh settings = what a clean install loads (no emule_config.xml).
        // NOTE: construction starts nothing (no timer/pipe/port) — no dispose
        // needed; EmuEngineHost is deliberately not IDisposable.
        var host = new EmuEngineHost(new EmuSettings());
        var st = host.GetStatus();

        var com = (Dictionary<string, object?>)st["com"]!;
        var lan = (Dictionary<string, object?>)st["lan"]!;

        Assert.False((bool)com["streaming"]!, "COM stream must be OFF at start");
        Assert.False((bool)com["emulating"]!, "COM emulation must be OFF at start");
        Assert.False((bool)com["portOpen"]!, "no COM port may be bound at start");

        Assert.False((bool)lan["streaming"]!, "LAN stream must be OFF at start");
        Assert.False((bool)lan["emulating"]!, "LAN emulation must be OFF at start");
        Assert.False((bool)lan["adapterOpen"]!, "no LAN adapter may be bound at start");
    }

    [Fact]
    public void StatusReply_ReflectsOffDefaults_Honestly()
    {
        var host = new EmuEngineHost(new EmuSettings());
        var reply = CommandProcessor.ProcessLine("status", host);

        using var doc = JsonDocument.Parse(reply.Json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());

        var data = root.GetProperty("data");
        var com = data.GetProperty("com");
        var lan = data.GetProperty("lan");

        Assert.False(com.GetProperty("streaming").GetBoolean());
        Assert.False(com.GetProperty("emulating").GetBoolean());
        Assert.False(lan.GetProperty("streaming").GetBoolean());
        Assert.False(lan.GetProperty("emulating").GetBoolean());
        Assert.False(lan.GetProperty("firmwareSv").GetBoolean());
    }

    [Fact]
    public void Operator_CanStillTurnChannelsOn_ViaPipeCommands()
    {
        // The decree changes DEFAULTS, not the control surface: setemulate +
        // start must keep working exactly as before (start needs a configured
        // port/adapter, so here we pin the emulation gate + honest port error).
        var host = new EmuEngineHost(new EmuSettings());

        var on = CommandProcessor.ProcessLine("{\"cmd\":\"setemulate\",\"channel\":\"com\",\"value\":1}", host);
        using (var doc = JsonDocument.Parse(on.Json))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), on.Json);
            Assert.True(doc.RootElement.GetProperty("data").GetProperty("emulating").GetBoolean());
        }
        Assert.True(host.GetStatus()["com"] is Dictionary<string, object?> com &&
                    (bool)com["emulating"]!, "setemulate must flip the runtime gate");

        // back off — freeze law intact
        var off = CommandProcessor.ProcessLine("{\"cmd\":\"setemulate\",\"channel\":\"com\",\"value\":0}", host);
        using (var doc = JsonDocument.Parse(off.Json))
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), off.Json);
        Assert.False((bool)((Dictionary<string, object?>)host.GetStatus()["com"]!)["emulating"]!);

        // start without a configured port = honest refusal (never a silent bind)
        var start = CommandProcessor.ProcessLine("{\"cmd\":\"start\",\"channel\":\"com\"}", host);
        using (var doc = JsonDocument.Parse(start.Json))
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), "start with no port must fail honestly");
    }
}
