using System.Buffers.Binary;
using System.Text;
using iCemDataMonitor.Plugins.Icem;

namespace iCemDataMonitor.Transport.Tests;

/// <summary>
/// ACE LAN decode contract regression suite (wire-decode defect 2026-08-17).
///
/// Root cause of the lan-zone int-bits-in-float defect was NOT the valueType
/// arms (all arms are spec-correct per LANTABLE_Complete.yaml + FlecsEmu
/// emitter symmetry). It was a 4-byte PAYLOAD-BASE mismatch: the FlecsEmu
/// emitter placed the signal payload at frame offset 28, while the real ACE
/// wire (iCemCrio.pcapng.bin, PROTOCOL.md §1.2, golden T8) places it at
/// offset 24. The decoder read every signal 4 bytes early — FF signals then
/// float-read neighboring UL/SI/HX int bytes ⇒ denormals whose bits equal
/// the raw int (1.4e-45 = 0x00000001, 1.13e-38 = 0x007C8000) landed in the
/// bus via WriteFloat.
///
/// Fix: FlecsEmu AceFrameBuilder.PayloadOffset 0x1C → 0x18 (real wire).
/// These tests lock the decoder contract (engineering double out, descaled)
/// and pin the real-wire base offset so no side may drift to 28 again.
/// </summary>
public class AceLanDecodeContractTests
{
    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Build a REAL-WIRE ACE frame (152 B): signal area starts at frame offset 24;
    /// the sv u32 occupies signal-area slots 0..3, signals start at ByteOffset >= 4.
    /// </summary>
    private static byte[] BuildRealWireFrame(uint svNumber, Action<byte[]> fillSignals)
    {
        var frame = new byte[152];

        // dst MAC (multicast 03:00:01:00:00:20), src MAC (NI OUI)
        frame[0] = 0x03; frame[1] = 0x00; frame[2] = 0x01;
        frame[3] = 0x00; frame[4] = 0x00; frame[5] = 0x20;
        frame[6] = 0x00; frame[7] = 0x80; frame[8] = 0x2F;
        frame[9] = 0x23; frame[10] = 0xF4; frame[11] = 0x44;
        // IEEE 802.3 length 0x008A
        frame[12] = 0x00; frame[13] = 0x8A;
        // LLC 03 03 08, magic 00 D0 0D
        frame[14] = 0x03; frame[15] = 0x03; frame[16] = 0x08;
        frame[17] = 0x00; frame[18] = 0xD0; frame[19] = 0x0D;
        // equipment uint32 BE at 20..24
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, 4), 0x00A86F46u);
        // signals first (area base 24), then sv — sv is authoritative
        fillSignals(frame);
        // sv uint32 BE at 24..28 (signal-area slots 0..3)
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(24, 4), svNumber);
        return frame;
    }

    /// <summary>
    /// Build the OLD emulator-bug frame: payload copied at offset 28
    /// (signal o sits at frame 28 + o instead of real-wire 24 + o).
    /// </summary>
    private static byte[] BuildMisalignedFrame(uint svNumber, Action<byte[]> fillSignals)
    {
        var frame = new byte[152];
        frame[0] = 0x03; frame[1] = 0x00; frame[2] = 0x01;
        frame[3] = 0x00; frame[4] = 0x00; frame[5] = 0x20;
        frame[6] = 0x00; frame[7] = 0x80; frame[8] = 0x2F;
        frame[9] = 0x23; frame[10] = 0xF4; frame[11] = 0x44;
        frame[12] = 0x00; frame[13] = 0x8A;
        frame[14] = 0x03; frame[15] = 0x03; frame[16] = 0x08;
        frame[17] = 0x00; frame[18] = 0xD0; frame[19] = 0x0D;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, 4), 0x00A86F46u);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(24, 4), svNumber);
        fillSignals(frame);
        return frame;
    }

    private static LantableVectorDef Def(params LantableSignalDef[] signals) =>
        new(0x13120206, "Test Vector", "Pump", signals);

    // ── per-arm contract tests ───────────────────────────────────────────

    [Fact]
    public void Decode_UL_IntScaled_ProducesDescaledEngineeringValue()
    {
        // Encode side: engineering 8192.0 × dasFactor 1000 ⇒ wire int 8192000.
        // Decode side: dasFactor = 1/1000 ⇒ Value MUST be 8192.0 exactly.
        var ulSig = new LantableSignalDef(4, 4, "UL", 0.001, "Mix Water Stage Total", "bbl", 0);
        var def = Def(ulSig);

        var frame = BuildRealWireFrame(0x13120206u, f =>
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(LantableRegistry.SignalBaseOffset + ulSig.ByteOffset, 4), 8192000u));

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var sig = decoded.Single(d => d.SignalName == "Mix Water Stage Total");

        Assert.NotNull(sig.Value);
        Assert.Equal(8192.0, sig.Value!.Value);   // exact — no float reinterpretation
        Assert.False(sig.IsSentinel);
    }

    [Fact]
    public void Decode_SI_Int32Scaled_ProducesDescaledEngineeringValue()
    {
        var siSig = new LantableSignalDef(8, 4, "SI", 0.01, "RPM", "rpm", 0);
        var def = Def(siSig);

        var frame = BuildRealWireFrame(0x13120206u, f =>
            BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(LantableRegistry.SignalBaseOffset + siSig.ByteOffset, 4), -42000));

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var sig = decoded.Single(d => d.SignalName == "RPM");

        Assert.NotNull(sig.Value);
        Assert.Equal(-420.0, sig.Value!.Value);
        Assert.False(sig.IsSentinel);
    }

    [Fact]
    public void Decode_FF_Float32BE_ProducesExactFloat_NoNeighborCrosstalk()
    {
        var ffSig = new LantableSignalDef(4, 4, "FF", 1.0, "Pump A Pressure", "psi", 0);
        var neighbor = new LantableSignalDef(8, 4, "FF", 1.0, "Raw Pmp A Pressure", "psi", 0);
        var def = Def(ffSig, neighbor);

        const float testValue = 125.93326568603516f;   // golden T8 wire value
        const float neighborValue = 150.0f;            // golden T8b (OPKO-style)

        var frame = BuildRealWireFrame(0x13120206u, f =>
        {
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(LantableRegistry.SignalBaseOffset + ffSig.ByteOffset, 4), testValue);
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(LantableRegistry.SignalBaseOffset + neighbor.ByteOffset, 4), neighborValue);
        });

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var sig = decoded.Single(d => d.SignalName == "Pump A Pressure");

        Assert.NotNull(sig.Value);
        // float32BE → double widening, dasFactor 1.0 — exact equality
        Assert.Equal((double)testValue, sig.Value!.Value);
        Assert.False(sig.IsSentinel);
    }

    [Fact]
    public void Decode_HX_UInt32BE_ProducesHexTextOnly()
    {
        var hxSig = new LantableSignalDef(16, 4, "HX", 1.0, "Status Word", "", 0);
        var def = Def(hxSig);

        var frame = BuildRealWireFrame(0x13120206u, f =>
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(LantableRegistry.SignalBaseOffset + hxSig.ByteOffset, 4), 0xABCD1234u));

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var sig = decoded.Single(d => d.SignalName == "Status Word");

        Assert.Null(sig.Value);                       // display-only type — sink skips
        Assert.Equal("0xABCD1234", sig.Text);
        Assert.False(sig.IsSentinel);
    }

    [Fact]
    public void Decode_UC_AsciiNullTerminated_ProducesTextOnly()
    {
        var ucSig = new LantableSignalDef(20, 12, "UC", 1.0, "Comment", "", 0);
        var def = Def(ucSig);

        var frame = BuildRealWireFrame(0x13120206u, f =>
            Encoding.ASCII.GetBytes("Initialize").CopyTo(f, LantableRegistry.SignalBaseOffset + ucSig.ByteOffset));

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var sig = decoded.Single(d => d.SignalName == "Comment");

        Assert.Null(sig.Value);                       // string-only type — sink skips
        Assert.Equal("Initialize", sig.Text);
        Assert.False(sig.IsSentinel);
    }

    // ── payload-base regression lock (the actual defect mechanism) ───────

    [Fact]
    public void Decode_RealWireBase24_SignalAtByteOffset4_IsReadFromFrame28()
    {
        // Contract: signal(o) = frame[24 + o]. UL @4 lives at frame 28 (not 32).
        var ulSig = new LantableSignalDef(4, 4, "UL", 0.001, "Mix Water Stage Total", "bbl", 0);
        var def = Def(ulSig);

        var frame = BuildRealWireFrame(0x13120206u, f =>
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(LantableRegistry.SignalBaseOffset + ulSig.ByteOffset, 4), 8192000u));

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        Assert.Equal(8192.0, decoded.Single().Value!.Value);
    }

    [Fact]
    public void Decode_MisalignedEmitterFrame_FloatReadsIntNeighbor_RawIntBitsInValue()
    {
        // Negative lock for the emitter bug (FlecsEmu payload @28, real wire @24):
        // decoder reads signal(o) at frame 24+o, but the misaligned emitter wrote
        // it at 28+o ⇒ every signal reads the 4 bytes BEFORE its slot. An FF
        // signal preceded by an UL int signal therefore float-reads the int
        // bytes — the exact live-defect signature: Value becomes a denormal
        // float whose bits EQUAL the raw int (lan zone regs showed 0x007C8000).
        var ulSig = new LantableSignalDef(12, 4, "UL", 1.0, "Digital Outputs", "", 0);
        var ffSig = new LantableSignalDef(16, 4, "FF", 1.0, "Display Temperature", "f", 0);
        var def = Def(ulSig, ffSig);

        // misaligned emitter: signals at 28 + o (old bug). UL@12 ⇒ frame 40..44.
        var frame = BuildMisalignedFrame(0x13120206u, f =>
        {
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(28 + ulSig.ByteOffset, 4), 8192000u);
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(28 + ffSig.ByteOffset, 4), 25.5f);
        });

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var ff = decoded.Single(d => d.SignalName == "Display Temperature");

        // decoder read frame 40..44 = the UL int bytes 00 7C 80 00 as float32
        Assert.NotNull(ff.Value);
        Assert.NotEqual(25.5, ff.Value!.Value);                        // NOT its own slot
        Assert.Equal(8192000u, BitConverter.SingleToUInt32Bits((float)ff.Value.Value)); // raw int bits in float
    }
}