// BusSelfTest.exe — W2 self-test harness (net48 console, compiled against the
// AddIn DLL). Exercises BusWriter + BusWriteQueue + CdabCodec + ComFeedLatch
// with REAL C# delegates (no PowerShell scriptblock-on-foreign-thread semantics).
// ISOLATION: runs against a TEMP backing file + unique section/mutex names
// (production file C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat is NEVER touched —
// it may be mapped by the live iCem/iCemDataMonitor.Tray processes).
// LAYOUT v5 (bus register map v5, user decree 2026-08-17):
//   state block = regs 0..3 {comPortActive, caseState, caseLanState, heartbeat
//   (wrap 32000)}; com_stream 4..199 (addr=4+idx*2; v5.1 CHANGE 1: idx0 regs
//   4..5 = UnixTimestampSeconds int32 RAW BITS in the CDAB word order —
//   consumer owns read formatting); lan_stream_crio 200..399
//   (addr=200+idx*2, full 4-byte CDAB); raw_com_line 400..599 (v5.1 CHANGE 2:
//   float32 CDAB positional CSV fields, field i @ 400+i*2, ≤100 fields — the
//   u16-per-char path is RETIRED);
//   ace_full RESERVE 600..10000 = DEFERRED — AddIn NEVER writes 600+.
//   D3 dictionary: fresh create = 10001 regs / 20066 B.
//   D4 liveness: com ∧ lan ∧ raw header blocks = ALL AddIn-owned (tick/flag/
//   counter marked per write; flag 0 on hold, data never zeroed).
//   SHAPE LAW v5 (FULL MIGRATION): FINAL shapes = layoutVer==5 ONLY with any
//   regCount ∈ [650..65535] (len==64+2N, IsValidShape). UPGRADE SOURCES
//   (IsUpgradeable, expand ONCE to 10001/20066 under the mutex): v2 650/1364,
//   v3-small 1650/3364, v3-full ∧ v4-full 8404/16872 (body preserved byte-exact,
//   tail zeroed; 49..63 NOT re-zeroed except v2). Any other legacy shape ⇒
//   fail-safe; v1 (600/1264) is DEAD.
//   INC-03 ordered-restart: a live smaller-capacity section blocks the sized
//   view ⇒ fail fast with BusAccessDeniedException.ForSectionCapacity
//   (runbook: stop consumers; producer iCem.exe FIRST, POC after).
// Usage: compile with csc /out:BusSelfTest.exe /reference:Burservis.iCem.DataMonitor.dll,System.Core.dll
// Exit code 0 = all assertions passed.

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Burservis.iCem.DataMonitor.Bus;

