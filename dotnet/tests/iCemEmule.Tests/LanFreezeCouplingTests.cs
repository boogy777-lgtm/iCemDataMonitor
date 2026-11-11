// LanFreezeCouplingTests — 2026-08-30 stand investigation «LAN values frozen
// in iCem while the emulator streams 95 pairs every poll». Evidence: bus regs
// 200..399 byte-identical for minutes (DisplayTemperature=119.991234 frozen,
// CountInput1=5790 frozen) while COM flowed live through the same AddIn.
//
// Verdict pinned here (wire law, W14):
//   · The LAN generator is NOT static: with the emulation gate ON the DFB
//     network executes every tick and the superframe bytes change every tick
//     (RawIo.CountIn = elapsed×10, AnalogIn per-tick noise, temperature lag).
//   · The W14 freeze gate (FrozenPayloadProvider, EmuEngineHost.cs:87-94,
//     102-104) holds the LAST superframe when emulation is OFF while the
//     stream stays ON — byte-identical frames at 1 Hz = the exact stand
//     signature. `start lan` does NOT raise the emulation gate (the two are
//     independent: EmuEngineHost.StartChannel vs SetEmulation).
//   · Manual UI/CLI edits (`set lan …`) reach the wire even while frozen —
//     ManualOverridePayloadProvider patches the cloned payload after the
//     frozen cache (FrozenPayloadProvider.cs:5-7, EmuEngineHost.cs:535-545).
//
// Tests mirror the engine's chain (EmuEngineHost.cs:87-94) and tick law
// (EmuEngineHost.cs:271-311) 1:1 — no host timer, no hardware.

using System.Text.Json;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Dfb;
using Xunit;

namespace iCemEmule.Tests;

