using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// Byte-level truth-table contract tests for the bus DECODE path
/// (raw persisted bytes → BusReader.Snapshot words → CdabCodec.Combine).
///
/// PRODUCER-ONLY DOCTRINE (INV-39): the POC has NO writer — the AddIn is the
/// only Data.dat producer. These pins prove the READ side carries the bytes
/// FAITHFULLY: whatever the producer (or a foreign writer) put on the wire is
/// what the host decodes — the reader never normalizes, truncates, or
/// canonicalizes. v5 map: ace_full 600..10000 is RESERVED/DEFERRED (nobody
/// writes in production); the truth-table register sits at the ace_full
/// reserve base (BusLayout.AceFullBaseReg = 600).
///
/// LIVE DEFECT SIGNATURE (observed 2026-08-17, INC-06): a FOREIGN writer (iCem
/// AddIn) interleaved writes into the same registers with TRUNCATED words —
/// expected [AE 14 3F A7] (float bits 0x3FA7AE14, CDAB low word 0xAE14 + high
/// word 0x3FA7, big-endian bytes inside each word) observed as [00 14 00 A7]:
/// the HIGH byte of every word zeroed while the low byte survives. The reader
/// must mirror the file bytes verbatim — even a defective window decodes as
/// the exact two words (0x0014, 0x00A7), never "corrected".
///
/// Coverage:
///   1. Decode truth table — cross product of 5 low-word byte classes ×
///      5 high-word byte classes (25 rows) + 11 semantic IEEE values + the
///      live-defect pair as a named pin — the words decoded from the EXACT
///      4 bytes equal the raw bit words for every row, and CdabCodec.Combine
///      reconstructs the same IEEE bit pattern (NaN rows exempt from the
///      bit-pattern compare — hardware payload canonicalization is legal).
///   2. Truncated-signature window [00 14 00 A7] decodes byte-faithfully as
///      {0x0014, 0x00A7} — the reader never normalizes.
///   3. STALE-HOLD LAW (v5 global invariant, read side): seeded cells survive
///      the whole reader lifecycle byte-exact — open/snapshot/dispose never
///      zeroes or reshapes anything; the AddIn heartbeat cell (reg 3) is
///      untouched by the host.
/// </summary>
public class BusDecodeTruthTableTests : IDisposable
{
    /// <summary>ace_full base register (v5) — the truth-table cell (600).</summary>
    private const int TestReg = BusLayout.AceFullBaseReg;

    private readonly string _dir;
    private readonly string _mmfPath;
    private readonly string _mapName;
    private readonly string _mutexName;

    public BusDecodeTruthTableTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bus_truth_test", Guid.NewGuid().ToString("N"));
        _mmfPath = Path.Combine(_dir, "test_bus.dat");
        _mapName = $"iCemDataMonitorBusTruthMap_{Guid.NewGuid():N}";
        _mutexName = $"iCemDataMonitorBusTruthMutex_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ═══ 1. Decode truth table (37 rows) ═══

    /// <summary>
    /// 37 rows: 25 = cross product of low-word byte classes {0x0000, 0x00FF,
    /// 0xFF00, 0xAE14, 0xFFFF} × high-word byte classes {0x0000, 0x00FF,
    /// 0x3FA7, 0xFF00, 0xFFFF} (crafted float bits), 11 = semantic IEEE values
    /// {+0, -0, +1, -1, MaxValue, MinValue, Epsilon (denormal), 8192, -125.93,
    /// NaN, +∞}, 1 = the live-defect pair 0x3FA7AE14 (explicit named pin).
    /// </summary>
    public static IEnumerable<object[]> TruthTableValues()
    {
        ushort[] lows = { 0x0000, 0x00FF, 0xFF00, 0xAE14, 0xFFFF };
        ushort[] highs = { 0x0000, 0x00FF, 0x3FA7, 0xFF00, 0xFFFF };
        foreach (ushort high in highs)
        foreach (ushort low in lows)
        {
            uint bits = ((uint)high << 16) | low;
            yield return new object[] { $"x_low=0x{low:X4}_high=0x{high:X4}", bits };
        }

        yield return new object[] { "s_+0", 0x00000000u };
        yield return new object[] { "s_-0", 0x80000000u };
        yield return new object[] { "s_+1", 0x3F800000u };
        yield return new object[] { "s_-1", 0xBF800000u };
        yield return new object[] { "s_max", 0x7F7FFFFFu };
        yield return new object[] { "s_min", 0xFF7FFFFFu };
        yield return new object[] { "s_epsilon", 0x00000001u };           // denormal 1.4e-45
        yield return new object[] { "s_8192", 0x46000000u };
        yield return new object[] { "s_-125.93", BitConverter.SingleToUInt32Bits(-125.93f) };
        yield return new object[] { "s_nan", BitConverter.SingleToUInt32Bits(float.NaN) };
        yield return new object[] { "s_+inf", 0x7F800000u };
        yield return new object[] { "s_live_defect", 0x3FA7AE14u };       // [AE 14 3F A7]
    }