internal static class Program
{
    private static int _failures;
    private static string _path;

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + " " + detail);
        if (!ok)
        {
            _failures++;
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

    private static bool AllZero(byte[] b, int off, int len)
    {
        for (int i = 0; i < len; i++)
        {
            if (b[off + i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Craft a length-derived bus file: 'STNB', version 1, given layoutVer +
    /// regCount, fileLength == 64 + regCount×2, sentinel body fill + tail
    /// sentinel at the last 2 bytes.
    /// </summary>
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
        f[len - 2] = 0xEE;                                                  // tail sentinel
        f[len - 1] = 0xFF;
        return f;
    }

    private static int Main()
    {
        try
        {
            // ── isolation: temp file + unique section/mutex names ────────────
            string id = Guid.NewGuid().ToString("N");
            _path = Path.Combine(Path.GetTempPath(), "busself_" + id + ".dat");
            BusWriter.BackingPathOverride = _path;
            BusWriter.BusNameOverride = @"Global\iCemDataMonitorBus_selftest_" + id;
            BusWriter.MutexNameOverride = @"Global\iCemDataMonitorBusMutex_selftest_" + id;
            TapLog.PathOverride = Path.Combine(Path.GetTempPath(), "busself_" + id + ".log");

            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            // ── 1. fresh open + init header (layout v5) ─────────────────────
            BusWriter w = BusWriter.Open();
            Check("open.fresh", w.IsOk, "");

            // ── 2. CDAB pins ────────────────────────────────────────────────
            ushort lo, hi;
            CdabCodec.Split(1.0f, out lo, out hi);
            Check("cdab.1.0f", lo == 0x0000 && hi == 0x3F80, "low=" + lo.ToString("X4") + " high=" + hi.ToString("X4"));
            CdabCodec.Split(15.79f, out lo, out hi);
            Check("cdab.15.79f", lo == 0xA3D7 && hi == 0x417C, "low=" + lo.ToString("X4") + " high=" + hi.ToString("X4"));
            Check("cdab.roundtrip", Math.Abs(CdabCodec.Combine(0xA3D7, 0x417C) - 15.79f) < 0.001f, "");

            // ── 3. v5/v5.1 zone pins: com_stream@4, lan_stream_crio@200 (DATA-ONLY),
            //        raw_com_line CSV fields @400, header state regs 0..3 ──────
            w.WriteInt32Pair(ZoneKind.ComMain, 4, 1787037218); // idx0 slot (Time & Date) — v5.1: unix seconds raw bits
            w.WriteFloatPair(ZoneKind.ComMain, 6, 15.79f);    // idx1 slot (Unit Num) — stays float32 (documented asymmetry)
            w.WriteFloatPair(ZoneKind.Lan, 200, -12.5f);      // lan idx0 slot (Recirc Density) — DATA-ONLY
            w.WriteRawLine("1.5,2.5");
            w.WriteReg16(BusLayout.RegComPort, 7);            // LIVE: COM7 latch
            w.WriteReg16(BusLayout.RegCaseState, BusLayout.CaseStateInProgress);    // LIVE: 2
            w.WriteReg16(BusLayout.RegCaseLanState, BusLayout.CaseLanStateRecording); // LIVE: 4
            w.WriteReg16(BusLayout.RegHeartbeat, 29);         // LIVE: 29
            // v5 guards: out-of-zone writes must be silent no-ops
            w.WriteFloatPair(ZoneKind.ComMain, 3, 9.0f);      // below com floor
            w.WriteFloatPair(ZoneKind.ComMain, 199, 9.0f);    // beyond com top
            w.WriteFloatPair(ZoneKind.Lan, 399, 9.0f);        // beyond lan top
            w.WriteFloatPair(ZoneKind.RawReserve, 400, 9.0f); // raw zone pairs = WriteRawLine batch only
            w.WriteFloatPair(ZoneKind.Wits, 600, 9.0f);       // dead
            w.WriteReg16(600, 7);                             // ace_full reserve floor — never written
            w.SetZoneActive(ZoneKind.Wits, true);             // dead — no-op
            w.Dispose();

            byte[] b = File.ReadAllBytes(_path);
            Check("file.len", b.Length == BusLayout.TotalFileBytes, "len=" + b.Length);
            Check("hdr.magic", Hex(b, 0, 4) == "53 54 4E 42", Hex(b, 0, 4));
            Check("hdr.version", Hex(b, 4, 2) == "01 00", Hex(b, 4, 2));
            Check("hdr.layout", Hex(b, 6, 2) == "05 00", Hex(b, 6, 2) + " (fresh create = layoutVer 5)");
            Check("hdr.regcount", Hex(b, 8, 2) == "11 27", Hex(b, 8, 2) + " (10001 LE — D3 dictionary)");
            // ── v5 header state regs 0..3 ──
            Check("reg0.comPortActive", Hex(b, 64, 2) == "00 07", Hex(b, 64, 2));
            Check("reg1.caseState", Hex(b, 66, 2) == "00 02", Hex(b, 66, 2) + " (InProgress = 2)");
            Check("reg2.caseLanState", Hex(b, 68, 2) == "00 04", Hex(b, 68, 2) + " (Recording = 4)");
            Check("reg3.heartbeat", Hex(b, 70, 2) == "00 1D", Hex(b, 70, 2) + " (29)");
            // ── zone data ──
            Check("pair.com.unixBits", Hex(b, 72, 4) == "06 22 6A 84", Hex(b, 72, 4) + " (reg 4..5 = int32 bits of unix 1787037218 = 0x6A840622, CDAB word order — v5.1)");
            Check("pair.com.15.79f", Hex(b, 76, 4) == "A3 D7 41 7C", Hex(b, 76, 4));
            Check("pair.lan.-12.5f", Hex(b, 464, 4) == "00 00 C1 48", Hex(b, 464, 4) + " (reg 200 — FULL CDAB, INC-06 fix)");
            Check("raw.fields", Hex(b, 864, 8) == "00 00 3F C0 00 00 40 20", Hex(b, 864, 8) + " (v5.1 CSV positional: field0 1.5f @400, field1 2.5f @402)");
            // guards: state block regs 0..3 untouched by pair writes
            Check("guard.reg0Intact", Hex(b, 64, 2) == "00 07", Hex(b, 64, 2));
            Check("guard.reg3Intact", Hex(b, 70, 2) == "00 1D", Hex(b, 70, 2));
            // D4: lan liveness block IS AddIn-owned — marked by the lan pair write
            Check("flag.com", b[18] == 1, "val=" + b[18]);
            Check("flag.raw", b[36] == 1, "val=" + b[36]);
            Check("flag.lanSet", b[27] == 1, "lan activeFlag SET by AddIn (D4; val=" + b[27] + ")");
            Check("count.com", Hex(b, 37, 4) == "02 00 00 00", Hex(b, 37, 4) + " (guarded writes did NOT count)");
            Check("count.raw", Hex(b, 45, 4) == "01 00 00 00", Hex(b, 45, 4));
            Check("count.lanOne", Hex(b, 41, 4) == "01 00 00 00", Hex(b, 41, 4) + " (lan counter +1 per pair write, D4)");
            bool lanTickSet = false;
            for (int i = 0; i < 8; i++)
            {
                lanTickSet |= b[19 + i] != 0;
            }
            Check("lan.tickSet", lanTickSet, "lan lastWriteTick SET by AddIn (D4)");
            // reserved-zero block 49..63 (former wits — the future ace_full claim is a layoutVer bump)
            Check("reservedZero.49..63", AllZero(b, 49, 15), "bytes 49..63 zero");
            // ace_full reserve 600..10000 never written
            Check("aceFull.600", AllZero(b, 1264, 4), "reg 600 never written");
            Check("aceFull.10000", AllZero(b, 20064, 2), "reg 10000 never written (reserve end)");

            // ── 3b. zone geometry v5 ───────────────────────────────────────────
            bool zonesOk =
                BusLayout.ComStreamBaseReg == 4 && BusLayout.ComStreamBaseReg + BusLayout.ComStreamRegCount - 1 == 199 &&
                BusLayout.LanStreamCrioBaseReg == 200 && BusLayout.LanStreamCrioBaseReg + BusLayout.LanStreamCrioRegCount - 1 == 399 &&
                BusLayout.RawComLineBaseReg == 400 && BusLayout.RawComLineBaseReg + BusLayout.RawComLineRegCount - 1 == 599 &&
                BusLayout.AceFullBaseReg == 600 && BusLayout.AceFullEndReg == 10000 &&
                BusLayout.AceFullRegCount == 9401 &&
                BusLayout.AddInMaxReg == 599 &&
                BusLayout.RegCount == 10001 &&
                BusLayout.TotalFileBytes == 20066 &&
                BusLayout.ComStreamBaseReg + BusLayout.ComStreamRegCount - 1 < BusLayout.LanStreamCrioBaseReg &&
                BusLayout.LanStreamCrioBaseReg + BusLayout.LanStreamCrioRegCount - 1 < BusLayout.RawComLineBaseReg &&
                BusLayout.RawComLineBaseReg + BusLayout.RawComLineRegCount - 1 < BusLayout.AceFullBaseReg &&
                BusLayout.AceFullEndReg == BusLayout.RegCount - 1;
            Check("zones.nonOverlap", zonesOk, "4..199 / 200..399 / 400..599 / 600..10000 (ace_full reserve, DEFERRED)");
            Check("dictionary.d3", BusLayout.BodyBytes == 20002 && BusLayout.TotalFileBytes == 64 + BusLayout.RegCount * 2,
                "D3 dictionary: 10001 regs / 20066 B");
            Check("zonekind.posBinding", (byte)ZoneKind.ComMain == 0 && (byte)ZoneKind.Lan == 1 && (byte)ZoneKind.RawReserve == 2, "positional binding");
            Check("heartbeat.wrap", BusLayout.HeartbeatWrap == 32000, "D2 wrap = " + BusLayout.HeartbeatWrap);
            Check("header.regcount4", BusLayout.HeaderRegCount == 4, "state block = regs 0..3");

            // ── 3c. DEFECT-1 normalization + v5 address law (both maps) ───────
            int addr = 0;
            Check("map.com.pump", DataMonitorAddressMap.TryGetComAddress("Pump Pressure", out addr) && addr == 16, "idx6 → 4+12 = 16 (addr=" + addr + ")");
            Check("map.com.canonical", DataMonitorAddressMap.TryGetComAddress("PumpPressure", out addr) && addr == 16, "addr=" + addr);
            Check("map.com.spaceCaseSymbol", DataMonitorAddressMap.TryGetComAddress("  pUmP   PrEsSuRe ", out addr) && addr == 16, "addr=" + addr);
            Check("map.com.aliasDS", DataMonitorAddressMap.TryGetComAddress("DS Pump Press", out addr) && addr == 18, "addr=" + addr);
            Check("map.com.wtrSlash", DataMonitorAddressMap.TryGetComAddress("Wtr/slry Ratio", out addr) && addr == 62, "idx29 → 4+58 = 62 (addr=" + addr + ")");
            Check("map.com.underscore", DataMonitorAddressMap.TryGetComAddress("rho12_hat", out addr) && addr == 42 &&
                DataMonitorAddressMap.TryGetComAddress("rho12hat", out addr) && addr == 42, "idx19 → 42 (addr=" + addr + ")");
            Check("map.com.idx0", DataMonitorAddressMap.TryGetComAddress("Time & Date", out addr) && addr == 4,
                "idx0 → reg 4 (addr=" + addr + ")");
            Check("map.com.idx33", DataMonitorAddressMap.TryGetComAddress("VolumetricMeter", out addr) && addr == 70,
                "idx33 → reg 70 (addr=" + addr + ")");
            Check("map.lan.idx0", DataMonitorAddressMap.TryGetLanAddress("Recirc Density", out addr) && addr == 200,
                "lan idx0 → reg 200 (addr=" + addr + ")");
            Check("map.lan.pumpA", DataMonitorAddressMap.TryGetLanAddress("Pump A Pressure", out addr) && addr == 266,
                "lan idx33 → 200+66 = 266 (addr=" + addr + ")");
            Check("map.lan.opsTrack", DataMonitorAddressMap.TryGetLanAddress("Cmnt Ops Track IDNum", out addr) && addr == 388,
                "lan idx94 → 388 (addr=" + addr + ")");
            // vendor-alias spot pins (additive-only dispatch 2026-08-18; the full
            // 73-row table lives in DataMonitorAddressMapTests.VendorAliasRows)
            Check("map.lan.alias.rcrc", DataMonitorAddressMap.TryGetLanAddress("Rcrc Density", out addr) && addr == 200,
                "vendor 'Rcrc Density' → idx0 reg 200 (addr=" + addr + ")");
            Check("map.lan.alias.mixwtr", DataMonitorAddressMap.TryGetLanAddress("Mix Water Pres", out addr) && addr == 218,
                "vendor 'Mix Water Pres' → idx9 reg 218 (addr=" + addr + ")");
            Check("map.lan.alias.analog", DataMonitorAddressMap.TryGetLanAddress("Analog In 5", out addr) && addr == 324,
                "vendor 'Analog In 5' → idx62 reg 324 (addr=" + addr + ")");
            Check("map.lan.alias.optrack", DataMonitorAddressMap.TryGetLanAddress("Op Track IDNum", out addr) && addr == 388,
                "vendor 'Op Track IDNum' → idx94 reg 388 (addr=" + addr + ")");
            Check("map.alias.zonesIsolated", DataMonitorAddressMap.TryGetComAddress("Unit Num", out addr) && addr == 6 &&
                DataMonitorAddressMap.TryGetLanAddress("Unit Num", out addr) && addr == 254,
                "'Unit Num' in BOTH maps: com=6, lan=254 — per-map lookup, no cross-zone leak");
            Check("map.lan.outOfView", !DataMonitorAddressMap.TryGetLanAddress("Truck Number", out addr),
                "idx>=96 out of curated view (addr=" + addr + ")");
            Check("map.unknownStaysUnknown", !DataMonitorAddressMap.TryGetComAddress("Measured Depth", out addr) &&
                !DataMonitorAddressMap.TryGetComAddress("Pumping Pressure", out addr), "not-in-registry names must NOT be invented");
            Check("map.nullNoThrow", !DataMonitorAddressMap.TryGetComAddress(null, out addr), "null → false, no throw");

            int minAddr = int.MaxValue;
            int maxAddr = int.MinValue;
            int lanDistinct = 0;
            var lanSeen = new System.Collections.Generic.HashSet<int>();
            foreach (System.Collections.Generic.KeyValuePair<string, int> e in DataMonitorAddressMap.DebugComEntries())
            {
                minAddr = Math.Min(minAddr, e.Value);
                maxAddr = Math.Max(maxAddr, e.Value);
            }
            foreach (System.Collections.Generic.KeyValuePair<string, int> e in DataMonitorAddressMap.DebugLanEntries())
            {
                if (lanSeen.Add(e.Value))
                {
                    lanDistinct++;
                }
            }
            Check("map.com.floor", minAddr == BusLayout.ComStreamBaseReg,
                "smallest com address = " + minAddr + " (no write below 4)");
            Check("map.com.ceiling", maxAddr == 70 && maxAddr + 1 < BusLayout.LanStreamCrioBaseReg,
                "largest com address = " + maxAddr + " (inside com_stream)");
            Check("map.lan.coverage96", lanDistinct == 96, "96 cRIO signals (distinct=" + lanDistinct + ")");
            Check("map.lan.distinct", lanDistinct == 96, "lan distinct addresses = " + lanDistinct);
            string[] amb = DataMonitorAddressMap.DebugComAmbiguous();
            string[] ambLan = DataMonitorAddressMap.DebugLanAmbiguous();
            Check("map.ambiguousNone", amb.Length == 0 && ambLan.Length == 0, "ambiguous norm keys: com=" + amb.Length + " lan=" + ambLan.Length);

            // ── 3d. com + lan resolution law ──────────────────────────────────
            var comCands = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, object>>();
            comCands.Add(new System.Collections.Generic.KeyValuePair<string, object>("Pump Pressure", DateTime.Now)); // badtype sentinel
            comCands.Add(new System.Collections.Generic.KeyValuePair<string, object>("Pump Pressure", 15.5f));         // good later
            PollResolution.Outcome comRes = PollResolution.Resolve(comCands);
            Check("pollres.convertibleWins",
                comRes.Entries.Count == 1 && comRes.Entries[0].Address == 16 &&
                Math.Abs(comRes.Entries[0].Value - 15.5) < 0.001 &&
                comRes.BadType == 1 && comRes.Dup == 0,
                "entries=" + comRes.Entries.Count + " badtype=" + comRes.BadType);

            comCands.Clear();
            comCands.Add(new System.Collections.Generic.KeyValuePair<string, object>("Pump Pressure", 1.0f));
            comCands.Add(new System.Collections.Generic.KeyValuePair<string, object>("PS Pump Press", 2.0f));
            comRes = PollResolution.Resolve(comCands);
            Check("pollres.dedupFirstWins",
                comRes.Entries.Count == 1 && comRes.Entries[0].Address == 16 &&
                Math.Abs(comRes.Entries[0].Value - 1.0) < 0.001 && comRes.Dup == 1,
                "entries=" + comRes.Entries.Count + " dup=" + comRes.Dup);

            var lanCands = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, object>>();
            lanCands.Add(new System.Collections.Generic.KeyValuePair<string, object>("Pump A Pressure", 125.93f));
            lanCands.Add(new System.Collections.Generic.KeyValuePair<string, object>("PumpAPressure", 99.0f));
            PollResolution.Outcome lanRes = PollResolution.ResolveLan(lanCands);
            Check("pollres.lanFirstWins",
                lanRes.Entries.Count == 1 && lanRes.Entries[0].Address == 266 &&
                Math.Abs((float)lanRes.Entries[0].Value - 125.93f) < 0.01f && lanRes.Dup == 1,
                "entries=" + lanRes.Entries.Count + " dup=" + lanRes.Dup);

            double cv;
            Check("pollres.convert.datetime", !PollResolution.TryConvert(DateTime.Now, out cv), "DateTime → false");
            Check("pollres.convert.string", !PollResolution.TryConvert("1.5", out cv), "String '1.5' → false (strict)");
            Check("pollres.convert.nan", !PollResolution.TryConvert(double.NaN, out cv), "NaN → false");
            Check("pollres.convert.float", PollResolution.TryConvert(15.5f, out cv) && Math.Abs(cv - 15.5) < 0.001, "float → true");

            // ── 3e. D1 first-valid-wins latch ─────────────────────────────────
            ComFeedLatch latch = new ComFeedLatch();
            Check("latch.firstWins", latch.OnLine("COM1", 1) && latch.IsLatched && latch.PortNumber == 1, "");
            Check("latch.sameAccepted", latch.OnLine("COM1", 1), "latched device keeps flowing");
            Check("latch.otherDiscarded", !latch.OnLine("COM2", 2) && latch.PortNumber == 1, "second device DISCARDED");
            Check("latch.silentWindow", latch.IsSilentBeyond(latch.LastEvidenceUtc.AddSeconds(6), TimeSpan.FromSeconds(5)), "silence > 5 s = stream loss");
            latch.Release();
            Check("latch.release", !latch.IsLatched && latch.PortNumber == 0, "");
            Check("latch.rearbitrate", latch.OnLine("COM2", 2) && latch.PortNumber == 2, "re-arbitration: first valid wins again");

            // ── 4. queue worker path (real C# delegate) ─────────────────────
            BusWriter wq = BusWriter.Open();
            bool done = false;
            BusWriteQueue.Post(delegate
            {
                wq.WriteFloatPair(ZoneKind.ComMain, 8, 7.25f);
                done = true;
            });
            for (int i = 0; i < 40 && !done; i++)
            {
                Thread.Sleep(50);
            }
            wq.Dispose();
            b = File.ReadAllBytes(_path);
            Check("queue.pair.7.25f", Hex(b, 80, 4) == "00 00 40 E8", Hex(b, 80, 4));
            Check("queue.count.com", Hex(b, 37, 4) == "03 00 00 00", Hex(b, 37, 4));

            // ── 5. reopen = validate, no re-init; counter persists ──────────
            BusWriter w2 = BusWriter.Open();
            Check("reopen.ok", w2.IsOk, "");
            w2.WriteFloatPair(ZoneKind.ComMain, 10, 2.5f);
            w2.Dispose();
            b = File.ReadAllBytes(_path);
            Check("reopen.pair.2.5f", Hex(b, 84, 4) == "00 00 40 20", Hex(b, 84, 4));
            Check("reopen.count.com", Hex(b, 37, 4) == "04 00 00 00", Hex(b, 37, 4));

            // ── 5b. sharing law: file held open ReadWrite by a foreign handle ──
            FileStream holder = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            BusWriter ws = BusWriter.Open();
            Check("sharing.heldOpenOk", ws.IsOk, "Open must succeed while a ReadWrite holder exists (DEFECT-1)");
            ws.WriteFloatPair(ZoneKind.ComMain, 12, 3.5f);
            ws.Dispose();
            holder.Close();
            b = File.ReadAllBytes(_path);
            Check("sharing.bytes", Hex(b, 88, 4) == "00 00 40 60", Hex(b, 88, 4) + " (3.5f = 0x40600000)");

            // ── 5c. attach law: second writer on the same named section ────────
            BusWriter wa = BusWriter.Open();
            BusWriter wb2 = BusWriter.Open();
            Check("attach.secondOk", wb2.IsOk, "same-name section ⇒ attach, not IOException");
            wb2.WriteFloatPair(ZoneKind.ComMain, 12, 2.5f);
            wa.Dispose();
            wb2.Dispose();
            b = File.ReadAllBytes(_path);
            Check("attach.bytes", Hex(b, 88, 4) == "00 00 40 20", Hex(b, 88, 4) + " (2.5f via second writer)");

            // ── 6. corruption fail-safe (never throws, disabled writer) ─────
            using (FileStream fs = File.OpenWrite(_path))
            {
                fs.Position = 0;
                fs.WriteByte(0x58); // break magic
            }
            BusWriter wBad = BusWriter.Open();
            Check("corrupt.disabled", !wBad.IsOk, "");
            wBad.WriteFloatPair(ZoneKind.ComMain, 4, 9.0f); // must be a no-op, no throw
            wBad.Dispose();

            // ── 6b. v2→v5 EXPANSION (CONTRACT_BusV2 §8) ──────────────────────
            string idUp = Guid.NewGuid().ToString("N");
            string pathUp = Path.Combine(Path.GetTempPath(), "busself_up_" + idUp + ".dat");
            string savedPath = BusWriter.BackingPathOverride;
            string savedBus = BusWriter.BusNameOverride;
            string savedMutex = BusWriter.MutexNameOverride;
            try
            {
                // craft an exact v2 file (1364 B): sentinel body + recognizable pair + zone header field
                byte[] v2 = CraftShapeFile(650, 2);
                v2[10] = 0xAB;                                            // zone header field sentinel (10..48 must survive)
                v2[76] = 0xA3; v2[77] = 0xD7; v2[78] = 0x41; v2[79] = 0x7C; // 15.79f at reg 6
                File.WriteAllBytes(pathUp, v2);

                BusWriter.BackingPathOverride = pathUp;
                BusWriter.BusNameOverride = @"Global\iCemDataMonitorBus_selftest_up_" + idUp;
                BusWriter.MutexNameOverride = @"Global\iCemDataMonitorBusMutex_selftest_up_" + idUp;

                BusWriter wu = BusWriter.Open();
                Check("upgrade.open", wu.IsOk, "v2 file must expand in place and open OK");
                wu.Dispose();

                byte[] b2 = File.ReadAllBytes(pathUp);
                Check("upgrade.len", b2.Length == BusLayout.TotalFileBytes, "len=" + b2.Length);
                Check("upgrade.magic", Hex(b2, 0, 4) == "53 54 4E 42", Hex(b2, 0, 4));
                Check("upgrade.version", Hex(b2, 4, 2) == "01 00", Hex(b2, 4, 2));
                Check("upgrade.layout", Hex(b2, 6, 2) == "05 00", Hex(b2, 6, 2) + " (expansion → layoutVer 5)");
                Check("upgrade.regcount", Hex(b2, 8, 2) == "11 27", Hex(b2, 8, 2) + " (10001 LE — D3)");
                Check("upgrade.zoneFieldPreserved", b2[10] == 0xAB, "header 10..48 must survive (val=" + b2[10].ToString("X2") + ")");
                Check("upgrade.reservedZeroed", AllZero(b2, 49, 15), "bytes 49..63 zeroed");
                Check("upgrade.bodyPairPreserved", Hex(b2, 76, 4) == "A3 D7 41 7C", Hex(b2, 76, 4) + " (reg6 pair survived)");
                Check("upgrade.bodyFillPreserved", b2[64] == 0x5A && b2[1361] == 0x5A && Hex(b2, 1362, 2) == "EE FF",
                    "body 64..1361 verbatim (first=" + b2[64].ToString("X2") + " last=" + b2[1361].ToString("X2") + ", tail=" + Hex(b2, 1362, 2) + ")");
                Check("upgrade.newAreaZeroed", AllZero(b2, 1364, 200), "new body 1364+ zeroed");

                // post-upgrade writability: reopen + com write lands at reg 4
                BusWriter wu2 = BusWriter.Open();
                Check("upgrade.reopen", wu2.IsOk, "");
                wu2.WriteFloatPair(ZoneKind.ComMain, 4, 3.5f);
                wu2.Dispose();
                b2 = File.ReadAllBytes(pathUp);
                Check("upgrade.postwrite.4", Hex(b2, 72, 4) == "00 00 40 60", Hex(b2, 72, 4) + " (3.5f at com reg 4)");
                Check("upgrade.postwrite.flag", b2[18] == 1, "com activeFlag = " + b2[18]);
                Check("upgrade.postwrite.600untouched", b2[1264] == 0x5A, "reg 600 area = preserved sentinel (AddIn never wrote it; val=" + b2[1264].ToString("X2") + ")");
            }
            finally
            {
                BusWriter.BackingPathOverride = savedPath;
                BusWriter.BusNameOverride = savedBus;
                BusWriter.MutexNameOverride = savedMutex;
                try { if (File.Exists(pathUp)) File.Delete(pathUp); } catch { }
            }

            // ── 6c. LENGTH-DERIVED regCount validation + semantic bump law ──
            // valid ⇔ magic ok ∧ version==1 ∧ layoutVer∈{2,3,4,5} ∧ 650 ≤ regCount ≤ 65535
            //         ∧ fileLength == 64 + regCount*2.
            // Expansions: v2 ∧ v3-small → v5-full. Semantic bump: layoutVer 2/3/4 → 5.
            string idLd = Guid.NewGuid().ToString("N");
            string pathLd = Path.Combine(Path.GetTempPath(), "busself_ld_" + idLd + ".dat");
            string savedPath2 = BusWriter.BackingPathOverride;
            string savedBus2 = BusWriter.BusNameOverride;
            string savedMutex2 = BusWriter.MutexNameOverride;
            try
            {
                BusWriter.BackingPathOverride = pathLd;
                BusWriter.BusNameOverride = @"Global\iCemDataMonitorBus_selftest_ld_" + idLd;
                BusWriter.MutexNameOverride = @"Global\iCemDataMonitorBusMutex_selftest_ld_" + idLd;

                // ── 6c-1. v3-small (1650/3364, layoutVer 3) — EXPANSION to 10001 ──
                File.WriteAllBytes(pathLd, CraftShapeFile(1650, 3));
                BusWriter w1650 = BusWriter.Open();
                Check("shape.1650.open", w1650.IsOk, "1650/3364 expansion source");
                w1650.WriteFloatPair(ZoneKind.ComMain, 4, 15.79f);
                w1650.WriteReg16(BusLayout.RegHeartbeat, 7);
                w1650.Dispose();
                byte[] b1650 = File.ReadAllBytes(pathLd);
                Check("shape.1650.len", b1650.Length == BusLayout.TotalFileBytes, "len=" + b1650.Length + " (expanded to v5-full 20066)");
                Check("shape.1650.bumped", Hex(b1650, 6, 2) == "05 00", Hex(b1650, 6, 2) + " (layoutVer 3→5)");
                Check("shape.1650.identity", Hex(b1650, 0, 10) == "53 54 4E 42 01 00 05 00 11 27", Hex(b1650, 0, 10));
                Check("shape.1650.pair", Hex(b1650, 72, 4) == "A3 D7 41 7C", Hex(b1650, 72, 4) + " (reg4)");
                Check("shape.1650.heartbeat", Hex(b1650, 70, 2) == "00 07", Hex(b1650, 70, 2));
                Check("shape.1650.flag", b1650[18] == 1, "com activeFlag = " + b1650[18]);
                Check("shape.1650.bodyUntouched", b1650[1364] == 0x5A, "preserved body (val=" + b1650[1364].ToString("X2") + ")");
                Check("shape.1650.tail", Hex(b1650, 3362, 2) == "EE FF", Hex(b1650, 3362, 2) + " (body tail intact)");
                Check("shape.1650.newAreaZeroed", AllZero(b1650, 3364, 200), "expansion area zeroed (3364..20065)");

                // ── 6c-2. v4-full (8404/16872, layoutVer 4) — FULL MIGRATION EXPANSION ──
                File.WriteAllBytes(pathLd, CraftShapeFile(8404, 4));
                BusWriter w8404 = BusWriter.Open();
                Check("shape.8404v4.open", w8404.IsOk, "8404/16872 layoutVer 4 = upgrade source");
                w8404.WriteFloatPair(ZoneKind.ComMain, 4, 15.79f);
                w8404.WriteReg16(BusLayout.RegHeartbeat, 7);
                w8404.Dispose();
                byte[] b8404 = File.ReadAllBytes(pathLd);
                Check("shape.8404v4.len", b8404.Length == BusLayout.TotalFileBytes, "len=" + b8404.Length + " (reshaped to 20066 — FULL MIGRATION)");
                Check("shape.8404v4.identity", Hex(b8404, 0, 10) == "53 54 4E 42 01 00 05 00 11 27", Hex(b8404, 0, 10) + " (layoutVer 5 + regCount 10001)");
                Check("shape.8404v4.pair", Hex(b8404, 72, 4) == "A3 D7 41 7C", Hex(b8404, 72, 4) + " (reg4)");
                Check("shape.8404v4.heartbeat", Hex(b8404, 70, 2) == "00 07", Hex(b8404, 70, 2));
                Check("shape.8404v4.tail", Hex(b8404, 16870, 2) == "EE FF", Hex(b8404, 16870, 2) + " (old body tail preserved byte-exact)");
                Check("shape.8404v4.newTailZeroed", AllZero(b8404, 16872, 200), "new tail 16872..20065 zero-filled");

                // ── 6c-2b. v3-full (8404/16872, layoutVer 3) — SAME EXPANSION ──
                File.WriteAllBytes(pathLd, CraftShapeFile(8404, 3));
                BusWriter wV3full = BusWriter.Open();
                Check("shape.8404v3.open", wV3full.IsOk, "v3-full 8404/16872 = upgrade source");
                wV3full.Dispose();
                byte[] bV3full = File.ReadAllBytes(pathLd);
                Check("shape.8404v3.len", bV3full.Length == BusLayout.TotalFileBytes, "len=" + bV3full.Length + " (reshaped to 20066)");
                Check("shape.8404v3.identity", Hex(bV3full, 0, 10) == "53 54 4E 42 01 00 05 00 11 27", Hex(bV3full, 0, 10));
                Check("shape.8404v3.tail", Hex(bV3full, 16870, 2) == "EE FF", Hex(bV3full, 16870, 2) + " (body tail preserved)");
                Check("shape.8404v3.newTailZeroed", AllZero(bV3full, 16872, 200), "new tail zero-filled");

                // ── 6c-3. v5-current (8404/16872, layoutVer 5) — FINAL SHAPE, OPEN AS-IS ──
                File.WriteAllBytes(pathLd, CraftShapeFile(8404, 5));
                BusWriter w5 = BusWriter.Open();
                Check("shape.v5.open", w5.IsOk, "8404/16872 layoutVer 5 = FINAL shape (dynamic regCount law) — opens as-is, no reshape");
                w5.Dispose();
                byte[] b5 = File.ReadAllBytes(pathLd);
                Check("shape.v5.identityStays", Hex(b5, 0, 10) == "53 54 4E 42 01 00 05 00 D4 20", Hex(b5, 0, 10) + " (final shape untouched)");

                // ── 6c-4. 700 regs / 1464 B (layoutVer 3) — FOREIGN legacy shape ──
                File.WriteAllBytes(pathLd, CraftShapeFile(700, 3));
                BusWriter w700 = BusWriter.Open();
                Check("shape.700.foreign", !w700.IsOk, "700/1464 layoutVer 3 ⇒ FOREIGN (not a recognized upgrade source)");
                w700.Dispose();
                byte[] b700 = File.ReadAllBytes(pathLd);
                Check("shape.700.untouched", b700.Length == 1464 && b700[6] == 0x03, "foreign file untouched (no bump, no reshape)");

                // ── 6c-5. v1 (600/1264) — DEAD ──
                File.WriteAllBytes(pathLd, CraftShapeFile(600, 1));
                BusWriter wV1 = BusWriter.Open();
                Check("shape.v1.dead", !wV1.IsOk, "v1 (600/1264) ⇒ disabled (no upgrade path)");
                wV1.Dispose();
                byte[] bV1 = File.ReadAllBytes(pathLd);
                Check("shape.v1.untouched", bV1.Length == 1264 && bV1[6] == 0x01, "v1 file untouched");

                // ── 6c-6. foreign shapes ⇒ writer disabled ──────────────────────
                File.WriteAllBytes(pathLd, CraftShapeFile(1650, 3));
                using (FileStream fs = new FileStream(pathLd, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.SetLength(3400); // 1650 regs but wrong length
                }
                BusWriter wBadLen = BusWriter.Open();
                Check("ld.foreignLength", !wBadLen.IsOk, "1650 regs / 3400 B ⇒ disabled");
                wBadLen.Dispose();

                File.WriteAllBytes(pathLd, CraftShapeFile(8404, 5));
                using (FileStream fs = new FileStream(pathLd, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.SetLength(16870); // 8404 regs but 2 B short
                }
                BusWriter wBadAce = BusWriter.Open();
                Check("ld.foreignAceLength", !wBadAce.IsOk, "8404 regs / 16870 B ⇒ disabled");
                wBadAce.Dispose();

                File.WriteAllBytes(pathLd, CraftShapeFile(1650, 9));
                BusWriter wBadLayout = BusWriter.Open();
                Check("ld.foreignLayout", !wBadLayout.IsOk, "layoutVer 9 ⇒ disabled");
                wBadLayout.Dispose();

                File.WriteAllBytes(pathLd, CraftShapeFile(649, 5));
                BusWriter wBadMin = BusWriter.Open();
                Check("ld.belowMin", !wBadMin.IsOk, "regCount 649 (below min) ⇒ disabled");
                wBadMin.Dispose();

                byte[] badVer = CraftShapeFile(1650, 5);
                badVer[4] = 0x02; // version 2
                File.WriteAllBytes(pathLd, badVer);
                BusWriter wBadVer = BusWriter.Open();
                Check("ld.foreignVersion", !wBadVer.IsOk, "version 2 ⇒ disabled");
                wBadVer.Dispose();

                // ── 6c-7. INC-03 blocked expansion: stale smaller section ──
                // A v4-full file + a RAW live section held at the OLD 16872
                // capacity simulates a stale POC holder. The file upgrades on
                // disk, the sized view is blocked ⇒ typed fail-fast with the
                // ordered-restart runbook.
                File.WriteAllBytes(pathLd, CraftShapeFile(8404, 4));
                using (FileStream staleHolder = new FileStream(pathLd, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                using (MemoryMappedFile staleSection = MemoryMappedFile.CreateFromFile(
                    staleHolder, BusWriter.BusNameOverride, 16872,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false))
                {
                    BusWriter wBlocked = BusWriter.Open();
                    Check("blocked.open", !wBlocked.IsOk, "stale 16872 section ⇒ disabled (ordered restart required)");
                    Check("blocked.typed", wBlocked.LastOpenError != null && wBlocked.LastOpenError is BusAccessDeniedException,
                        "LastOpenError = BusAccessDeniedException (got: " + (wBlocked.LastOpenError == null ? "null" : wBlocked.LastOpenError.GetType().Name) + ")");
                    Check("blocked.runbook", wBlocked.LastOpenError != null &&
                        wBlocked.LastOpenError.Message.Contains("ACCESS DENIED") &&
                        wBlocked.LastOpenError.Message.Contains("producer FIRST"),
                        "message carries the ordered-restart runbook");
                    wBlocked.Dispose();
                }
                Check("blocked.fileUpgraded", new FileInfo(pathLd).Length == BusLayout.TotalFileBytes,
                    "on-disk file upgraded to 20066 (runbook applies to the live section)");
                BusWriter wAfter = BusWriter.Open();
                Check("blocked.reopenAfterHoldersClosed", wAfter.IsOk, "reopen after stale holder closed ⇒ OK");
                wAfter.Dispose();
            }
            finally
            {
                BusWriter.BackingPathOverride = savedPath2;
                BusWriter.BusNameOverride = savedBus2;
                BusWriter.MutexNameOverride = savedMutex2;
                try { if (File.Exists(pathLd)) File.Delete(pathLd); } catch { }
            }

            // ── 7. mutex behavior: concurrent pair writes from two threads ──
            // INC-12 liveness law: the counter counts FRESH (changed) writes —
            // unique values per iteration keep the count exactly deterministic.
            File.Delete(_path);
            BusWriter wm = BusWriter.Open();
            Thread t1 = new Thread(() => { for (int i = 0; i < 200; i++) wm.WriteFloatPair(ZoneKind.ComMain, 14, i + 1.0f); });
            Thread t2 = new Thread(() => { for (int i = 0; i < 200; i++) wm.WriteFloatPair(ZoneKind.ComMain, 14, i + 1001.0f); });
            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();
            wm.Dispose();
            b = File.ReadAllBytes(_path);
            ushort mLow = (ushort)((b[92] << 8) | b[93]);
            ushort mHigh = (ushort)((b[94] << 8) | b[95]);
            float mFinal = CdabCodec.Combine(mLow, mHigh);
            bool mIntact = (mFinal >= 1.0f && mFinal <= 200.0f) || (mFinal >= 1001.0f && mFinal <= 1200.0f);
            Check("mutex.torn", mIntact, "reg14 bytes=" + Hex(b, 92, 4) + " → " + mFinal.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Check("mutex.count", Hex(b, 37, 4) == "90 01 00 00", Hex(b, 37, 4) + " (400 fresh pairs)");

            File.Delete(_path);
            BusWriter.BackingPathOverride = null;
            BusWriter.BusNameOverride = null;
            BusWriter.MutexNameOverride = null;
            TapLog.PathOverride = null;
            Console.WriteLine(_failures == 0 ? "ALL SELF-TEST ASSERTIONS PASSED" : "FAILURES: " + _failures);
            return _failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SELF-TEST EXCEPTION: " + ex);
            try { if (_path != null && File.Exists(_path)) File.Delete(_path); } catch { }
            return 2;
        }
    }
}
