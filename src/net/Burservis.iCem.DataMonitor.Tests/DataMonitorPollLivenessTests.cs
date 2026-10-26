using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

/// <summary>
/// Regression pins for the 2026-08-29 data-flow fixes:
///
///   BUG 1 (com/lan zones never written while raw flows): the poll pipeline
///   project→ActiveCase→VariableProvider→map-matched-candidates is exercised
///   at the resolution/writing seam with faked vendor candidates (the house
///   pattern — the vendor objects themselves are UI-affine and untestable
///   in-proc). The per-gate diagnostics live in DataMonitorModule (POLL SKIP
///   lines, owner-facing) and are intentionally not unit-pinned here.
///
///   BUG 2 (INC-12 false "LAN active"): the zone liveness block (tick/flag/
///   counter) is refreshed ONLY by a FRESH pair write — raw bits differ from
///   the last pair at that address, with the change-state seeded FROM THE FILE
///   so a writer re-open cannot fake it. Frozen same-value rewrites (the
///   vendor tap replaying last-known values) keep the data bytes current but
///   stop refreshing liveness ⇒ the host 5 s age gate honestly reports
///   inactive. raw_com_line keeps line-ARRIVAL liveness (documented
///   asymmetry — a real serial line is genuine evidence even when it repeats).
///
///   BUG 3 (reg 2 vs regs 8..10): the header path writes ONLY regs 0..3 —
///   caseLanState lands at reg 2 and NOTHING ever writes regs 8..10 as state
///   (they are com_stream float-pair DATA in v5; the v4 surface is frozen).
/// </summary>
public class DataMonitorPollLivenessTests : IDisposable
{
    private readonly string _path;

    public DataMonitorPollLivenessTests()
    {
        string id = Guid.NewGuid().ToString("N");
        _path = Path.Combine(Path.GetTempPath(), "icemdatamonitorbus_pol_" + id + ".dat");
        BusWriter.BackingPathOverride = _path;
        BusWriter.BusNameOverride = @"Global\iCemDataMonitorBus_pol_" + id;
        BusWriter.MutexNameOverride = @"Global\iCemDataMonitorBusMutex_pol_" + id;
    }