    [Theory]
    [MemberData(nameof(TruthTableValues))]
    public void RawBytes_DecodeTruthTable_ExactWords(string name, uint bits)
    {
        // Expected words: the CDAB pair for the float whose bits are `bits`.
        // NaN rows split the RAW bits directly — CdabCodec.Split routes
        // through the float value and the hardware may canonicalize the NaN
        // payload on the way, shifting the words.
        float value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        ushort low, high;
        if (IsNaN(bits))
        {
            low = (ushort)(bits & 0xFFFF);
            high = (ushort)(bits >> 16);
        }
        else
        {
            (low, high) = CdabCodec.Split(value);
        }

        // Seed the EXACT 4 wire bytes [low.hi, low.lo, high.hi, high.lo].
        BusTestFactory.CreateFile(_mmfPath);
        WriteRawBytes(TestReg, new byte[] { (byte)(low >> 8), (byte)low, (byte)(high >> 8), (byte)high });

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var rerr), rerr);
        var snap = reader.Snapshot();
        Assert.NotNull(snap);

        // The reader decodes the EXACT words (byte-faithful mirror).
        Assert.True(snap.Words[TestReg] == low,
            $"row {name}: low word mismatch — expected 0x{low:X4}, decoded 0x{snap.Words[TestReg]:X4}");
        Assert.True(snap.Words[TestReg + 1] == high,
            $"row {name}: high word mismatch — expected 0x{high:X4}, decoded 0x{snap.Words[TestReg + 1]:X4}");

        // Roundtrip bit pattern: Combine reconstructs the same IEEE bits.
        // NaN rows exempt — payload canonicalization is legal hardware behavior.
        if (!IsNaN(bits))
        {
            float combined = CdabCodec.Combine(snap.Words[TestReg], snap.Words[TestReg + 1]);
            Assert.True(BitConverter.SingleToUInt32Bits(combined) == bits,
                $"row {name}: bit-pattern roundtrip mismatch — expected 0x{bits:X8}, got 0x{BitConverter.SingleToUInt32Bits(combined):X8}");
        }
    }

    // ═══ 2. INC-06 truncated window: [00 14 00 A7] decodes byte-faithfully ═══

    [Fact]
    public void TruncatedSignature_BytesDecodeWordFaithful()
    {
        BusTestFactory.CreateFile(_mmfPath);
        // The defective foreign-writer window: high byte of every word zeroed.
        WriteRawBytes(TestReg, new byte[] { 0x00, 0x14, 0x00, 0xA7 });

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var rerr), rerr);
        var snap = reader.Snapshot();
        Assert.NotNull(snap);

        // Byte-faithful mirror — the reader NEVER normalizes: the exact two
        // words of the defective window surface as-is (detection duty is the
        // consumer's, not the bus layer's).
        Assert.Equal(0x0014, snap.Words[TestReg]);
        Assert.Equal(0x00A7, snap.Words[TestReg + 1]);
    }

    // ═══ 3. STALE-HOLD LAW (read side) — seeded cells survive the whole
    //        reader lifecycle byte-exact. ═══

    /// <summary>Open + snapshot + dispose ⇒ every seeded cell keeps its exact
    /// bytes — the reader never zeroes or reshapes (stale-hold).</summary>
    [Fact]
    public void StaleHold_SeededCellsSurviveReaderLifecycle_ByteExact()
    {
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, TestReg, BitConverter.Int32BitsToSingle(unchecked((int)0x3FA7AE14))); // live-defect pair
        BusTestFactory.WriteFloat(_mmfPath, TestReg + 2, 15.79f);
        BusTestFactory.WriteFloat(_mmfPath, TestReg + 4, -42.0f);
        BusTestFactory.WriteZoneHeader(_mmfPath, ZoneKind.Lan, DateTime.UtcNow.Ticks, active: true, count: 1);

        byte[] seeded0 = ReadRawBytes(TestReg);
        byte[] seeded1 = ReadRawBytes(TestReg + 2);
        byte[] seeded2 = ReadRawBytes(TestReg + 4);

        using (var reader = new BusReader(_mmfPath, _mapName, _mutexName))
        {
            Assert.True(reader.TryOpen(out var rerr), rerr);
            var snap = reader.Snapshot();
            Assert.NotNull(snap);
            Assert.True(snap.Lan.Active); // zone header seeds and survives
        } // reader disposed — the file keeps every byte

        Assert.True(seeded0.AsSpan().SequenceEqual(ReadRawBytes(TestReg)),
            "stale-hold breach: reg 600 bytes changed across the reader lifecycle");
        Assert.True(seeded1.AsSpan().SequenceEqual(ReadRawBytes(TestReg + 2)),
            "stale-hold breach: reg 602 bytes changed across the reader lifecycle");
        Assert.True(seeded2.AsSpan().SequenceEqual(ReadRawBytes(TestReg + 4)),
            "stale-hold breach: reg 604 bytes changed across the reader lifecycle");
    }

    /// <summary>Heartbeat-cell sovereignty: the AddIn's heartbeat cell (reg 3)
    /// survives the host reader's whole lifecycle (open → snapshot → dispose →
    /// reopen → snapshot) byte-exact — the host never zeroes or rewrites it.</summary>
    [Fact]
    public void ReaderLifecycle_NeverTouchesAddInHeartbeatCell()
    {
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteWord(_mmfPath, BusLayout.RegHeartbeat, 0x1234); // AddIn heartbeat bytes {0x12, 0x34}

        byte[] seeded = ReadBytes(BusLayout.RegHeartbeat, 2);

        using (var reader = new BusReader(_mmfPath, _mapName, _mutexName))
        {
            Assert.True(reader.TryOpen(out var err), err);
            Assert.NotNull(reader.Snapshot());
            reader.Dispose();               // stop…

            Assert.True(reader.TryOpen(out var err2), err2); // …and reopen (repeat-open law)
            Assert.NotNull(reader.Snapshot());
        }

        Assert.True(seeded.AsSpan().SequenceEqual(ReadBytes(BusLayout.RegHeartbeat, 2)),
            "heartbeat-cell breach: reg 3 changed across the reader lifecycle");
    }

    // ── Helpers ──

    /// <summary>Raw 4 bytes of one CDAB float pair, read straight off the
    /// persisted file (mutex-free — the byte-level view a foreign reader sees).</summary>
    private byte[] ReadRawBytes(int reg) => ReadBytes(reg, 4);

    private byte[] ReadBytes(int reg, int count)
    {
        var buf = new byte[count];
        using var fs = new FileStream(_mmfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = BusLayout.BodyOffset + reg * 2L;
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>Raw 4 bytes written directly to the file at `reg` (test-side producer surrogate).</summary>
    private void WriteRawBytes(int reg, byte[] bytes)
    {
        using var fs = new FileStream(_mmfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Position = BusLayout.BodyOffset + reg * 2L;
        fs.Write(bytes);
    }

    private static bool IsNaN(uint bits) =>
        (bits & 0x7F800000) == 0x7F800000 && (bits & 0x007FFFFF) != 0;
}
