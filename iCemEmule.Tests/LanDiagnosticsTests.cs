// LanDiagnosticsTests — 2026-08-29 LAN UX/diagnostics round: pins
//   (1) the `adapters` command against an EMPTY adapter source (the
//       Npcap-missing case) — ok envelope, empty array, npcapAvailable
//       carried, no crash;
//   (2) framesSent flowing emitter counter → status reply (the field the
//       LAN tab renders as «кадров отправлено: N»);
//   (3) the REAL AceLanEmitter frame counter through the injected device
//       seam — increments per SendSuperframe, resets on Open (adapter
//       change = new emission session);
//   (4) the static Npcap probes never throw, with or without the driver.
// Start-without-adapter ⇒ ErrNoLanAdapter is already pinned by
// EngineStreamToggleTests.Start_WithoutEndpoint_RefusesWithExactMappedText.

using System.Text.Json;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Emitters;
using Xunit;

namespace iCemEmule.Tests;

public class LanDiagnosticsTests
{
    // ── fake SharpPcap device for the AceLanEmitter ctor seam ────────────
    private sealed class FakeSendDevice : ILanSendDevice
    {
        public string? Description => "Fake test NIC";
        public string? Name { get; }
        public byte[]? MacBytes { get; } = { 0x02, 0x00, 0x00, 0x00, 0x00, 0x01 };
        public List<byte[]> Sent { get; } = new();
        public int OpenCalls { get; private set; }

        public FakeSendDevice(string name) => Name = name;

        public void Open() => OpenCalls++;
        public void Close() { }
        public void SendPacket(byte[] frame) => Sent.Add(frame);
        public void Dispose() { }
    }

    private static JsonElement Reply(EmuEngineHost host, string command)
    {
        var r = CommandProcessor.ProcessLine(command, host);
        return JsonDocument.Parse(r.Json).RootElement.Clone();
    }

    // ── (1) adapters command, Npcap missing ⇒ empty, no crash ───────────

    [Fact]
    public void Adapters_EmptySource_OkEmptyArray_CarriesNpcapFlag_NoCrash()
    {
        // Npcap missing ⇒ the engine-visible adapter list is EMPTY; the
        // command must still answer ok with an empty array — the UI renders
        // the «⚠ Npcap не установлен» hint off this + npcapAvailable.
        var host = new EmuEngineHost(new EmuSettings(), new FakeComEmitter(), new FakeLanEmitter(),
            controlPipeName: null, adapterLister: () => Array.Empty<string>());

        var root = Reply(host, "{\"cmd\":\"adapters\"}");
        Assert.True(root.GetProperty("ok").GetBoolean(), root.GetRawText());
        var data = root.GetProperty("data");
        Assert.Empty(data.GetProperty("adapters").EnumerateArray());
        Assert.True(data.TryGetProperty("npcapAvailable", out _),
            "adapters reply must carry npcapAvailable for the UI honesty hint");
    }

    // ── (2) framesSent in the status reply ───────────────────────────────

    [Fact]
    public void Status_Lan_CarriesFramesSent_FromEmitterCounter()
    {
        var lan = new FakeLanEmitter();
        var host = new EmuEngineHost(new EmuSettings(), new FakeComEmitter(), lan);

        // simulate emission (the 1 Hz tick adds vectors.Count per superframe)
        lan.SendSuperframe(new[]
        {
            new AceVectorData(1u, "a", new byte[AceFrameBuilder.PayloadSize]),
            new AceVectorData(2u, "b", new byte[AceFrameBuilder.PayloadSize]),
            new AceVectorData(3u, "c", new byte[AceFrameBuilder.PayloadSize]),
        }, 0x00A86F46u, "");
        Assert.Equal(3, lan.FramesSent);

        var root = Reply(host, "{\"cmd\":\"status\"}");
        Assert.True(root.GetProperty("ok").GetBoolean(), root.GetRawText());
        var lanEl = root.GetProperty("data").GetProperty("lan");
        Assert.Equal(3, lanEl.GetProperty("framesSent").GetInt64());
        Assert.True(lanEl.TryGetProperty("npcapAvailable", out _),
            "status.lan must carry npcapAvailable for the header LAN indicator");
    }

    // ── (3) real emitter counter through the injected device seam ────────

    [Fact]
    public void AceLanEmitter_CountsFramesPerSuperframe_AndResetsOnOpen()
    {
        var dev = new FakeSendDevice("TestNIC");
        var emitter = new AceLanEmitter(() => new ILanSendDevice[] { dev });

        Assert.Equal("", emitter.Open("TestNIC"));
        Assert.True(emitter.IsOpen);
        Assert.Equal(1, dev.OpenCalls);
        Assert.Equal(0, emitter.FramesSent);

        var vectors = new[]
        {
            new AceVectorData(0x11111111u, "s", new byte[AceFrameBuilder.PayloadSize])
        };
        emitter.SendSuperframe(vectors, 0x00A86F46u, "00:11:22:33:44:55");
        Assert.Equal(1, emitter.FramesSent);
        var frame = Assert.Single(dev.Sent);
        Assert.Equal(AceFrameBuilder.FrameSize, frame.Length);   // 152-byte ACE wire frame

        emitter.SendSuperframe(vectors, 0x00A86F46u, "00:11:22:33:44:55");
        Assert.Equal(2, emitter.FramesSent);
        Assert.Equal(2, dev.Sent.Count);

        // adapter change / reopen = a NEW emission session ⇒ counter restarts
        Assert.Equal("", emitter.Open("TestNIC"));
        Assert.Equal(0, emitter.FramesSent);

        emitter.Dispose();
        Assert.False(emitter.IsOpen);
    }

    // ── (4) the static Npcap probe never throws ──────────────────────────

    [Fact]
    public void AceLanEmitter_NpcapProbe_NeverThrows()
    {
        // With or without Npcap on the machine the probe must answer fast and
        // never throw (2026-08-29: a pure wpcap.dll FILE check — a real
        // CaptureDeviceList enumeration here cost seconds and starved the
        // parallel pipe tests).
        _ = AceLanEmitter.IsNpcapAvailable();
    }
}