    public void Dispose()
    {
        BusWriter.BackingPathOverride = null;
        BusWriter.BusNameOverride = null;
        BusWriter.MutexNameOverride = null;
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    // ── oracles (house pattern) ─────────────────────────────────────────────

    private byte[] ReadFile()
    {
        // The BusWriter holds the backing file open ReadWrite — read with
        // matching sharing (house pattern: DataMonitorEdgeTruthTableTests.ReadBytes).
        var buf = new byte[new FileInfo(_path).Length];
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }
        }
        return buf;
    }

    private static string Hex(byte[] b, int off, int n)
    {
        string s = "";
        for (int i = 0; i < n; i++)
        {
            s += b[off + i].ToString("X2") + (i + 1 < n ? " " : "");
        }
        return s.Trim();
    }

    private static string PairHex(float value)
    {
        ushort low, high;
        CdabCodec.Split(value, out low, out high);
        return ((low >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture) + " "
             + (low & 0xFF).ToString("X2", CultureInfo.InvariantCulture) + " "
             + ((high >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture) + " "
             + (high & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
    }

    private static long BodyOffsetOf(int reg)
    {
        return BusLayout.BodyOffset + reg * 2L;
    }

    private static KeyValuePair<string, object> Cand(string name, object value)
    {
        return new KeyValuePair<string, object>(name, value);
    }

    /// <summary>The module's exact com write sequence: resolve → per-entry
    /// dispatch (idx0 timestamp = int32 raw bits, everything else float CDAB).</summary>
    private static int WriteComLikeModule(BusWriter w, List<KeyValuePair<string, object>> candidates)
    {
        PollResolution.Outcome com = PollResolution.Resolve(candidates);
        foreach (PollResolution.Entry e in com.Entries)
        {
            w.WritePreparedPair(PollPairPreparation.PrepareCom(e));
        }
        return com.Entries.Count;
    }

    private static int WriteLanLikeModule(BusWriter w, List<KeyValuePair<string, object>> candidates)
    {
        PollResolution.Outcome lan = PollResolution.ResolveLan(candidates);
        foreach (PollResolution.Entry e in lan.Entries)
        {
            w.WritePreparedPair(PollPairPreparation.PrepareLan(e));
        }
        return lan.Entries.Count;
    }

    private static long ReadTick(byte[] b, int offset)
    {
        return BitConverter.ToInt64(b, offset);
    }

    private static uint ReadCounter(byte[] b, int offset)
    {
        return BitConverter.ToUInt32(b, offset);
    }

    // Tick literals (exact in double — both divisible by 128 with a 53-bit quotient):
    //   639226512000000000.0 → unix 1787054400 (0x6A844940)
    //   639226512010000000.0 → unix 1787054401 (0x6A844941)
    private const double TicksSec0 = 639226512000000000.0;
    private const double TicksSec1 = 639226512010000000.0;

    // ═══ BUG 1 — zones receive writes when candidates resolve ═══

    [Fact]
    public void Poll_Pipeline_ComCandidates_WriteComStreamZone_WithTimestampDispatch()
    {
        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk);
            // Vendor-shaped snapshot: 'Time & Date' arrives as .NET UTC ticks
            // (numeric — UnixTimestamp.cs evidence), the rest as template
            // floats; an unmatched vendor name is skipped at lookup.
            var snapshot = new List<KeyValuePair<string, object>>
            {
                Cand("Time & Date", TicksSec0),            // → 4 (int32 unix bits)
                Cand("Slurry Density", 1.5f),               // → 8
                Cand("Pump Rate", 15.79f),                  // → 14
                Cand("PS Pump Press", 99.0f),               // → 16 (alias of PumpPressure)
                Cand("Unknown Vendor Signal", 1.0f),        // unmatched → never written
            };
            int written = WriteComLikeModule(w, snapshot);
            Assert.Equal(4, written);
        }

        byte[] b = ReadFile();
        // idx0: unix 1787054400 = 0x6A844940 → CDAB word order (49 40 | 6A 84)
        Assert.Equal("49 40 6A 84", Hex(b, (int)BodyOffsetOf(4), 4));
        Assert.Equal(PairHex(1.5f), Hex(b, (int)BodyOffsetOf(8), 4));    // Slurry Density
        Assert.Equal(PairHex(15.79f), Hex(b, (int)BodyOffsetOf(14), 4)); // Pump Rate
        Assert.Equal(PairHex(99.0f), Hex(b, (int)BodyOffsetOf(16), 4));  // PS Pump Press
        // com liveness marked by the fresh batch
        Assert.Equal(1, b[BusLayout.ZoneComActiveFlagOffset]);
        Assert.Equal(4u, ReadCounter(b, BusLayout.ProducerComWriteCountOffset));
        Assert.True(ReadTick(b, BusLayout.ZoneComLastWriteTickOffset) > 0);
    }

    [Fact]
    public void Poll_Pipeline_LanCandidates_WriteLanStreamCrioZone()
    {
        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk);
            var snapshot = new List<KeyValuePair<string, object>>
            {
                Cand("Recirc Density", 15.79f),      // lan idx0 → 200
                Cand("Pump A Pressure", 125.93f),    // lan idx33 → 266
            };
            int written = WriteLanLikeModule(w, snapshot);
            Assert.Equal(2, written);
        }

        byte[] b = ReadFile();
        Assert.Equal(PairHex(15.79f), Hex(b, (int)BodyOffsetOf(200), 4));
        Assert.Equal(PairHex(125.93f), Hex(b, (int)BodyOffsetOf(266), 4));
        // lan liveness marked (D4)
        Assert.Equal(1, b[BusLayout.ZoneLanActiveFlagOffset]);
        Assert.Equal(2u, ReadCounter(b, BusLayout.ProducerLanWriteCountOffset));
        Assert.True(ReadTick(b, BusLayout.ZoneLanLastWriteTickOffset) > 0);
    }

    // ═══ BUG 2 — fresh-frame liveness (INC-12 amendment) ═══

    [Fact]
    public void LanLiveness_UnchangedRewrite_DoesNotRefreshTickFlagCounter()
    {
        using (var w = BusWriter.Open())
        {
            Assert.Equal(2, WriteLanLikeModule(w, new List<KeyValuePair<string, object>>
            {
                Cand("Recirc Density", 15.79f),
                Cand("Pump A Pressure", 125.93f),
            }));

            byte[] afterFirst = ReadFile();
            long tick1 = ReadTick(afterFirst, BusLayout.ZoneLanLastWriteTickOffset);
            uint count1 = ReadCounter(afterFirst, BusLayout.ProducerLanWriteCountOffset);
            Assert.Equal(2u, count1);
            Assert.Equal(1, afterFirst[BusLayout.ZoneLanActiveFlagOffset]);

            Thread.Sleep(20); // a refreshed tick would differ from tick1

            // The vendor tap replays the SAME last-known values for many ticks:
            for (int frozen = 0; frozen < 5; frozen++)
            {
                Assert.Equal(2, WriteLanLikeModule(w, new List<KeyValuePair<string, object>>
                {
                    Cand("Recirc Density", 15.79f),
                    Cand("Pump A Pressure", 125.93f),
                }));
            }

            byte[] afterFrozen = ReadFile();
            Assert.Equal(tick1, ReadTick(afterFrozen, BusLayout.ZoneLanLastWriteTickOffset)); // tick FROZEN
            Assert.Equal(count1, ReadCounter(afterFrozen, BusLayout.ProducerLanWriteCountOffset)); // counter FROZEN
            Assert.Equal(1, afterFrozen[BusLayout.ZoneLanActiveFlagOffset]); // in-window: flag stays 1
            // data bytes ARE kept current (recording continuity)
            Assert.Equal(PairHex(15.79f), Hex(afterFrozen, (int)BodyOffsetOf(200), 4));

            // Beyond the 5 s idle window the module's staleness drop applies —
            // and STAYS applied because no frozen rewrite re-raises the flag.
            w.SetZoneActive(ZoneKind.Lan, false);
            for (int frozen = 0; frozen < 3; frozen++)
            {
                WriteLanLikeModule(w, new List<KeyValuePair<string, object>>
                {
                    Cand("Recirc Density", 15.79f),
                    Cand("Pump A Pressure", 125.93f),
                });
            }
            byte[] afterDrop = ReadFile();
            Assert.Equal(0, afterDrop[BusLayout.ZoneLanActiveFlagOffset]); // honest inactive
            Assert.Equal(tick1, ReadTick(afterDrop, BusLayout.ZoneLanLastWriteTickOffset)); // still frozen
            Assert.Equal(count1, ReadCounter(afterDrop, BusLayout.ProducerLanWriteCountOffset));
            Assert.Equal(PairHex(15.79f), Hex(afterDrop, (int)BodyOffsetOf(200), 4)); // values keep flowing
        }
    }

    [Fact]
    public void LanLiveness_ValueChanged_ReraisesLiveness()
    {
        using (var w = BusWriter.Open())
        {
            WriteLanLikeModule(w, new List<KeyValuePair<string, object>>
            {
                Cand("Recirc Density", 15.79f),
                Cand("Pump A Pressure", 125.93f),
            });
            byte[] before = ReadFile();
            long tick1 = ReadTick(before, BusLayout.ZoneLanLastWriteTickOffset);
            uint count1 = ReadCounter(before, BusLayout.ProducerLanWriteCountOffset);

            Thread.Sleep(20);
            w.SetZoneActive(ZoneKind.Lan, false); // zone idle (flag 0)
            Assert.Equal(0, ReadFile()[BusLayout.ZoneLanActiveFlagOffset]);

            // A REAL fresh frame arrives — one value changes:
            Assert.Equal(2, WriteLanLikeModule(w, new List<KeyValuePair<string, object>>
            {
                Cand("Recirc Density", 16.25f),      // CHANGED
                Cand("Pump A Pressure", 125.93f),    // unchanged
            }));

            byte[] after = ReadFile();
            Assert.Equal(1, after[BusLayout.ZoneLanActiveFlagOffset]); // write re-raised
            Assert.True(ReadTick(after, BusLayout.ZoneLanLastWriteTickOffset) > tick1); // fresh tick
            // counter = FRESH pair-write count: only the CHANGED pair counts
            // (the unchanged Pump A Pressure rewrite is a frozen no-mark).
            Assert.Equal(count1 + 1, ReadCounter(after, BusLayout.ProducerLanWriteCountOffset));
            Assert.Equal(PairHex(16.25f), Hex(after, (int)BodyOffsetOf(200), 4)); // 16.25f landed
        }
    }

    [Fact]
    public void ComLiveness_TimestampPair_Int32Semantics_FreshVsFrozen()
    {
        // com idx0 carries UnixTimestampSeconds as int32 raw bits (v5.1 CHANGE 1):
        // a NEW second = fresh liveness; the same second replayed = frozen.
        using (var w = BusWriter.Open())
        {
            int sec0 = UnixTimestamp.TicksToUnixSeconds(TicksSec0); // 1787054400
            Assert.Equal(2, WriteComLikeModule(w, new List<KeyValuePair<string, object>>
            {
                Cand("Time & Date", TicksSec0),
                Cand("Slurry Density", 1.5f),
            }));

            byte[] before = ReadFile();
            long tick1 = ReadTick(before, BusLayout.ZoneComLastWriteTickOffset);
            uint count1 = ReadCounter(before, BusLayout.ProducerComWriteCountOffset);
            Assert.Equal(2u, count1);

            Thread.Sleep(20);
            // Frozen tap: same timestamp second + same density.
            WriteComLikeModule(w, new List<KeyValuePair<string, object>>
            {
                Cand("Time & Date", TicksSec0),
                Cand("Slurry Density", 1.5f),
            });
            byte[] frozen = ReadFile();
            Assert.Equal(tick1, ReadTick(frozen, BusLayout.ZoneComLastWriteTickOffset));
            Assert.Equal(count1, ReadCounter(frozen, BusLayout.ProducerComWriteCountOffset));

            Thread.Sleep(20);
            // Fresh frame: timestamp advances (new second), density unchanged.
            Assert.Equal(sec0 + 1, UnixTimestamp.TicksToUnixSeconds(TicksSec1));
            WriteComLikeModule(w, new List<KeyValuePair<string, object>>
            {
                Cand("Time & Date", TicksSec1),
                Cand("Slurry Density", 1.5f),
            });
            byte[] fresh = ReadFile();
            Assert.True(ReadTick(fresh, BusLayout.ZoneComLastWriteTickOffset) > tick1);
            // counter = FRESH pair-write count: the new timestamp counts, the
            // unchanged density does not.
            Assert.Equal(count1 + 1, ReadCounter(fresh, BusLayout.ProducerComWriteCountOffset));
            // int32 bits at regs 4..5 = 1787054401 = 0x6A844941 → CDAB (49 41 | 6A 84)
            Assert.Equal("49 41 6A 84", Hex(fresh, (int)BodyOffsetOf(4), 4));
        }
    }

    [Fact]
    public void WriterReopen_SeedsFromFile_FrozenRewriteAfterReopen_DoesNotFakeLiveness()
    {
        // Module retry loop (TryReopenBus) re-opens the writer mid-run: the
        // change-state must come from the FILE, not from the new instance.
        long tick1;
        uint count1;
        using (var w1 = BusWriter.Open())
        {
            WriteLanLikeModule(w1, new List<KeyValuePair<string, object>>
            {
                Cand("Recirc Density", 15.79f),
            });
            byte[] b1 = ReadFile();
            tick1 = ReadTick(b1, BusLayout.ZoneLanLastWriteTickOffset);
            count1 = ReadCounter(b1, BusLayout.ProducerLanWriteCountOffset);
        }

        using (var w2 = BusWriter.Open()) // reopen — same file, fresh instance
        {
            Assert.True(w2.IsOk);
            Thread.Sleep(20);
            // The tap replays the SAME value the file already holds:
            WriteLanLikeModule(w2, new List<KeyValuePair<string, object>>
            {
                Cand("Recirc Density", 15.79f),
            });
            byte[] b2 = ReadFile();
            Assert.Equal(tick1, ReadTick(b2, BusLayout.ZoneLanLastWriteTickOffset)); // NOT re-raised
            Assert.Equal(count1, ReadCounter(b2, BusLayout.ProducerLanWriteCountOffset)); // NOT counted

            // A genuinely new value after the reopen DOES re-raise:
            Thread.Sleep(20);
            WriteLanLikeModule(w2, new List<KeyValuePair<string, object>>
            {
                Cand("Recirc Density", 20.5f),
            });
            byte[] b3 = ReadFile();
            Assert.True(ReadTick(b3, BusLayout.ZoneLanLastWriteTickOffset) > tick1);
            Assert.Equal(count1 + 1, ReadCounter(b3, BusLayout.ProducerLanWriteCountOffset));
            Assert.Equal(1, b3[BusLayout.ZoneLanActiveFlagOffset]);
        }
    }

    [Fact]
    public void RawLiveness_LineArrival_MarksEvenIdenticalLine()
    {
        // Documented asymmetry: a real serial line arrival is genuine evidence
        // even when the text repeats — raw keeps line-ARRIVAL liveness.
        using (var w = BusWriter.Open())
        {
            w.WriteRawLine("1.5,2.5");
            byte[] b1 = ReadFile();
            long tick1 = ReadTick(b1, BusLayout.ZoneRawLastWriteTickOffset);
            uint count1 = ReadCounter(b1, BusLayout.ProducerRawWriteCountOffset);
            Assert.Equal(1u, count1);

            Thread.Sleep(20);
            w.WriteRawLine("1.5,2.5"); // identical text — still a REAL line
            byte[] b2 = ReadFile();
            Assert.True(ReadTick(b2, BusLayout.ZoneRawLastWriteTickOffset) > tick1);
            Assert.Equal(count1 + 1, ReadCounter(b2, BusLayout.ProducerRawWriteCountOffset));
            Assert.Equal(1, b2[BusLayout.ZoneRawActiveFlagOffset]);
        }
    }

    [Fact]
    public void LastLivenessMarkUtc_AdvancesOnlyOnFreshEvidence()
    {
        // The module staleness FSM consumes this writer-side clock (ONE change
        // detector): it must advance ONLY on fresh evidence — a changed pair or
        // a raw line arrival — and stay parked through frozen rewrites.
        using (var w = BusWriter.Open())
        {
            Assert.Equal(DateTime.MinValue, w.LastLivenessMarkUtc(ZoneKind.Lan));

            w.WriteFloatPair(ZoneKind.Lan, 200, 15.79f);
            DateTime fresh1 = w.LastLivenessMarkUtc(ZoneKind.Lan);
            Assert.True(fresh1 > DateTime.MinValue, "fresh pair write must advance the lan evidence clock");

            Thread.Sleep(20);
            w.WriteFloatPair(ZoneKind.Lan, 200, 15.79f); // frozen rewrite
            Assert.Equal(fresh1, w.LastLivenessMarkUtc(ZoneKind.Lan)); // parked

            Thread.Sleep(20);
            w.WriteFloatPair(ZoneKind.Lan, 200, 15.8f); // changed
            Assert.True(w.LastLivenessMarkUtc(ZoneKind.Lan) > fresh1);

            Thread.Sleep(20);
            w.WriteFloatPair(ZoneKind.ComMain, 8, 42.0f); // OTHER zone must not move lan
            Assert.True(w.LastLivenessMarkUtc(ZoneKind.ComMain) > DateTime.MinValue);
            Assert.True(w.LastLivenessMarkUtc(ZoneKind.Lan) < w.LastLivenessMarkUtc(ZoneKind.ComMain));

            Thread.Sleep(20);
            DateTime raw1 = w.LastLivenessMarkUtc(ZoneKind.RawReserve);
            Assert.Equal(DateTime.MinValue, raw1); // no raw line yet
            w.WriteRawLine("3.5");
            Assert.True(w.LastLivenessMarkUtc(ZoneKind.RawReserve) > DateTime.MinValue); // line arrival = fresh

            // historical zones are never marked
            Assert.Equal(DateTime.MinValue, w.LastLivenessMarkUtc(ZoneKind.Wits));
        }
    }

    // ═══ BUG 3 — reg 2 vs regs 8..10 (v5 owns the surface) ═══

    [Fact]
    public void HeaderPath_WritesReg2CaseLanState_NeverTouchesRegs8to10()
    {
        // The module's EXACT header set (DataMonitorModule.WriteHeaderRegisters):
        // regs 0..3 ONLY — one write each, every tick.
        using (var w = BusWriter.Open())
        {
            w.WriteReg16(BusLayout.RegComPort, 7);
            w.WriteReg16(BusLayout.RegCaseState, BusLayout.CaseStateInProgress);
            w.WriteReg16(BusLayout.RegCaseLanState, BusLayout.CaseLanStateStreaming);
            w.WriteReg16(BusLayout.RegHeartbeat, 29);
        }

        byte[] b = ReadFile();
        Assert.Equal("00 07", Hex(b, (int)BodyOffsetOf(0), 2));      // reg0
        Assert.Equal("00 02", Hex(b, (int)BodyOffsetOf(1), 2));      // reg1
        Assert.Equal("00 02", Hex(b, (int)BodyOffsetOf(2), 2));      // reg2 = caseLanState (Streaming) — THE lan state surface
        Assert.Equal("00 1D", Hex(b, (int)BodyOffsetOf(3), 2));      // reg3
        // regs 8..10: com_stream float-pair territory — header path wrote NOTHING there
        Assert.Equal(new byte[6], new[] { b[BodyOffsetOf(8)], b[BodyOffsetOf(8) + 1], b[BodyOffsetOf(9)], b[BodyOffsetOf(9) + 1], b[BodyOffsetOf(10)], b[BodyOffsetOf(10) + 1] });
    }

    [Fact]
    public void Regs8to10_AreComStreamData_HeaderWritesNeverClobberThem()
    {
        using (var w = BusWriter.Open())
        {
            // com DATA legitimately lands at regs 8 and 10 (v5 map: Slurry
            // Density → 8, DH Density → 10):
            w.WriteFloatPair(ZoneKind.ComMain, 8, 1.5f);
            w.WriteFloatPair(ZoneKind.ComMain, 10, 2.5f);

            byte[] withData = ReadFile();
            Assert.Equal(PairHex(1.5f), Hex(withData, (int)BodyOffsetOf(8), 4));
            Assert.Equal(PairHex(2.5f), Hex(withData, (int)BodyOffsetOf(10), 4));

            // Header writes (the module's set) must never touch the data:
            w.WriteReg16(BusLayout.RegComPort, 1);
            w.WriteReg16(BusLayout.RegCaseState, 2);
            w.WriteReg16(BusLayout.RegCaseLanState, 4);  // reg 2 — v5 lan state
            w.WriteReg16(BusLayout.RegHeartbeat, 100);

            byte[] after = ReadFile();
            Assert.Equal(PairHex(1.5f), Hex(after, (int)BodyOffsetOf(8), 4));  // reg 8 pair intact
            Assert.Equal(PairHex(2.5f), Hex(after, (int)BodyOffsetOf(10), 4)); // reg 10 pair intact
            Assert.Equal("00 04", Hex(after, (int)BodyOffsetOf(2), 2));        // reg 2 carries caseLanState
        }
    }
}
