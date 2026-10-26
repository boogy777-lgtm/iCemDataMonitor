using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

/// <summary>
/// Edge-domain truth-table tests (specs/iCemAddIn/Э2b_edge_truth_tables.csv —
/// EVERY CSV row is pinned here, house pattern after
/// server-dotnet/iCemDataMonitor.Data.Tests/BusWriteTruthTableTests.cs).
///
/// DA GATE findings (real code paths, read before writing the tables):
///   • There is NO template-load/event path: DataMonitorAddressMap is a static
///     compiled-in map; a COM template switch manifests ONLY as a changed name
///     set in the next 1 Hz GatherCandidates snapshot. Addr drift is therefore
///     structurally impossible (addr=4+idx*2 from the static map).
///   • The poll path is name-based and device-blind; source affinity splits
///     ONLY LAN (SourceComPort==17) vs the rest. The D1 latch gates reg0 +
///     raw_com_line ONLY — template change is not a latch input (no call site).
///   • There is NO LAN event handler (only ComPortPropertyChanged for serial).
///     Link up/down/flap manifest solely through the candidate set; liveness
///     falls out of ZoneStaleness.ShouldDrop + SetZoneActive (header-only).
///   • ResolveCaseLanState feeds reg2 ONLY (WriteHeaderRegisters); nothing in
///     the writer/staleness path reads LANStatus — passthrough never gates.
///   • The lan flag's ONLY raiser is UpdateZoneHeaderLocked inside a pair
///     write ⇒ flag=1 ∧ never-received is an UNREACHABLE input combination.
///
/// Each case runs against a fresh temp backing file (static overrides ⇒ the
/// assembly runs strictly sequentially, see TestAssembly).
/// </summary>
public class DataMonitorEdgeTruthTableTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    // ── temp-bus plumbing (per-case isolation) ──────────────────────────────

    private static BusWriter OpenTempBus(out string path)
    {
        string id = Guid.NewGuid().ToString("N");
        path = Path.Combine(Path.GetTempPath(), "icemdatamonitorbus_edge_" + id + ".dat");
        BusWriter.BackingPathOverride = path;
        BusWriter.BusNameOverride = @"Global\iCemDataMonitorBus_edge_" + id;
        BusWriter.MutexNameOverride = @"Global\iCemDataMonitorBusMutex_edge_" + id;
        BusWriter w = BusWriter.Open();
        Assert.True(w.IsOk, "temp bus must open");
        return w;
    }

    private static void CloseTempBus(BusWriter w, string path)
    {
        try { if (w != null) w.Dispose(); } catch { }
        BusWriter.BackingPathOverride = null;
        BusWriter.BusNameOverride = null;
        BusWriter.MutexNameOverride = null;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── byte oracles ────────────────────────────────────────────────────────

    /// <summary>CDAB words → big-endian bus bytes: [low.hi, low.lo, high.hi, high.lo].</summary>
    private static byte[] ExpectedBytes(float value)
    {
        ushort low, high;
        CdabCodec.Split(value, out low, out high);
        return new[] { (byte)(low >> 8), (byte)low, (byte)(high >> 8), (byte)high };
    }

    private static byte[] ReadBytes(string path, long offset, int count)
    {
        var buf = new byte[count];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Position = offset;
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buf, read, count - read);
                if (n <= 0) break;
                read += n;
            }
        }
        return buf;
    }

    private static byte[] ReadPair(string path, int reg)
    {
        return ReadBytes(path, BusLayout.BodyOffset + reg * 2L, 4);
    }

    private static byte ReadFlag(string path, int flagOffset)
    {
        return ReadBytes(path, flagOffset, 1)[0];
    }

    private static long ReadTick(string path, int tickOffset)
    {
        return BitConverter.ToInt64(ReadBytes(path, tickOffset, 8), 0);
    }

    private static uint ReadCounter(string path, int countOffset)
    {
        return BitConverter.ToUInt32(ReadBytes(path, countOffset, 4), 0);
    }

    private static void AssertPair(string path, int reg, float value, string ctx)
    {
        byte[] expected = ExpectedBytes(value);
        byte[] actual = ReadPair(path, reg);
        Assert.True(ByteEq(expected, actual),
            ctx + ": reg " + reg + " expected=[" + Hex(expected) + "] actual=[" + Hex(actual) + "]");
    }

    private static bool ByteEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static string Hex(byte[] b)
    {
        string s = "";
        for (int i = 0; i < b.Length; i++) s += b[i].ToString("X2") + (i + 1 < b.Length ? " " : "");
        return s;
    }

    private static KeyValuePair<string, object> Cand(string name, object value)
    {
        return new KeyValuePair<string, object>(name, value);
    }

    // ═══ DOMAIN A — COM template change (Э2b rows A01..A27) ═══
    // Old template T1: Slurry Density→8, Pump Rate→14, Pump Pressure→16.
    // varCount axis: same = same names new values; fewer = {Pump Pressure};
    // more = T1 + Tub Height→48 (known-new) + "Brand New Signal" (unknown).

    private static readonly List<KeyValuePair<string, object>> T1 = new List<KeyValuePair<string, object>>
    {
        Cand("Slurry Density", 1.5f),   // → 8
        Cand("Pump Rate", 15.79f),      // → 14
        Cand("Pump Pressure", -12.5f),  // → 16
    };

    public static IEnumerable<object[]> DomainARows()
    {
        string[] varCounts = { "same", "fewer", "more" };
        bool[] latches = { true, false };
        bool[] flowings = { true, false };
        bool[] sameDevices = { true, false };
        int n = 0;
        foreach (string vc in varCounts)
        foreach (bool latched in latches)
        foreach (bool flowing in flowings)
        foreach (bool sameDevice in sameDevices)
        {
            n++;
            yield return new object[] { "A" + n.ToString("D2"), vc, latched, flowing, sameDevice };
        }
    }

    [Theory]
    [MemberData(nameof(DomainARows))]
    public void DomainA_TemplateChange_TruthRow(string rowId, string varCount, bool latched, bool flowing, bool sameDevice)
    {
        string path;
        BusWriter w = OpenTempBus(out path);
        try
        {
            var latch = new ComFeedLatch();
            if (latched)
            {
                Assert.True(latch.OnLine("COM1", 1), rowId + ": first line latches");
            }
            int portBefore = latch.PortNumber; // 1 latched / 0 unlatched

            // Tick N (old template): resolve + write.
            PollResolution.Outcome before = PollResolution.Resolve(T1);
            Assert.Equal(3, before.Entries.Count);
            foreach (PollResolution.Entry e in before.Entries)
            {
                w.WritePreparedPair(PollPairPreparation.PrepareCom(e));
            }
            byte[] heartbeatBefore = ReadPair(path, BusLayout.RegHeartbeat);

            // Tick N+1 (template changed): build the new snapshot.
            var next = new List<KeyValuePair<string, object>>();
            if (flowing)
            {
                if (varCount == "same" || varCount == "more")
                {
                    next.Add(Cand("Slurry Density", 2.5f));
                    next.Add(Cand("Pump Rate", 3.5f));
                    next.Add(Cand("Pump Pressure", 4.5f));
                }
                else // fewer
                {
                    next.Add(Cand("Pump Pressure", 4.5f));
                }
                if (varCount == "more")
                {
                    next.Add(Cand("Tub Height", 7.0f));        // known-new → 48
                    next.Add(Cand("Brand New Signal", 9.9f));  // unknown → skipped
                }
            }
            PollResolution.Outcome after = PollResolution.Resolve(next);
            foreach (PollResolution.Entry e in after.Entries)
            {
                // addr law: addr=4+idx*2 inside com_stream — no drift, ever.
                Assert.InRange(e.Address, BusLayout.ComStreamBaseReg, 198);
                Assert.Equal(0, (e.Address - BusLayout.ComStreamBaseReg) % 2);
                w.WritePreparedPair(PollPairPreparation.PrepareCom(e));
            }

            if (flowing)
            {
                if (varCount == "same")
                {
                    Assert.Equal(3, after.Entries.Count);
                    AssertPair(path, 8, 2.5f, rowId);
                    AssertPair(path, 14, 3.5f, rowId);
                    AssertPair(path, 16, 4.5f, rowId);
                }
                else if (varCount == "fewer")
                {
                    Assert.Single(after.Entries);
                    AssertPair(path, 16, 4.5f, rowId + " new");
                    // tail-hold: regs beyond the new set RETAIN old values byte-exact.
                    AssertPair(path, 8, 1.5f, rowId + " tail-hold");
                    AssertPair(path, 14, 15.79f, rowId + " tail-hold");
                }
                else // more
                {
                    Assert.Equal(4, after.Entries.Count);
                    Assert.Equal(0, after.BadType); // unknown name skipped at lookup, ¬ badtype
                    AssertPair(path, 48, 7.0f, rowId + " known-new");
                    AssertPair(path, 8, 2.5f, rowId);
                    // canary: an unmatched map slot never receives a write.
                    Assert.Equal(new byte[4], ReadPair(path, 20));
                }
                // writes flowed ⇒ com flag stays 1 (no staleness drop in-window).
                Assert.Equal(1, ReadFlag(path, BusLayout.ZoneComActiveFlagOffset));
            }
            else // stalled: empty snapshot ⇒ zero writes; ALL cells hold.
            {
                Assert.Empty(after.Entries);
                AssertPair(path, 8, 1.5f, rowId + " stall-hold");
                AssertPair(path, 14, 15.79f, rowId + " stall-hold");
                AssertPair(path, 16, -12.5f, rowId + " stall-hold");
                // in-window: flag still 1; beyond the idle timeout: flag-only drop.
                Assert.Equal(1, ReadFlag(path, BusLayout.ZoneComActiveFlagOffset));
                DateTime lastWrite = DateTime.UtcNow;
                Assert.False(ZoneStaleness.ShouldDrop(true, lastWrite, lastWrite.AddSeconds(4.9), Idle));
                Assert.True(ZoneStaleness.ShouldDrop(true, lastWrite, lastWrite.AddSeconds(6), Idle));
                w.SetZoneActive(ZoneKind.ComMain, false);
                Assert.Equal(0, ReadFlag(path, BusLayout.ZoneComActiveFlagOffset));
                AssertPair(path, 8, 1.5f, rowId + " flag-drop keeps cells");
                AssertPair(path, 14, 15.79f, rowId + " flag-drop keeps cells");
                AssertPair(path, 16, -12.5f, rowId + " flag-drop keeps cells");
            }

            // reg0 (D1 latch): template change is not a latch input.
            // sameDevice documents the Э2b identity axis (no-op by design) —
            // the latch state must be identical for both axis values.
            Assert.True(portBefore == latch.PortNumber,
                rowId + ": latch port drifted (sameDevice=" + sameDevice + ")");
            Assert.True(latched == latch.IsLatched,
                rowId + ": latch state drifted (sameDevice=" + sameDevice + ")");
            // heartbeat cell: poll writes never touch regs 0..3.
            Assert.True(ByteEq(heartbeatBefore, ReadPair(path, BusLayout.RegHeartbeat)),
                rowId + ": heartbeat cell mutated by poll writes");
        }
        finally
        {
            CloseTempBus(w, path);
        }
    }

    /// <summary>Row A25: the heartbeat cell survives any combination of zone
    /// writes and flag flips byte-exact (poll/write paths never touch regs 0..3).</summary>
    [Fact]
    public void DomainA_HeartbeatCell_SurvivesPollWrites()
    {
        string path;
        BusWriter w = OpenTempBus(out path);
        try
        {
            // Simulate the AddIn heartbeat: raw BE word at reg 3 (file offset 64+6).
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                fs.Position = BusLayout.BodyOffset + BusLayout.RegHeartbeat * 2L;
                fs.WriteByte(0x12);
                fs.WriteByte(0x34);
            }

            w.WriteFloatPair(ZoneKind.ComMain, 8, 1.5f);
            w.WriteFloatPair(ZoneKind.Lan, 200, 15.79f);
            w.WriteRawLine("1.5,2.5");
            w.SetZoneActive(ZoneKind.ComMain, false);
            w.SetZoneActive(ZoneKind.Lan, false);
            w.SetZoneActive(ZoneKind.RawReserve, false);
            w.WriteReg16(BusLayout.RegCaseState, 2); // header-state writes must skip reg3

            Assert.Equal(new byte[] { 0x12, 0x34 }, ReadBytes(path, BusLayout.BodyOffset + BusLayout.RegHeartbeat * 2L, 2));
        }
        finally
        {
            CloseTempBus(w, path);
        }
    }

    /// <summary>Row A26 (v5.1): raw_com_line keeps the last line's parsed field
    /// pairs across arbitrary poll activity; a shorter next line overwrites its
    /// leading fields and stale-holds the tail pairs (never zeroed).</summary>
    [Fact]
    public void DomainA_RawLine_PersistsUntilNextLine()
    {
        string path;
        BusWriter w = OpenTempBus(out path);
        try
        {
            w.WriteRawLine("1.5,2.5,3.5"); // 3 fields → regs 400..405
            byte[] persisted = ReadBytes(path, BusLayout.BodyOffset + BusLayout.RawComLineBaseReg * 2L, 12);

            // No line events; heavy poll activity elsewhere — pairs must persist.
            w.WriteFloatPair(ZoneKind.ComMain, 8, 1.5f);
            w.WriteFloatPair(ZoneKind.Lan, 200, 15.79f);
            w.SetZoneActive(ZoneKind.RawReserve, false);
            Assert.True(ByteEq(persisted, ReadBytes(path, BusLayout.BodyOffset + BusLayout.RawComLineBaseReg * 2L, 12)),
                "raw field pairs mutated without a new line event");

            // Shorter next line: field0 overwritten, tail pairs stale-held byte-exact.
            w.WriteRawLine("9");
            AssertPair(path, 400, 9.0f, "A26 shorter-line head");
            AssertPair(path, 402, 2.5f, "A26 stale tail field1");
            AssertPair(path, 404, 3.5f, "A26 stale tail field2");
        }
        finally
        {
            CloseTempBus(w, path);
        }
    }

    /// <summary>Row A27: the D1 latch has no template/poll input — its state is
    /// invariant across any candidate-set change (incl. the empty set); release
    /// happens only via Release() (UpdateLatchLossPolicy in the module).</summary>
    [Fact]
    public void DomainA_Latch_HasNoTemplateInput()
    {
        var latch = new ComFeedLatch();
        Assert.True(latch.OnLine("COM1", 1));
        DateTime evidence = latch.LastEvidenceUtc;

        var sets = new List<List<KeyValuePair<string, object>>>
        {
            T1,
            new List<KeyValuePair<string, object>> { Cand("Pump Pressure", 4.5f) },      // fewer
            new List<KeyValuePair<string, object>> { Cand("Brand New Signal", 9.9f) },   // unknown-only
            new List<KeyValuePair<string, object>>(),                                    // vanished template
        };
        foreach (var set in sets)
        {
            PollResolution.Resolve(set); // poll churn — must not couple into the latch
            Assert.True(latch.IsLatched);
            Assert.Equal(1, latch.PortNumber);
            Assert.Equal(evidence, latch.LastEvidenceUtc);
        }

        latch.Release(); // the ONLY release path (module: stream loss policy)
        Assert.False(latch.IsLatched);
        Assert.Equal(0, latch.PortNumber);
    }

    // ═══ DOMAIN B — LAN transitional processes (Э2b rows B01..B22) ═══
    // Seed = 2 lan pairs via ResolveLan (Recirc Density→200, Recirc Flow
    // Rate→202) — pins addr=200+idx*2 on every (re)write.

    private static readonly List<KeyValuePair<string, object>> LanSeed = new List<KeyValuePair<string, object>>
    {
        Cand("Recirc Density", 15.79f),
        Cand("Recirc Flow Rate", -12.5f),
    };

    private static readonly List<KeyValuePair<string, object>> LanV2 = new List<KeyValuePair<string, object>>
    {
        Cand("Recirc Density", 1.0f),
        Cand("Recirc Flow Rate", 8192f),
    };

    private static readonly List<KeyValuePair<string, object>> LanV3 = new List<KeyValuePair<string, object>>
    {
        Cand("Recirc Density", 2.0f),
        Cand("Recirc Flow Rate", 4096f),
    };

    public static IEnumerable<object[]> DomainBRows()
    {
        // rowId, linkEvent, dataPresent, flagBefore — exact Э2b enumeration
        // (unreachable = ¬dataPresent ∧ flagBefore: the module's ONLY lan-flag
        // raiser is a pair write ⇒ flag=1 implies values were received).
        yield return new object[] { "B01", "down", true, true };
        yield return new object[] { "B02", "down", true, false };
        yield return new object[] { "B03", "down", false, false };
        yield return new object[] { "B04", "down", false, true };   // unreachable
        yield return new object[] { "B05", "up", true, true };
        yield return new object[] { "B06", "up", true, false };
        yield return new object[] { "B07", "up", false, false };
        yield return new object[] { "B08", "up", false, true };     // unreachable
        yield return new object[] { "B09", "flap", true, true };
        yield return new object[] { "B10", "flap", true, false };
        yield return new object[] { "B11", "flap", false, false };
        yield return new object[] { "B12", "flap", false, true };   // unreachable
        yield return new object[] { "B13", "longloss", true, true };
        yield return new object[] { "B14", "longloss", true, false };
        yield return new object[] { "B15", "longloss", false, false };
        yield return new object[] { "B16", "longloss", false, true }; // unreachable
    }

    private static void WriteLan(BusWriter w, List<KeyValuePair<string, object>> set, string rowId)
    {
        PollResolution.Outcome o = PollResolution.ResolveLan(set);
        foreach (PollResolution.Entry e in o.Entries)
        {
            // addr law: addr=200+idx*2 inside lan_stream_crio — no shift on (re)connect.
            Assert.InRange(e.Address, BusLayout.LanStreamCrioBaseReg, 398);
            Assert.Equal(0, (e.Address - BusLayout.LanStreamCrioBaseReg) % 2);
            w.WritePreparedPair(PollPairPreparation.PrepareLan(e));
        }
        Assert.Equal(2, o.Entries.Count);
    }

    [Theory]
    [MemberData(nameof(DomainBRows))]
    public void DomainB_LanTransitional_TruthRow(string rowId, string linkEvent, bool dataPresent, bool flagBefore)
    {
        string path;
        BusWriter w = OpenTempBus(out path);
        try
        {
            if (!dataPresent && flagBefore)
            {
                // UNREACHABLE (Э2b): on a fresh bus the lan flag is 0 and the
                // counter is 0 — flag=1 exists only after a pair write, which
                // contradicts "never received".
                Assert.Equal(0, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset));
                Assert.Equal(0u, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset));
                return;
            }

            bool active = false; // module-side _lanActiveFlag model
            DateTime lastWrite = DateTime.UtcNow;
            if (dataPresent)
            {
                WriteLan(w, LanSeed, rowId); // counter=2, flag=1, tick>0
                active = true;
                lastWrite = DateTime.UtcNow;
                if (!flagBefore)
                {
                    w.SetZoneActive(ZoneKind.Lan, false); // already-idle entry state
                    active = false;
                }
            }

            switch (linkEvent)
            {
                case "down":
                {
                    long tickBefore = ReadTick(path, BusLayout.ZoneLanLastWriteTickOffset);
                    uint countBefore = ReadCounter(path, BusLayout.ProducerLanWriteCountOffset);
                    if (ZoneStaleness.ShouldDrop(active, lastWrite, lastWrite.AddSeconds(6), Idle))
                    {
                        active = false;
                        w.SetZoneActive(ZoneKind.Lan, false); // FLAG ONLY
                    }
                    Assert.Equal(0, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset));
                    Assert.Equal(tickBefore, ReadTick(path, BusLayout.ZoneLanLastWriteTickOffset)); // frozen
                    Assert.Equal(countBefore, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset));
                    if (dataPresent)
                    {
                        AssertPair(path, 200, 15.79f, rowId + " loss-hold");
                        AssertPair(path, 202, -12.5f, rowId + " loss-hold");
                    }
                    else
                    {
                        Assert.Equal(new byte[4], ReadPair(path, 200)); // initial state, ¬ zeroing
                    }
                    break;
                }
                case "up":
                {
                    uint countBefore = ReadCounter(path, BusLayout.ProducerLanWriteCountOffset);
                    long tickBefore = ReadTick(path, BusLayout.ZoneLanLastWriteTickOffset);
                    WriteLan(w, LanV2, rowId); // reconnect: SAME addrs
                    AssertPair(path, 200, 1.0f, rowId + " reconnect");
                    AssertPair(path, 202, 8192f, rowId + " reconnect");
                    Assert.Equal(1, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset)); // write re-raises
                    // counter CONTINUES from the header-persisted value (monotonic).
                    Assert.Equal(countBefore + 2u, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset));
                    Assert.True(ReadTick(path, BusLayout.ZoneLanLastWriteTickOffset) >= tickBefore);
                    break;
                }
                case "flap":
                {
                    if (!dataPresent)
                    {
                        // dry-link flap = total no-op on the bus
                        Assert.Equal(0, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset));
                        Assert.Equal(0u, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset));
                        Assert.Equal(new byte[4], ReadPair(path, 200));
                        break;
                    }
                    // down-up-down within seconds: every silence < 5 s ⇒ ShouldDrop
                    // false ⇒ the module posts NOTHING; flag must never bounce.
                    Assert.False(ZoneStaleness.ShouldDrop(active, lastWrite, lastWrite.AddSeconds(2), Idle), rowId);
                    WriteLan(w, LanV2, rowId);   // up #1
                    Assert.Equal(1, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset));
                    Assert.False(ZoneStaleness.ShouldDrop(true, DateTime.UtcNow, DateTime.UtcNow.AddSeconds(1), Idle), rowId);
                    WriteLan(w, LanV3, rowId);   // up #2
                    AssertPair(path, 200, 2.0f, rowId + " flap final");
                    AssertPair(path, 202, 4096f, rowId + " flap final");
                    Assert.Equal(1, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset)); // no bounce
                    // strictly monotonic counter across bursts (2 seed + 2 + 2).
                    Assert.Equal(6u, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset));
                    break;
                }
                case "longloss":
                {
                    if (!dataPresent)
                    {
                        Assert.Equal(0, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset));
                        Assert.Equal(0u, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset));
                        break;
                    }
                    uint drops = 0;
                    for (int tick = 1; tick <= 12; tick++) // 12 s of silence at 1 Hz
                    {
                        if (ZoneStaleness.ShouldDrop(active, lastWrite, lastWrite.AddSeconds(tick), Idle))
                        {
                            active = false;
                            drops++;
                            w.SetZoneActive(ZoneKind.Lan, false);
                        }
                    }
                    Assert.True(drops <= 1, rowId + ": flag dropped " + drops + " times — bounce artifact");
                    Assert.Equal(flagBefore ? 1u : 0u, drops); // exactly one drop iff it was active
                    Assert.Equal(0, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset));
                    // cells hold indefinitely
                    AssertPair(path, 200, 15.79f, rowId + " long-loss hold");
                    AssertPair(path, 202, -12.5f, rowId + " long-loss hold");
                    Assert.Equal(2u, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset));
                    // reconnect after the long loss: same addrs, counter continues.
                    WriteLan(w, LanV2, rowId);
                    AssertPair(path, 200, 1.0f, rowId + " post-loss reconnect");
                    Assert.Equal(4u, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset));
                    Assert.Equal(1, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset));
                    break;
                }
            }
        }
        finally
        {
            CloseTempBus(w, path);
        }
    }

    public static IEnumerable<object[]> IdleThresholdRows()
    {
        // rowId, zoneActive, silenceSeconds, expectedDrop (Э2b B17..B20)
        yield return new object[] { "B17", true, 4.999, false };  // flap silence ⇒ no bounce
        yield return new object[] { "B18", true, 5.0, false };    // boundary is NOT beyond
        yield return new object[] { "B19", true, 5.001, true };   // beyond ⇒ drop (flag only)
        yield return new object[] { "B20", false, 3600.0, false }; // ¬active ⇒ no repeated posts
    }

    [Theory]
    [MemberData(nameof(IdleThresholdRows))]
    public void DomainB_IdleThreshold_TruthRow(string rowId, bool zoneActive, double silenceSeconds, bool expectedDrop)
    {
        DateTime lastWrite = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        DateTime now = lastWrite.AddSeconds(silenceSeconds);
        Assert.True(expectedDrop == ZoneStaleness.ShouldDrop(zoneActive, lastWrite, now, Idle),
            rowId + ": drop=" + ZoneStaleness.ShouldDrop(zoneActive, lastWrite, now, Idle)
            + " != expected " + expectedDrop + " (silence=" + silenceSeconds + "s)");
    }

    /// <summary>Row B21: caseLanState (reg2 passthrough) never gates the writer —
    /// lan pair writes land byte-exact regardless of the lan flag/state, and the
    /// write itself re-raises the flag (the module never posts SetZoneActive(true)).</summary>
    [Fact]
    public void DomainB_CaseLanState_NeverGatesWriter()
    {
        string path;
        BusWriter w = OpenTempBus(out path);
        try
        {
            // Simulated LostConnection period (reg2 would read 8): flag 0.
            w.SetZoneActive(ZoneKind.Lan, false);
            Assert.Equal(0, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset));
            // The writer has no state gate: the pair lands byte-exact anyway.
            w.WriteFloatPair(ZoneKind.Lan, 200, 15.79f);
            AssertPair(path, 200, 15.79f, "write under flag=0");
            Assert.Equal(1, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset)); // write re-raised
            // And under flag=1 the next write lands the same way (state-free path).
            w.WriteFloatPair(ZoneKind.Lan, 200, -12.5f);
            AssertPair(path, 200, -12.5f, "write under flag=1");
        }
        finally
        {
            CloseTempBus(w, path);
        }
    }

    /// <summary>Row B22: writer restart mid-loss (module retry loop re-open):
    /// a layoutVer-5 file opens as-is — lan cells survive the reopen byte-exact,
    /// the counter continues from the header-persisted value, addrs are stable,
    /// and the file length is unchanged (no reshape).</summary>
    [Fact]
    public void DomainB_Reconnect_CounterMonotonic_AddrStable()
    {
        string path;
        BusWriter w1 = OpenTempBus(out path);
        WriteLan(w1, LanSeed, "B22 seed");
        w1.Dispose(); // producer crash/restart sim — channel lost
        Assert.Equal(20066L, new FileInfo(path).Length);
        AssertPair(path, 200, 15.79f, "B22 stale-hold across restart");
        AssertPair(path, 202, -12.5f, "B22 stale-hold across restart");

        BusWriter w2 = BusWriter.Open(); // same overrides — v5 file opens as-is
        try
        {
            Assert.True(w2.IsOk);
            Assert.Equal(20066L, new FileInfo(path).Length); // no reshape on reopen
            Assert.Equal(2u, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset)); // persisted
            WriteLan(w2, LanV2, "B22 reconnect");
            AssertPair(path, 200, 1.0f, "B22 reconnect addr-stable");
            AssertPair(path, 202, 8192f, "B22 reconnect addr-stable");
            Assert.Equal(4u, ReadCounter(path, BusLayout.ProducerLanWriteCountOffset)); // continued
            Assert.Equal(1, ReadFlag(path, BusLayout.ZoneLanActiveFlagOffset));
        }
        finally
        {
            CloseTempBus(w2, path);
        }
    }

    // ═══ DOMAIN C — raw_com_line CSV positional law (Э2b rows C01..C08,
    //     contract v5.1 CHANGE 2, user decree 2026-08-18) ═══
    // The line is split POSITIONAL over the raw line AS-IS (excludeFromTelemetry
    // fields INCLUDED — DA decision: the zone mirrors "what lands in the string",
    // template-agnostic); field i → float32 CDAB pair at 400+i*2, ≤100 fields;
    // unparseable ⇒ pair not written (stale-hold); empty line ⇒ total no-op;
    // liveness = line-ARRIVAL evidence (fires even when zero fields parse).

    public static IEnumerable<object[]> DomainCRows()
    {
        // rowId, firstLine, secondLine (null = none), expectedPairs "reg:float;…",
        // zeroRegs "reg;…" (4-byte zero check), expectedRawCount
        yield return new object[] { "C01", "1.5,2.5,-3.25", null, "400:1.5;402:2.5;404:-3.25", "", 1u };
        yield return new object[] { "C02", "abc,2.5", null, "402:2.5", "400", 1u };
        yield return new object[] { "C03", "1.5,2.5,3.5", "9", "400:9;402:2.5;404:3.5", "", 2u };
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 150; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append("1.5");
        }
        yield return new object[] { "C04", sb.ToString(), null, "400:1.5;500:1.5;598:1.5", "600", 1u };
        yield return new object[] { "C05", "", null, "", "400", 0u };
        yield return new object[] { "C06", "abc,def", null, "", "400;402", 1u };
        yield return new object[] { "C07", " 1.5e1 ,+2.5", null, "400:15;402:2.5", "", 1u };
        yield return new object[] { "C08", "1.5", "abc", "400:1.5", "", 2u };
    }

    [Theory]
    [MemberData(nameof(DomainCRows))]
    public void DomainC_RawCsv_TruthRow(string rowId, string firstLine, string secondLine,
        string expectedPairs, string zeroRegs, uint expectedRawCount)
    {
        string path;
        BusWriter w = OpenTempBus(out path);
        try
        {
            w.WriteRawLine(firstLine);
            if (secondLine != null)
            {
                w.WriteRawLine(secondLine);
            }

            char[] semi = { ';' };
            foreach (string spec in expectedPairs.Split(semi, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = spec.Split(':');
                AssertPair(path, int.Parse(kv[0], CultureInfo.InvariantCulture),
                    float.Parse(kv[1], CultureInfo.InvariantCulture), rowId);
            }
            foreach (string regS in zeroRegs.Split(semi, StringSplitOptions.RemoveEmptyEntries))
            {
                int reg = int.Parse(regS, CultureInfo.InvariantCulture);
                Assert.Equal(new byte[4], ReadPair(path, reg)); // never written / canary — ¬ a zeroing event
            }
            Assert.Equal(expectedRawCount, ReadCounter(path, BusLayout.ProducerRawWriteCountOffset));
            Assert.Equal(expectedRawCount > 0 ? (byte)1 : (byte)0, ReadFlag(path, BusLayout.ZoneRawActiveFlagOffset));
        }
        finally
        {
            CloseTempBus(w, path);
        }
    }

    // ═══ DOMAIN T — com idx0 timestamp law (Э2b rows T01/T02, contract v5.1
    //     CHANGE 1, user decree 2026-08-18) ═══

    /// <summary>T01/T02: idx0 ticks → Unix seconds → int32 RAW BITS in the CDAB
    /// word order (consumer reads regs 4..5 as int32 ⇒ unix time; float read =
    /// garbage BY DECREE). Byte pin: 2026-08-18T12:00:00Z = ticks
    /// 639226512000000000 → unix 1787054400 = 0x6A844940 → 49 40 6A 84.</summary>
    [Fact]
    public void DomainT_Timestamp_TicksToUnixSeconds_BitsPinned()
    {
        // T02: the law itself (total function — epoch boundary + no-throw).
        Assert.Equal(1787054400, UnixTimestamp.TicksToUnixSeconds(639226512000000000.0));
        Assert.Equal(0, UnixTimestamp.TicksToUnixSeconds((double)UnixTimestamp.EpochTicks));
        UnixTimestamp.TicksToUnixSeconds(0.0); // pre-epoch: total function, no throw

        // T01: the bus wire image (byte-level pin through the real writer).
        string path;
        BusWriter w = OpenTempBus(out path);
        try
        {
            int sec = UnixTimestamp.TicksToUnixSeconds(639226512000000000.0);
            w.WriteInt32Pair(ZoneKind.ComMain, BusLayout.ComTimestampReg, sec);
            Assert.Equal(new byte[] { 0x49, 0x40, 0x6A, 0x84 }, ReadPair(path, BusLayout.ComTimestampReg));
            Assert.Equal(1, ReadFlag(path, BusLayout.ZoneComActiveFlagOffset)); // one liveness mark
            Assert.Equal(1u, ReadCounter(path, BusLayout.ProducerComWriteCountOffset));
        }
        finally
        {
            CloseTempBus(w, path);
        }
    }
}