[Collection("EmulatorControlStatics")]
public class LanFreezeCouplingTests : IDisposable
{
    private const uint RawIoSv = 0x13120209;
    private const uint Equipment = 0x00A86F46;

    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), $"iCemEmule.Tests.{Guid.NewGuid():N}", "emule_config.xml");

    public LanFreezeCouplingTests()
    {
        // setemulate persists settings — never clobber the stand's emule_config.xml
        EmuSettings.SettingsPathOverride = _settingsPath;
    }

    public void Dispose()
    {
        EmuSettings.SettingsPathOverride = null;
        try { Directory.Delete(Path.GetDirectoryName(_settingsPath)!, recursive: true); }
        catch { }
    }

    /// <summary>The engine's LAN provider chain, 1:1 with EmuEngineHost ctor.</summary>
    private static (DfbNetwork? Net, ManualOverridePayloadProvider Overrides, FrozenPayloadProvider Frozen, UnitConvertingPayloadProvider Outer) BuildChain()
    {
        var baseProvider = new AcePayloadProvider();
        var net = DfbEngineBridge.TryCreateNetwork();
        var dfbProvider = net is not null
            ? DfbEngineBridge.Decorate(baseProvider, net)
            : baseProvider;
        var frozen = new FrozenPayloadProvider(dfbProvider);
        var overrides = new ManualOverridePayloadProvider(frozen);
        var outer = new UnitConvertingPayloadProvider(overrides, UnitPreset.LanNative);
        return (net, overrides, frozen, outer);
    }

    /// <summary>The engine's Tick LAN law (EmuEngineHost.cs:271-311): the DFB
    /// network executes ONLY while the emulation gate is raised; one superframe
    /// per tick; elapsed advances 1 s per tick.</summary>
    private static IReadOnlyList<AceVectorData> Tick(
        UnitConvertingPayloadProvider provider, DfbNetwork? net, bool emulating, double elapsed)
    {
        if (net is not null && emulating)
            net.Execute(DfbContext.Create(elapsed, (long)elapsed, DfbEngineBridge.Seed));
        return provider.GetSuperframe(Equipment, elapsed);
    }

    private static byte[] Payload(IReadOnlyList<AceVectorData> superframe, uint sv)
        => superframe.First(v => v.SvNumber == sv).Payload;

    private static LantableRegistry.SignalDef Sig(LantableRegistry lantable, string name)
    {
        var svDef = lantable.GetBySvNumber(RawIoSv)!;
        return svDef.Signals.First(s => s.Name == name);
    }

    private static float ReadFloat(byte[] payload, LantableRegistry.SignalDef sig)
    {
        var bytes = new byte[4];
        Array.Copy(payload, sig.ByteOffset - 4, bytes, 0, 4);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToSingle(bytes, 0);
    }

    // ── (1) the generator is live: bytes change every tick while emulating ──

    [Fact]
    public void EmulationOn_SuperframeBytesChangeEveryTick_CountInputAdvances()
    {
        var (net, _, frozen, outer) = BuildChain();
        frozen.Frozen = false;                       // setemulate lan on
        Assert.NotNull(net);                         // DFB network must exist (else base registry sines drive values)
        var lantable = new LantableRegistry();
        var countIn = Sig(lantable, "Count Input 1");

        var t5 = Tick(outer, net, emulating: true, elapsed: 5.0);
        var t6 = Tick(outer, net, emulating: true, elapsed: 6.0);

        // RawIoBlock: CountIn[i] = (elapsed×10 + i×137) % 10000 — advances 10/s
        Assert.Equal(50f, ReadFloat(Payload(t5, RawIoSv), countIn));
        Assert.Equal(60f, ReadFloat(Payload(t6, RawIoSv), countIn));

        // the payload as a whole moves every tick (AnalogIn per-tick noise)
        Assert.False(Payload(t5, RawIoSv).AsSpan().SequenceEqual(Payload(t6, RawIoSv)),
            "with emulation ON the wire payload must change every tick");
    }

    // ── (2) emulation OFF while streaming = byte-identical frames (stand signature) ──

    [Fact]
    public void EmulationOff_StreamsByteIdenticalSuperframes_StandFreezeSignature()
    {
        var (net, _, frozen, outer) = BuildChain();
        var lantable = new LantableRegistry();
        var displayTemp = Sig(lantable, "Display Temperature");

        // warm-up exactly like the stand: emulation ON for ~570 s — the DFB
        // temperature converges toward 120 °F (stand froze at 119.991234)
        frozen.Frozen = false;
        for (int t = 1; t <= 570; t++)
            Tick(outer, net, emulating: true, elapsed: t);
        var warm = Tick(outer, net, emulating: true, elapsed: 571.0);
        Assert.InRange(ReadFloat(Payload(warm, RawIoSv), displayTemp), 115f, 125f);

        // the operator flips the EMULATION gate off; the STREAM stays on —
        // W14: hold the last superframe, frames keep flowing byte-identical
        frozen.Frozen = true;                        // SetEmulation("lan", off)

        var f1 = Tick(outer, net, emulating: false, elapsed: 572.0);
        var f2 = Tick(outer, net, emulating: false, elapsed: 573.0);
        var f3 = Tick(outer, net, emulating: false, elapsed: 574.0);

        Assert.Equal(Payload(warm, RawIoSv), Payload(f1, RawIoSv));
        Assert.Equal(Payload(f1, RawIoSv), Payload(f2, RawIoSv));
        Assert.Equal(Payload(f2, RawIoSv), Payload(f3, RawIoSv));

        // every SV of the superframe is frozen, not just RAWIO
        foreach (var v in f1)
        {
            var v2 = f2.First(x => x.SvNumber == v.SvNumber);
            Assert.True(v.Payload.AsSpan().SequenceEqual(v2.Payload),
                $"SV 0x{v.SvNumber:X8} must be byte-identical while frozen");
        }
    }

    // ── (3) manual edits reach the wire even while frozen (UI/CLI coupling) ──

    [Fact]
    public void ManualSet_ReachesWire_EvenWhileFrozen()
    {
        var (net, overrides, frozen, outer) = BuildChain();
        var lantable = new LantableRegistry();
        var countIn = Sig(lantable, "Count Input 1");
        var displayTemp = Sig(lantable, "Display Temperature");

        frozen.Frozen = false;
        Tick(outer, net, emulating: true, elapsed: 10.0);
        frozen.Frozen = true;                        // freeze (stream stays on)

        var before = Tick(outer, net, emulating: false, elapsed: 11.0);

        Assert.True(overrides.CanTransmit(RawIoSv, countIn.Name, out _),
            "Count Input 1 must be transmittable (payload law)");
        overrides.SetOverride(RawIoSv, countIn.Name, 12345.0);   // `set lan …`

        var after = Tick(outer, net, emulating: false, elapsed: 12.0);

        Assert.Equal(12345f, ReadFloat(Payload(after, RawIoSv), countIn));
        // ONLY the edited signal moved — the frozen waveform stays held
        int off = displayTemp.ByteOffset - 4;
        var tempBefore = Payload(before, RawIoSv)[off..(off + 4)];
        var tempAfter = Payload(after, RawIoSv)[off..(off + 4)];
        Assert.Equal(tempBefore, tempAfter);
    }

    // ── (4) the two-gates trap: `start lan` streams but does NOT un-freeze ──

    [Fact]
    public void StartLan_LeavesEmulationOff_StatusShowsStreamingTrueEmulatingFalse()
    {
        var settings = new EmuSettings { LanAdapter = "fake0" };
        var host = new EmuEngineHost(settings, new FakeComEmitter(), new FakeLanEmitter());

        var start = CommandProcessor.ProcessLine("{\"cmd\":\"start\",\"channel\":\"lan\"}", host);
        using (var doc = JsonDocument.Parse(start.Json))
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        var st = host.GetStatus();
        var lan = (Dictionary<string, object?>)st["lan"]!;
        Assert.True((bool)lan["streaming"]!, "stream gate raised by start");
        Assert.False((bool)lan["emulating"]!,
            "start must NOT raise the emulation gate — streaming a FROZEN superframe is the W14 law; " +
            "the operator lifts the freeze with setemulate lan on (UI: «Эмуляция (автогенерация значений)»)");

        var on = CommandProcessor.ProcessLine("{\"cmd\":\"setemulate\",\"channel\":\"lan\",\"value\":1}", host);
        using (var doc = JsonDocument.Parse(on.Json))
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        lan = (Dictionary<string, object?>)host.GetStatus()["lan"]!;
        Assert.True((bool)lan["emulating"]!, "setemulate lan on must raise the emulation gate");
    }
}
