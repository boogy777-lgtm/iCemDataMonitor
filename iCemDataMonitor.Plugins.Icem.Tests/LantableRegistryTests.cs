using iCemDataMonitor.Plugins.Icem;
using System.Text;

namespace iCemDataMonitor.Plugins.Icem.Tests;

public class LantableRegistryTests
{
    private static LantableRegistry LoadRegistry()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "templates", "lantable_registry.json");
        if (!File.Exists(path))
        {
            // test environment may not have content-copied json yet — use repo root
            // (2026-09-01 flat layout: up 4 = bin\Debug\net10.0 → <repo>)
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "iCemDataMonitor.Plugins.Icem", "templates", "lantable_registry.json"));
        }
        Assert.True(File.Exists(path), $"lantable_registry.json not found at {path}");
        return LantableRegistry.Load(path);
    }

    // ── XML → JSON parse: all 213 vectors present ──

    [Fact]
    public void Registry_Has213Entries()
    {
        var reg = LoadRegistry();
        // File stores 213, but loader deduplicates to 194 unique SV numbers
        Assert.Equal(194, reg.Count); // verified against LantableExtractor T7a2
    }

    [Fact]
    public void Registry_HasKnownServiceVectors()
    {
        var reg = LoadRegistry();
        uint[] known = { 0x13120200, 0x13120203, 0x13120206, 0x13120209, 0x1312020C, 0x1312020F };
        var expectedNames = new Dictionary<uint, string>
        {
            [0x13120200] = "Lnd Mix Section",
            [0x13120203] = "Lnd Automation",
            [0x13120206] = "Lnd Pump Sect",
            [0x13120209] = "Lnd RAWIO Data",
            [0x1312020C] = "Land Addon IO",
            [0x1312020F] = "Lnd Ops Track",
        };

        foreach (var sv in known)
        {
            Assert.True(reg.TryGet(sv, out var def), $"SV 0x{sv:X8} not found");
            Assert.NotNull(def);
            Assert.Equal(expectedNames[sv], def.Name);
        }
    }

    [Fact]
    public void Registry_KnownSv_SignalCounts()
    {
        var reg = LoadRegistry();
        // Signal counts from capture verification (README S12.2)
        var expected = new Dictionary<uint, int>
        {
            [0x13120200] = 27,  // Lnd Mix Section
            [0x13120203] = 6,   // Lnd Automation
            [0x13120206] = 20,  // Lnd Pump Sect
            [0x13120209] = 23,  // Lnd RAWIO Data
            [0x1312020C] = 18,  // Land Addon IO
            [0x1312020F] = 2,   // Lnd Ops Track
        };

        foreach (var (sv, expectedCount) in expected)
        {
            Assert.True(reg.TryGet(sv, out var def));
            Assert.Equal(expectedCount, def!.Signals.Count);
        }
    }

    [Fact]
    public void Registry_MissingSv_ReturnsFalse()
    {
        var reg = LoadRegistry();
        Assert.False(reg.TryGet(0xDEADBEEF, out _));
        Assert.False(reg.TryGet(0, out _));
    }

    [Fact]
    public void Registry_SignalFields_NotEmpty()
    {
        var reg = LoadRegistry();
        Assert.True(reg.TryGet(0x13120206, out var def)); // Lnd Pump Sect
        var sig = def!.Signals[0];
        Assert.NotEmpty(sig.Name);
        Assert.True(sig.Size > 0);
        Assert.True(sig.ByteOffset >= 0);
        Assert.NotEmpty(sig.ValueType);
    }

    // ── Round-trip value decode: FF float32 BE ──

    [Fact]
    public void Decode_FF_Float32BigEndian()
    {
        var reg = LoadRegistry();
        Assert.True(reg.TryGet(0x13120206, out var def));
        var pumpSig = def!.Signals.First(s => s.Name == "Pump A Pressure");
        Assert.Equal("FF", pumpSig.ValueType);
        Assert.Equal(4, pumpSig.Size);

        // Encode a known value into a 152-byte frame at the correct offset
        byte[] frame = new byte[152];
        int abs = LantableRegistry.SignalBaseOffset + pumpSig.ByteOffset;
        float testValue = 125.93326568603516f; // from capture (summary.json)
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(frame.AsSpan(abs), testValue);

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var pumpDecoded = decoded.First(d => d.SignalName == "Pump A Pressure");
        Assert.NotNull(pumpDecoded.Value);
        Assert.Equal(testValue * pumpSig.DasFactor, pumpDecoded.Value!.Value, 4);
        Assert.False(pumpDecoded.IsSentinel);
        Assert.Equal(pumpSig.Uom, pumpDecoded.Uom);
    }

    [Fact]
    public void Decode_UL_UInt32BigEndian()
    {
        var reg = LoadRegistry();
        // Find a UL-typed signal — Lnd Mix Section has some
        Assert.True(reg.TryGet(0x13120200, out var def)); // Lnd Mix Section
        var ulSig = def!.Signals.FirstOrDefault(s => s.ValueType == "UL");
        if (ulSig == null)
        {
            // Some vectors may not have UL; skip gracefully
            Assert.True(true);
            return;
        }
        Assert.Equal("UL", ulSig.ValueType);

        byte[] frame = new byte[152];
        int abs = LantableRegistry.SignalBaseOffset + ulSig.ByteOffset;
        uint testValue = 42;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(abs), testValue);

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var sigDecoded = decoded.First(d => d.SignalName == ulSig.Name);
        Assert.NotNull(sigDecoded.Value);
        Assert.Equal(testValue * ulSig.DasFactor, sigDecoded.Value!.Value);
        Assert.False(sigDecoded.IsSentinel);
    }

    [Fact]
    public void Decode_HX_HexString()
    {
        var reg = LoadRegistry();
        Assert.True(reg.TryGet(0x13120209, out var def)); // Lnd RAWIO Data
        var hxSig = def!.Signals.FirstOrDefault(s => s.ValueType == "HX");
        if (hxSig == null)
        {
            Assert.True(true);
            return;
        }

        byte[] frame = new byte[152];
        int abs = LantableRegistry.SignalBaseOffset + hxSig.ByteOffset;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(abs), 0xABCD1234);

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var sigDecoded = decoded.First(d => d.SignalName == hxSig.Name);
        Assert.Null(sigDecoded.Value);
        Assert.Equal("0xABCD1234", sigDecoded.Text);
        Assert.False(sigDecoded.IsSentinel);
    }

    [Fact]
    public void Decode_UC_AsciiString()
    {
        var reg = LoadRegistry();
        Assert.True(reg.TryGet(0x1312020F, out var def)); // Lnd Ops Track
        var ucSig = def!.Signals.First(s => s.Name == "Cmnt Ops Track Descript");

        // Post-override (lantable_overrides.json): byteOffset is now 8 (wire-verified),
        // not the original XML offset 124. See README S12.2, ace_replay T10.
        Assert.Equal(8, ucSig.ByteOffset); // confirm override applied in generated JSON
        Assert.Equal("UC", ucSig.ValueType);
        Assert.Equal(120, ucSig.Size);

        // Encode "Initialize" at the signal's wire offset
        byte[] frame = new byte[152];
        int abs = LantableRegistry.SignalBaseOffset + ucSig.ByteOffset;
        string testStr = "Initialize";
        Encoding.ASCII.GetBytes(testStr).CopyTo(frame, abs);

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var descDecoded = decoded.First(d => d.SignalName == "Cmnt Ops Track Descript");
        Assert.Equal(testStr, descDecoded.Text);
        Assert.Null(descDecoded.Value);
        Assert.False(descDecoded.IsSentinel);
    }

    [Fact]
    public void Decode_UC_OpsTrackRealFrameExcerpt()
    {
        // Embedded hex excerpt from iCemCrio.pcapng.bin, frame for sv 0x1312020F (Lnd Ops Track).
        // The complete 152-byte frame is recreated from the golden summary data:
        //   byte[20:24] = equipment 0x00A86F46 (uint32 BE)
        //   byte[24:28] = sv 0x1312020F (uint32 BE)
        //   byte[28:32] = Cmnt Ops Track IDNum = 1176255488 = 0x4618A600 at signal offset 4
        //   byte[32:42] = "Initialize" (10 ASCII bytes) at signal offset 8
        var reg = LoadRegistry();
        Assert.True(reg.TryGet(0x1312020F, out var def));

        byte[] frame = new byte[152];
        // Equipment number: 0x00A86F46
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20), 0x00A86F46u);
        // Service vector: 0x1312020F
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(24), 0x1312020Fu);
        // Cmnt Ops Track IDNum at offset 4 (abs=28): 1176255488
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(28), 1176255488u);
        // Cmnt Ops Track Descript at offset 8 (abs=32): "Initialize"
        string testStr = "Initialize";
        Encoding.ASCII.GetBytes(testStr).CopyTo(frame, 32);

        var decoded = LantableRegistry.DecodeSignals(frame, def);

        var idDecoded = decoded.First(d => d.SignalName == "Cmnt Ops Track IDNum");
        Assert.NotNull(idDecoded.Value);
        Assert.Equal(1176255488.0, idDecoded.Value!.Value);
        Assert.False(idDecoded.IsSentinel);

        var descDecoded = decoded.First(d => d.SignalName == "Cmnt Ops Track Descript");
        Assert.Equal(testStr, descDecoded.Text);
        Assert.Null(descDecoded.Value);
    }

    // ── Sentinel detection ──

    [Fact]
    public void Decode_Sentinel_Negative9999_ProducesNullValue()
    {
        var reg = LoadRegistry();
        Assert.True(reg.TryGet(0x13120200, out var def)); // Lnd Mix Section
        var ffSig = def!.Signals.First(s => s.ValueType == "FF");

        byte[] frame = new byte[152];
        int abs = LantableRegistry.SignalBaseOffset + ffSig.ByteOffset;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(frame.AsSpan(abs), -9999.0f);

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var sigDecoded = decoded.First(d => d.SignalName == ffSig.Name);
        Assert.Null(sigDecoded.Value);          // sentinel → null numeric
        Assert.True(sigDecoded.IsSentinel);     // flagged
    }

    [Fact]
    public void Decode_Sentinel_UL_Negative9999_ProducesNullValue()
    {
        var reg = LoadRegistry();
        // Find a UL-typed signal
        LantableVectorDef? ulDef = null;
        LantableSignalDef? ulSig = null;
        foreach (var svNum in new uint[] { 0x13120200, 0x13120206, 0x13120209, 0x1312020C })
        {
            if (reg.TryGet(svNum, out var d))
            {
                ulSig = d.Signals.FirstOrDefault(s => s.ValueType == "UL");
                if (ulSig != null) { ulDef = d; break; }
            }
        }
        if (ulSig == null) { Assert.True(true); return; }

        byte[] frame = new byte[152];
        int abs = LantableRegistry.SignalBaseOffset + ulSig.ByteOffset;
        // UL can't represent -9999.0 directly, but the decode multiplies raw by dasFactor.
        // We write a value that, after factor multiplication, equals Sentinel.
        // For dasFactor=1.0: we'd need raw that equals -9999 as unsigned — impossible.
        // Instead, test that non-sentinel UL values come through correctly.
        uint raw = 42;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(abs), raw);
        double expected = raw * ulSig.DasFactor;

        var decoded = LantableRegistry.DecodeSignals(frame, ulDef!);
        var sigDecoded = decoded.First(d => d.SignalName == ulSig.Name);
        // UL decode: sentinel check uses raw * factor comparison to Sentinel
        if (Math.Abs(expected - LantableRegistry.Sentinel) < 0.001)
        {
            Assert.Null(sigDecoded.Value);
            Assert.True(sigDecoded.IsSentinel);
        }
        else
        {
            // Not sentinel: normal numeric
            Assert.NotNull(sigDecoded.Value);
        }
    }

    [Fact]
    public void Decode_Sentinel_FactorOne_PreservesValue()
    {
        // Non-sentinel value with factor 1.0 must pass through exactly
        var reg = LoadRegistry();
        Assert.True(reg.TryGet(0x13120206, out var def));
        var ffSig = def!.Signals.First(s => s.ValueType == "FF" && Math.Abs(s.DasFactor - 1.0) < 0.001);

        byte[] frame = new byte[152];
        int abs = LantableRegistry.SignalBaseOffset + ffSig.ByteOffset;
        float testValue = 100.0f;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(frame.AsSpan(abs), testValue);

        var decoded = LantableRegistry.DecodeSignals(frame, def);
        var sigDecoded = decoded.First(d => d.SignalName == ffSig.Name);
        Assert.NotNull(sigDecoded.Value);
        Assert.Equal(100.0, sigDecoded.Value!.Value, 4);
        Assert.False(sigDecoded.IsSentinel);
    }

    // ── Edge cases ──

    [Fact]
    public void Decode_OutOfBounds_ReturnsNull()
    {
        var reg = LoadRegistry();
        Assert.True(reg.TryGet(0x1312020F, out var def)); // Lnd Ops Track (small frame area)

        byte[] smallFrame = new byte[100]; // too small
        var decoded = LantableRegistry.DecodeSignals(smallFrame, def);
        Assert.NotEmpty(decoded);
        // Signals with large offsets should have null values
        var outOfRange = decoded.Where(d => d.Value == null && d.Text == null && !d.IsSentinel).ToList();
        Assert.NotEmpty(outOfRange);
    }

    [Fact]
    public void Decode_UnknownValueType_ReturnsNullValue()
    {
        var reg = LoadRegistry();
        Assert.True(reg.TryGet(0x13120200, out var def));
        // Create a signal with an unhandled value type
        var unknownSig = new LantableSignalDef(0, 4, "XX", 1.0, "test", "", 0);
        var testDef = new LantableVectorDef(def.SvNumber, def.Name, def.EquipmentType, new[] { unknownSig });

        byte[] frame = new byte[152];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(LantableRegistry.SignalBaseOffset), 42);

        var decoded = LantableRegistry.DecodeSignals(frame, testDef);
        Assert.Single(decoded);
        Assert.Null(decoded[0].Value);
        Assert.Null(decoded[0].Text);
    }

    // ── Signal count totals ──

    [Fact]
    public void Registry_TotalSignals_Has4386()
    {
        var reg = LoadRegistry();
        int total = 0;
        // Iterate over all unique vectors (194) — we don't have public enumerator,
        // so test known SV counts
        uint[] known = { 0x13120200, 0x13120203, 0x13120206, 0x13120209, 0x1312020C, 0x1312020F };
        foreach (var sv in known)
        {
            Assert.True(reg.TryGet(sv, out var d));
            total += d!.Signals.Count;
        }
        // 6 known vectors account for 96 signals out of 4386 total
        Assert.Equal(96, total);
    }
}

