using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

/// <summary>
/// BusWriter byte-layout tests (bus register map v5). Each test runs against a
/// fresh temp backing file (BackingPathOverride hook); static state ⇒ sequential
/// execution (see TestAssembly).
/// </summary>
public class BusWriterTests : IDisposable
{
    private readonly string _path;

    public BusWriterTests()
    {
        // Unique file + unique SECTION/MUTEX names per test: Windows section names are
        // process-global, so a stale process holding the production section would make
        // our mapping silently attach to the OLD file (byte asserts would see zeros).
        string id = Guid.NewGuid().ToString("N");
        _path = Path.Combine(Path.GetTempPath(), "icemdatamonitorbus_test_" + id + ".dat");
        BusWriter.BackingPathOverride = _path;
        BusWriter.BusNameOverride = @"Global\iCemDataMonitorBus_test_" + id;
        BusWriter.MutexNameOverride = @"Global\iCemDataMonitorBusMutex_test_" + id;
    }

    public void Dispose()
    {
        BusWriter.BackingPathOverride = null;
        BusWriter.BusNameOverride = null;
        BusWriter.MutexNameOverride = null;
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // temp cleanup best-effort
        }
    }

    private static string Hex(byte[] b, int off, int n)
    {
        string s = "";
        for (int i = 0; i < n; i++)
        {
            s += b[off + i].ToString("X2") + " ";
        }
        return s.Trim();
    }

    private byte[] ReadFile()
    {
        return File.ReadAllBytes(_path);
    }

    private static byte[] CraftShapeFile(int regCount, ushort layoutVer)
    {
        int len = 64 + regCount * 2;
        byte[] f = new byte[len];
        f[0] = 0x53; f[1] = 0x54; f[2] = 0x4E; f[3] = 0x42;                // 'STNB'
        f[4] = 0x01; f[5] = 0x00;                                          // version 1
        f[6] = (byte)(layoutVer & 0xFF); f[7] = (byte)((layoutVer >> 8) & 0xFF);
        f[8] = (byte)(regCount & 0xFF); f[9] = (byte)((regCount >> 8) & 0xFF);
        for (int i = 64; i < len; i++)
        {
            f[i] = 0x5A;                                                    // sentinel body fill
        }
        return f;
    }

    [Fact]
    public void Open_CreatesFile_V5Full()
    {
        Assert.False(File.Exists(_path));
        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk);
        }

        byte[] b = ReadFile();
        Assert.Equal(BusLayout.TotalFileBytes, b.Length); // 20066
        Assert.Equal("53 54 4E 42", Hex(b, 0, 4));        // 'STNB'
        Assert.Equal("01 00", Hex(b, 4, 2));               // version LE
        Assert.Equal("05 00", Hex(b, 6, 2));               // layoutVer LE = 5
        Assert.Equal("11 27", Hex(b, 8, 2));               // regCount LE = 10001 (D3)
        // zone blocks + counters zero on fresh creation
        Assert.Equal(0, b[BusLayout.ZoneComActiveFlagOffset]);
        Assert.Equal(0, b[BusLayout.ZoneLanActiveFlagOffset]);
        Assert.Equal(0, b[BusLayout.ZoneRawActiveFlagOffset]);
        Assert.Equal(0, b[BusLayout.ZoneWitsActiveFlagOffset]);
        // reserved-zero block 49..63
        for (int i = 49; i < 64; i++)
        {
            Assert.Equal(0, b[i]);
        }
        // body all zero
        for (int i = BusLayout.BodyOffset; i < b.Length; i++)
        {
            Assert.Equal(0, b[i]);
        }
    }

    [Fact]
    public void WriteReg16_WritesBigEndianBytes_AndHeaderStateRegs()
    {
        using (var w = BusWriter.Open())
        {
            w.WriteReg16(BusLayout.RegComPort, 1);
            w.WriteReg16(BusLayout.RegCaseState, 2);
            w.WriteReg16(BusLayout.RegCaseLanState, 4);
            w.WriteReg16(BusLayout.RegHeartbeat, unchecked((short)0xABCD));
        }

        byte[] b = ReadFile();
        Assert.Equal("00 01", Hex(b, 64, 2));    // reg0 = 1 (comPortActive)
        Assert.Equal("00 02", Hex(b, 66, 2));    // reg1 = 2 (caseState)
        Assert.Equal("00 04", Hex(b, 68, 2));    // reg2 = 4 (caseLanState)
        Assert.Equal("AB CD", Hex(b, 70, 2));    // reg3 = 0xABCD (heartbeat)
    }

    [Fact]
    public void WriteReg16_NeverWrites600Plus()
    {
        using (var w = BusWriter.Open())
        {
            w.WriteReg16(600, 7);    // ace_full reserve floor — silent no-op
            w.WriteReg16(4899, 7);   // mid-reserve — silent no-op
            w.WriteReg16(10000, 7);  // ace_full reserve end — silent no-op
            w.WriteReg16(-1, 7);     // negative — silent no-op
        }

        byte[] b = ReadFile();
        for (int i = 1264; i < b.Length; i++)
        {
            Assert.Equal(0, b[i]);
        }
    }

    [Fact]
    public void WriteFloatPair_ComStream_AtReg4_FullCdab_AndComLiveness()
    {
        // v5: com_stream starts at reg 4 (state block is regs 0..3 ONLY — the
        // v4 rebase/skip rule is GONE)
        using (var w = BusWriter.Open())
        {
            w.WriteFloatPair(ZoneKind.ComMain, 4, 1.0f);
            w.WriteFloatPair(ZoneKind.ComMain, 6, 15.79f);
        }

        byte[] b = ReadFile();
        Assert.Equal("00 00 3F 80", Hex(b, 72, 4));    // 1.0f CDAB at reg4 (offset 64+8)
        Assert.Equal("A3 D7 41 7C", Hex(b, 76, 4));    // 15.79f CDAB at reg6

        Assert.Equal(1, b[BusLayout.ZoneComActiveFlagOffset]);
        Assert.Equal("02 00 00 00", Hex(b, BusLayout.ProducerComWriteCountOffset, 4));
        bool anyTick = false;
        for (int i = 0; i < 8; i++)
        {
            anyTick |= b[BusLayout.ZoneComLastWriteTickOffset + i] != 0;
        }
        Assert.True(anyTick);
    }

    [Fact]
    public void WriteFloatPair_LanStreamCrio_FullCdab_MarksLanLivenessBlock()
    {
        // INC-06 fix: lan_stream_crio writes are FULL 4-byte CDAB — no byte-width
        // truncation. D4: the lan liveness block (tick@19..26 / flag@27 /
        // counter@41..44) is ADDIN-OWNED and marked per pair write — same
        // pattern as com/raw.
        using (var w = BusWriter.Open())
        {
            w.WriteFloatPair(ZoneKind.Lan, 200, 15.79f);
            w.WriteFloatPair(ZoneKind.Lan, 202, -12.5f);
        }

        byte[] b = ReadFile();
        Assert.Equal("A3 D7 41 7C", Hex(b, 464, 4));   // 15.79f at reg200 (offset 64+400)
        Assert.Equal("00 00 C1 48", Hex(b, 468, 4));   // -12.5f at reg202

        // D4: lan liveness block marked by the AddIn
        Assert.Equal(1, b[BusLayout.ZoneLanActiveFlagOffset]);
        Assert.Equal("02 00 00 00", Hex(b, BusLayout.ProducerLanWriteCountOffset, 4)); // 2 pairs
        bool anyTick = false;
        for (int i = 0; i < 8; i++)
        {
            anyTick |= b[BusLayout.ZoneLanLastWriteTickOffset + i] != 0;
        }
        Assert.True(anyTick);
    }

    [Fact]
    public void LanFlag_ZeroOnChannelLoss_DataCellsRetained()
    {
        // D4 + STALE-HOLD: flag 0 = hold; the data pair is NEVER zeroed.
        using (var w = BusWriter.Open())
        {
            w.WriteFloatPair(ZoneKind.Lan, 200, 15.79f);
            w.SetZoneActive(ZoneKind.Lan, false); // channel loss / idle hold
        }

        byte[] b = ReadFile();
        Assert.Equal(0, b[BusLayout.ZoneLanActiveFlagOffset]);
        Assert.Equal("A3 D7 41 7C", Hex(b, 464, 4)); // data retained — never zeroed
    }

    [Fact]
    public void WriteFloatPair_ZoneGuards_SilentNoOps()
    {
        using (var w = BusWriter.Open())
        {
            w.WriteFloatPair(ZoneKind.ComMain, 3, 1.0f);    // below com floor (state block) — no-op
            w.WriteFloatPair(ZoneKind.ComMain, 199, 1.0f);  // odd / beyond top — no-op
            w.WriteFloatPair(ZoneKind.Lan, 199, 1.0f);      // below lan floor — no-op
            w.WriteFloatPair(ZoneKind.Lan, 399, 1.0f);      // beyond lan top — no-op
            w.WriteFloatPair(ZoneKind.RawReserve, 400, 1.0f); // raw zone pairs = WriteRawLine batch only — no-op
            w.WriteFloatPair(ZoneKind.Wits, 600, 1.0f);     // dead — no-op
            w.WriteFloatPair(ZoneKind.LanExt, 650, 1.0f);   // dead — no-op
        }

        byte[] b = ReadFile();
        for (int i = BusLayout.BodyOffset; i < b.Length; i++)
        {
            Assert.Equal(0, b[i]);
        }
        Assert.Equal("00 00 00 00", Hex(b, BusLayout.ProducerComWriteCountOffset, 4));
        Assert.Equal("00 00 00 00", Hex(b, BusLayout.ProducerLanWriteCountOffset, 4));
        Assert.Equal("00 00 00 00", Hex(b, BusLayout.ProducerRawWriteCountOffset, 4));
    }

    [Fact]
    public void WriteInt32Pair_ComStream_RawBits_AndLiveness()
    {
        // contract v5.1 CHANGE 1 (user decree 2026-08-18, Э2b T01): com idx0
        // carries UnixTimestampSeconds as RAW int32 bits in the CDAB word
        // order — an int32 read of regs 4..5 in the standard word order yields
        // the seconds. Byte pin: 1787037218 = 0x6A840622 → low word 0x0622 @
        // reg4 BE, high word 0x6A84 @ reg5 BE.
        using (var w = BusWriter.Open())
        {
            w.WriteInt32Pair(ZoneKind.ComMain, BusLayout.ComTimestampReg, 1787037218);
        }

        byte[] b = ReadFile();
        Assert.Equal("06 22 6A 84", Hex(b, 72, 4));    // int32 bits at reg4 (offset 64+8)
        Assert.Equal(1, b[BusLayout.ZoneComActiveFlagOffset]);
        Assert.Equal("01 00 00 00", Hex(b, BusLayout.ProducerComWriteCountOffset, 4));
    }

    [Fact]
    public void WriteInt32Pair_ZoneGuards_SilentNoOps()
    {
        using (var w = BusWriter.Open())
        {
            w.WriteInt32Pair(ZoneKind.ComMain, 3, 1);      // below com floor — no-op
            w.WriteInt32Pair(ZoneKind.ComMain, 199, 1);    // beyond com top — no-op
            w.WriteInt32Pair(ZoneKind.Lan, 199, 1);        // below lan floor — no-op
            w.WriteInt32Pair(ZoneKind.RawReserve, 400, 1); // raw zone = WriteRawLine batch only — no-op
            w.WriteInt32Pair(ZoneKind.Wits, 600, 1);       // dead — no-op
        }

        byte[] b = ReadFile();
        for (int i = BusLayout.BodyOffset; i < b.Length; i++)
        {
            Assert.Equal(0, b[i]);
        }
        Assert.Equal("00 00 00 00", Hex(b, BusLayout.ProducerComWriteCountOffset, 4));
    }

    [Fact]
    public void WriteRawLine_CsvFields_AsFloatCdab_UpdatesRawLivenessOnce()
    {
        // contract v5.1 CHANGE 2 (user decree 2026-08-18, Э2b C01/C02): the line
        // is CSV-split POSITIONAL; field i → float32 CDAB pair at 400+i*2;
        // unparseable fields are NOT written. The u16-per-char path is RETIRED.
        using (var w = BusWriter.Open())
        {
            w.WriteRawLine("1.5,abc,2.5");
        }

        byte[] b = ReadFile();
        // reg400..401 = field0 1.5f CDAB; reg402..403 = field1 "abc" NOT written
        // (stays initial zero — never written, ¬ zeroed); reg404..405 = field2 2.5f
        Assert.Equal("00 00 3F C0", Hex(b, 864, 4));
        Assert.Equal("00 00 00 00", Hex(b, 868, 4));
        Assert.Equal("00 00 40 20", Hex(b, 872, 4));
        // tail beyond the line's fields stays zero
        Assert.Equal("00 00 00 00", Hex(b, 876, 4));
        // raw liveness updated exactly once (line-ARRIVAL evidence)
        Assert.Equal(1, b[BusLayout.ZoneRawActiveFlagOffset]);
        Assert.Equal("01 00 00 00", Hex(b, BusLayout.ProducerRawWriteCountOffset, 4));
        bool anyTick = false;
        for (int i = 0; i < 8; i++)
        {
            anyTick |= b[BusLayout.ZoneRawLastWriteTickOffset + i] != 0;
        }
        Assert.True(anyTick);
    }

    [Fact]
    public void WriteRawLine_TruncatesAt100Fields_AndShorterLineStaleHoldsTail()
    {
        // STALE-HOLD (Э2b C03/C04): a shorter later line leaves the previous tail
        // FIELD PAIRS in place (never zeroed); fields beyond index 99 are dropped.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 150; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append("1.5");
        }
        string longLine = sb.ToString();
        using (var w = BusWriter.Open())
        {
            w.WriteRawLine(longLine);
            w.WriteRawLine("9");
        }

        byte[] b = ReadFile();
        Assert.Equal("00 00 41 10", Hex(b, 864, 4));              // field0 = 9.0f
        Assert.Equal("00 00 3F C0", Hex(b, 868, 4));              // field1 = stale 1.5f (tail persists)
        // last pair of the zone (regs 598..599) = field 99 = long line's 1.5f
        Assert.Equal("00 00 3F C0", Hex(b, 864 + 199 * 2 - 2, 4));
        // canary: fields 100..149 never reached reg 600+ (ace_full floor)
        Assert.Equal("00 00", Hex(b, 1264, 2));
        Assert.Equal("02 00 00 00", Hex(b, BusLayout.ProducerRawWriteCountOffset, 4));
    }

    [Fact]
    public void WriteRawLine_EmptyOrNull_IsNoOp()
    {
        using (var w = BusWriter.Open())
        {
            w.WriteRawLine(null);
            w.WriteRawLine("");
        }

        byte[] b = ReadFile();
        Assert.Equal("00 00 00 00", Hex(b, BusLayout.ProducerRawWriteCountOffset, 4));
        Assert.Equal(0, b[BusLayout.ZoneRawActiveFlagOffset]);
    }

    [Fact]
    public void Reopen_Validates_AndPreservesCounters()
    {
        using (var w = BusWriter.Open())
        {
            w.WriteFloatPair(ZoneKind.ComMain, 4, 1.0f);
        }
        using (var w2 = BusWriter.Open())
        {
            Assert.True(w2.IsOk);
            w2.WriteFloatPair(ZoneKind.ComMain, 6, 2.5f);
        }

        byte[] b = ReadFile();
        Assert.Equal("02 00 00 00", Hex(b, BusLayout.ProducerComWriteCountOffset, 4));
        Assert.Equal("00 00 40 20", Hex(b, 76, 4));     // 2.5f at reg6
    }

    [Fact]
    public void CorruptMagic_DisablesWriter_AndNeverThrows()
    {
        using (var w = BusWriter.Open())
        {
            w.WriteFloatPair(ZoneKind.ComMain, 4, 1.0f);
        }
        using (FileStream fs = File.OpenWrite(_path))
        {
            fs.Position = 0;
            fs.WriteByte(0x58); // break 'STNB' → 'XTNB'
        }

        using (var wBad = BusWriter.Open())
        {
            Assert.False(wBad.IsOk);
            wBad.WriteReg16(0, 42);                       // must be silent no-ops
            wBad.WriteFloatPair(ZoneKind.ComMain, 4, 9.0f);
            wBad.SetZoneActive(ZoneKind.ComMain, false);
        }
    }

    [Fact]
    public void WrongVersion_DisablesWriter()
    {
        using (var w = BusWriter.Open()) { }
        using (FileStream fs = File.OpenWrite(_path))
        {
            fs.Position = BusLayout.HeaderVersionOffset;
            fs.WriteByte(2); // version 2 → unsupported
            fs.WriteByte(0);
        }

        using (var wBad = BusWriter.Open())
        {
            Assert.False(wBad.IsOk);
        }
    }

    [Fact]
    public void WrongFileSize_DisablesWriter()
    {
        using (FileStream fs = File.Create(_path))
        {
            fs.SetLength(2000); // ≠ 64 + regCount×2 for any header shape → foreign → disabled
        }

        using (var wBad = BusWriter.Open())
        {
            Assert.False(wBad.IsOk);
        }
    }

    [Fact]
    public void SetZoneActive_TogglesComLanRaw_WitsIsNoOp()
    {
        using (var w = BusWriter.Open())
        {
            w.WriteFloatPair(ZoneKind.ComMain, 4, 1.0f);
            w.SetZoneActive(ZoneKind.ComMain, false);
            w.SetZoneActive(ZoneKind.RawReserve, true);
            w.SetZoneActive(ZoneKind.Lan, true);   // D4: lan flag = AddIn-owned — real
            w.SetZoneActive(ZoneKind.Wits, true);  // dead — no-op
        }

        byte[] b = ReadFile();
        Assert.Equal(0, b[BusLayout.ZoneComActiveFlagOffset]);
        Assert.Equal(1, b[BusLayout.ZoneRawActiveFlagOffset]);
        Assert.Equal(1, b[BusLayout.ZoneLanActiveFlagOffset]);
        Assert.Equal(0, b[BusLayout.ZoneWitsActiveFlagOffset]);
    }

    [Fact]
    public void ConcurrentPairWrites_NeverTear_AndCountIsExact()
    {
        // INC-12 liveness law: the zone counter counts FRESH (changed-value)
        // pair writes. Every iteration writes a UNIQUE value ⇒ every write is
        // a change ⇒ the counter is exactly 400 (deterministic), and a torn
        // mid-pair read would surface as a value outside the two ranges.
        long timeouts;
        using (var w = BusWriter.Open())
        {
            var t1 = new Thread(() => { for (int i = 0; i < 200; i++) w.WriteFloatPair(ZoneKind.ComMain, 8, i + 1.0f); });
            var t2 = new Thread(() => { for (int i = 0; i < 200; i++) w.WriteFloatPair(ZoneKind.ComMain, 8, i + 1001.0f); });
            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();
            timeouts = w.LockTimeouts;
        }

        byte[] b = ReadFile();
        ushort low = (ushort)((b[80] << 8) | b[81]);
        ushort high = (ushort)((b[82] << 8) | b[83]);
        float final = CdabCodec.Combine(low, high); // reg8 → offset 64+16
        bool intact = (final >= 1.0f && final <= 200.0f) || (final >= 1001.0f && final <= 1200.0f);
        Assert.True(intact, "torn pair detected: bytes " + Hex(b, 80, 4) + " → " + final.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("90 01 00 00", Hex(b, BusLayout.ProducerComWriteCountOffset, 4)); // 400 fresh writes
        Assert.Equal(0L, timeouts);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndReleasesFileLock()
    {
        var w = BusWriter.Open();
        w.Dispose();
        w.Dispose();
        File.Delete(_path);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Open_WhileFileHeldOpenByReadWriteStream_Succeeds()
    {
        // DEFECT-1 regression: a concurrent ReadWrite holder (iCemDataMonitor.Tray BusReader,
        // dying iCem) must NOT cause a sharing violation.
        using (var w = BusWriter.Open())
        {
            w.WriteFloatPair(ZoneKind.ComMain, 4, 1.0f);
        }
        using (FileStream holder = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            using (var w = BusWriter.Open())
            {
                Assert.True(w.IsOk);
                w.WriteFloatPair(ZoneKind.ComMain, 6, 3.5f);
            }
        }

        byte[] b = ReadFile();
        Assert.Equal("00 00 40 60", Hex(b, 76, 4)); // 3.5f (0x40600000) at reg6
    }

    [Fact]
    public void SecondWriter_AttachesToExistingSection_AndWritesThrough()
    {
        using (var w1 = BusWriter.Open())
        {
            w1.WriteFloatPair(ZoneKind.ComMain, 4, 1.0f);
        }
        using (var w1 = BusWriter.Open())
        using (var w2 = BusWriter.Open())
        {
            Assert.True(w1.IsOk);
            Assert.True(w2.IsOk); // same-name section ⇒ attach, not exception
            w2.WriteFloatPair(ZoneKind.ComMain, 6, 2.5f);
        }

        byte[] b = ReadFile();
        Assert.Equal("00 00 40 20", Hex(b, 76, 4)); // 2.5f at reg6 via second writer
    }

    [Fact]
    public void Upgrade_V2File_ExpandsToV5Full_BodyPreserved_NewAreaZeroed()
    {
        // Exact v2 shape (CONTRACT_BusV2 §8): 650 regs / 1364 B, layoutVer 2.
        byte[] v2 = CraftShapeFile(BusLayout.RegCountV2, BusLayout.LayoutVersionV2);
        v2[10] = 0xAB;                                          // zone header sentinel (10..48 must survive)
        v2[76] = 0xA3; v2[77] = 0xD7; v2[78] = 0x41; v2[79] = 0x7C; // 15.79f pair at reg 6
        File.WriteAllBytes(_path, v2);

        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk, "v2 file must expand in place and open OK");
        }

        byte[] b = ReadFile();
        Assert.Equal(BusLayout.TotalFileBytes, b.Length);       // 20066
        Assert.Equal("53 54 4E 42", Hex(b, 0, 4));              // magic intact
        Assert.Equal("01 00", Hex(b, 4, 2));                    // version stays 1
        Assert.Equal("05 00", Hex(b, 6, 2));                    // layoutVer → 5
        Assert.Equal("11 27", Hex(b, 8, 2));                    // regCount → 10001
        Assert.Equal(0xAB, b[10]);                              // zone header 10..48 preserved
        // reserved-zero block 49..63 (v2 source: former wits zeroed)
        for (int i = 49; i < 64; i++)
        {
            Assert.Equal(0, b[i]);
        }
        // body 64..1363 preserved verbatim (v2 body = 650 regs)
        Assert.Equal(0x5A, b[64]);
        Assert.Equal(0x5A, b[1361]);
        Assert.Equal("A3 D7 41 7C", Hex(b, 76, 4));
        // new body 1364..20065 zeroed
        for (int i = 1364; i < b.Length; i++)
        {
            Assert.Equal(0, b[i]);
        }

        // post-upgrade writability: com pair at reg 4; the AddIn never WRITES 600+
        using (var w2 = BusWriter.Open())
        {
            Assert.True(w2.IsOk);
            w2.WriteFloatPair(ZoneKind.ComMain, 4, 3.5f);
        }
        b = ReadFile();
        Assert.Equal("00 00 40 60", Hex(b, 72, 4)); // 3.5f at reg4
        Assert.Equal(1, b[BusLayout.ZoneComActiveFlagOffset]);
        // regs 650+ (the expansion area, offset 1364+) stay zero; the preserved
        // v2 body (64..1363, incl. historical 600..649) is untouched by the upgrade
        for (int i = 1364; i < b.Length; i++)
        {
            Assert.Equal(0, b[i]);
        }
    }

    [Fact]
    public void Upgrade_V3SmallFile_ExpandsToV5Full_BodyPreserved()
    {
        // Exact v3-small shape: 1650 regs / 3364 B, layoutVer 3.
        byte[] v3 = CraftShapeFile(BusLayout.RegCountV3Small, BusLayout.LayoutVersionV3);
        v3[3362] = 0xEE; v3[3363] = 0xFF;                       // lan_ext tail sentinel
        File.WriteAllBytes(_path, v3);

        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk, "v3-small file must expand in place and open OK");
            w.WriteFloatPair(ZoneKind.ComMain, 4, 15.79f);
        }

        byte[] b = ReadFile();
        Assert.Equal(BusLayout.TotalFileBytes, b.Length);       // 20066
        Assert.Equal("05 00", Hex(b, 6, 2));                    // layoutVer → 5
        Assert.Equal("11 27", Hex(b, 8, 2));                    // regCount → 10001
        Assert.Equal("A3 D7 41 7C", Hex(b, 72, 4));             // 15.79f at reg 4
        Assert.Equal(0x5A, b[1364]);                            // body beyond view untouched
        Assert.Equal("EE FF", Hex(b, 3362, 2));                 // lan_ext tail intact
        // expansion tail zeroed
        for (int i = 3364; i < b.Length; i++)
        {
            Assert.Equal(0, b[i]);
        }
    }

    [Fact]
    public void Upgrade_V4FullFile_ExpandsToV5Full_BodyPreserved_TailZeroed_NoWitsZeroing()
    {
        // FULL MIGRATION (user decision 2026-08-17): v4-full (8404/16872,
        // layoutVer 4) is an UPGRADE SOURCE — expanded IN PLACE to 10001/20066:
        // layoutVer=5, regCount=10001, body 64..16871 preserved byte-exact, tail
        // 16872..20065 zero-filled. Header 49..63 need NO re-zeroing (reserved-zero
        // in every v4-full file by induction) — the expansion must NOT touch them:
        // a crafted sentinel there survives byte-exact.
        const int v4RegCount = 8404;
        byte[] v4 = CraftShapeFile(v4RegCount, BusLayout.LayoutVersionV4);
        v4[76] = 0x11; v4[100] = 0x22; v4[2000] = 0x33;         // body sentinels
        v4[49] = 0xAB; v4[63] = 0xCD;                           // 49..63 sentinels (must survive — no re-zeroing)
        File.WriteAllBytes(_path, v4);

        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk, "v4-full file must expand in place and open OK");
            w.WriteReg16(BusLayout.RegHeartbeat, 7);
        }

        byte[] b = ReadFile();
        Assert.Equal(BusLayout.TotalFileBytes, b.Length);       // 20066 — reshaped
        Assert.Equal("53 54 4E 42", Hex(b, 0, 4));              // magic intact
        Assert.Equal("01 00", Hex(b, 4, 2));                    // version stays 1
        Assert.Equal("05 00", Hex(b, 6, 2));                    // layoutVer → 5
        Assert.Equal("11 27", Hex(b, 8, 2));                    // regCount → 10001
        Assert.Equal(0xAB, b[49]);                              // 49..63 NOT re-zeroed (sentinel survives)
        Assert.Equal(0xCD, b[63]);
        // body 64..16871 preserved byte-exact
        Assert.Equal(0x11, b[76]);
        Assert.Equal(0x22, b[100]);
        Assert.Equal(0x33, b[2000]);
        Assert.Equal(0x5A, b[16871]);
        // tail 16872..20065 zero-filled
        for (int i = 16872; i < b.Length; i++)
        {
            Assert.Equal(0, b[i]);
        }
        Assert.Equal("00 07", Hex(b, 70, 2));                   // reg3 heartbeat = 7 (writable post-upgrade)
    }

    [Fact]
    public void Upgrade_V3FullFile_ExpandsToV5Full_BodyPreserved()
    {
        // FULL MIGRATION: v3-full (8404/16872, layoutVer 3) migrates exactly like
        // v4-full — same geometry, expanded to 10001/20066.
        const int v3RegCount = 8404;
        byte[] v3 = CraftShapeFile(v3RegCount, BusLayout.LayoutVersionV3);
        v3[16870] = 0xEE; v3[16871] = 0xFF;                     // tail sentinel
        v3[49] = 0xAB; v3[63] = 0xCD;                           // 49..63 must survive (no re-zeroing)
        File.WriteAllBytes(_path, v3);

        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk, "v3-full file must expand in place and open OK");
        }

        byte[] b = ReadFile();
        Assert.Equal(BusLayout.TotalFileBytes, b.Length);       // 20066
        Assert.Equal("05 00", Hex(b, 6, 2));                    // layoutVer → 5
        Assert.Equal("11 27", Hex(b, 8, 2));                    // regCount → 10001
        Assert.Equal(0xAB, b[49]);                              // 49..63 NOT touched
        Assert.Equal(0xCD, b[63]);
        Assert.Equal("EE FF", Hex(b, 16870, 2));                // old tail preserved byte-exact
        for (int i = 16872; i < b.Length; i++)
        {
            Assert.Equal(0, b[i]);                              // new tail zero-filled
        }
    }

    [Fact]
    public void Upgrade_BlockedByStaleSection_FailsFastWithOrderedRestartRunbook()
    {
        // INC-03: the named section capacity is frozen until ALL handles close.
        // Synthetic contention: a v4-full file + a raw live section held at the
        // OLD 16872 capacity (simulating a stale POC holder). The file upgrades
        // on disk, but the sized view is blocked ⇒ the writer fails fast with the
        // typed BusAccessDeniedException carrying the ordered-restart runbook.
        const int v4RegCount = 8404;
        byte[] v4 = CraftShapeFile(v4RegCount, BusLayout.LayoutVersionV4);
        File.WriteAllBytes(_path, v4);

        using (FileStream holder = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (MemoryMappedFile staleSection = MemoryMappedFile.CreateFromFile(
            holder, BusWriter.BusNameOverride, 16872,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false))
        {
            using (var w = BusWriter.Open())
            {
                Assert.False(w.IsOk, "blocked expansion ⇒ writer disabled");
                Assert.NotNull(w.LastOpenError);
                BusAccessDeniedException typed = Assert.IsType<BusAccessDeniedException>(w.LastOpenError);
                Assert.Contains("ACCESS DENIED", typed.Message);
                Assert.Contains("10001 regs", BusAccessDeniedException.OrderedRestartRunbook);
                Assert.Contains("producer FIRST", BusAccessDeniedException.OrderedRestartRunbook);
                Assert.Contains("20066", typed.Message);
            }
        }

        // the on-disk file WAS upgraded (the runbook applies to the live section)
        Assert.Equal(BusLayout.TotalFileBytes, new FileInfo(_path).Length);

        // after the stale holder closes, a reopen attaches the v5-full shape and writes through
        using (var w2 = BusWriter.Open())
        {
            Assert.True(w2.IsOk, "reopen after holders closed must succeed");
            w2.WriteFloatPair(ZoneKind.ComMain, 4, 15.79f);
        }
        Assert.Equal("A3 D7 41 7C", Hex(ReadFile(), 72, 4));
    }

    [Fact]
    public void Open_ArbitraryLegacyShape_IsForeign_DisablesWriter()
    {
        // FULL MIGRATION: arbitrary legacy shapes (layoutVer 3, non-source
        // regCount) are FOREIGN — the former semantic-bump path is REMOVED.
        byte[] v3g = CraftShapeFile(700, BusLayout.LayoutVersionV3);
        File.WriteAllBytes(_path, v3g);

        using (var w = BusWriter.Open())
        {
            Assert.False(w.IsOk);
            w.WriteFloatPair(ZoneKind.ComMain, 4, 15.79f); // silent no-op
        }

        byte[] b = ReadFile();
        Assert.Equal(64 + 700 * 2, b.Length);                   // file untouched
        Assert.Equal(0x03, b[6]);                               // layoutVer untouched (no bump)
    }

    [Fact]
    public void V1File_IsDead_DisablesWriter()
    {
        // v1 (600/1264, layoutVer 1) is OUT of the law — fail-safe, no upgrade path.
        byte[] v1 = CraftShapeFile(600, 1);
        File.WriteAllBytes(_path, v1);

        using (var w = BusWriter.Open())
        {
            Assert.False(w.IsOk);
        }

        byte[] b = ReadFile();
        Assert.Equal(1264, b.Length);                           // file untouched
        Assert.Equal(0x01, b[6]);                               // layoutVer untouched
    }

    [Theory]
    // Foreign shapes ⇒ writer disabled (never throws, never rewrites).
    [InlineData(3, 1650, 3400, "wrong length for 1650 regs")]
    [InlineData(5, 8404, 16870, "v5 file wrong length")]
    [InlineData(9, 1650, 3364, "future layoutVer")]
    [InlineData(5, 649, 1362, "below MinRegCount")]
    [InlineData(2, 650, 2000, "wrong length for 650 regs")]
    public void ForeignShapes_DisableWriter(int layoutVer, int regCount, int fileLength, string label)
    {
        byte[] f = new byte[fileLength];
        f[0] = 0x53; f[1] = 0x54; f[2] = 0x4E; f[3] = 0x42;                    // 'STNB'
        f[4] = 0x01; f[5] = 0x00;                                             // version 1
        f[6] = (byte)(layoutVer & 0xFF); f[7] = (byte)((layoutVer >> 8) & 0xFF);
        f[8] = (byte)(regCount & 0xFF); f[9] = (byte)((regCount >> 8) & 0xFF);
        File.WriteAllBytes(_path, f);

        using (var w = BusWriter.Open())
        {
            Assert.False(w.IsOk, label);
            w.WriteFloatPair(ZoneKind.ComMain, 4, 1.0f); // silent no-op
        }

        byte[] after = ReadFile();
        Assert.Equal(fileLength, after.Length);           // file untouched
        Assert.Equal((byte)(layoutVer & 0xFF), after[6]); // header untouched
    }
}
