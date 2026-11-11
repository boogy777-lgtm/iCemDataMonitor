// ─────────────────────────────────────────────────────────────────────────────
// Bus v5 contract — net48 re-declaration (producer duality, C# 7.3-safe).
// Canonical source: server-dotnet\iCemDataMonitor.Sdk\Bus\BusLayout.cs (W1 insite-sdk).
// This file re-declares the WIRE CONSTANTS ONLY — values are the contract:
// changing any of them = layoutVer bump (CONTRACT_BusV2.yaml §8/§9) + coordinated
// re-release of this AddIn.
//
// LAYOUT v5 (user decree 2026-08-17 — schema delta 1.5.0, bus register map v5):
//   header state block = body regs 0..3 ONLY: {comPortActive, caseState,
//   caseLanState, heartbeat}. HeaderRegCount 16 → 4. Com_stream starts at reg 4
//   (the v4 4..15 overlap/skip rule is GONE).
//   heartbeat PARADIGM CHANGE: reg 3 advances +1/s ALWAYS while the AddIn is
//   loaded (pipeline-alive: iCem up ∧ POC reading ∧ Modbus publishing);
//   NO LONGER tied to Streaming state. D2: wraps 32000→0.
//   D1 multi-device law (reg 0): first-valid-wins latch — the dll locks onto
//   the FIRST COM device delivering parseable telemetry; others discarded;
//   re-arbitrate only on stream loss.
//   reg 1 caseState / reg 2 caseLanState = tap-exposed enums ONLY (passthrough
//   of vendor enum values); invented states forbidden; unknown/unavailable = 0.
//   Zones (ALL AddIn-owned — v5 has NO POC writer): com_stream 4..199 (float32
//   CDAB, addr=4+idx*2 — EXCEPT idx0: regs 4..5 = UnixTimestampSeconds int32
//   RAW BITS in the CDAB word order, contract v5.1 CHANGE 1 user decree
//   2026-08-18 — the consumer owns read formatting, float-read = garbage BY
//   DECREE; idx1 Unit Num STAYS float32, documented asymmetry Э2b T03);
//   lan_stream_crio 200..399 (float32 CDAB, addr=200+idx*2,
//   96 cRIO signals per Wireshark/PROTOCOL.md); raw_com_line 400..599
//   (contract v5.1 CHANGE 2 user decree 2026-08-18: last COM line split into
//   CSV fields, field i → float32 CDAB pair at 400+i*2, ≤100 fields,
//   POSITIONAL over the raw line AS-IS incl. excludeFromTelemetry fields —
//   template-agnostic; unparseable field ⇒ pair not written, stale-hold;
//   the v5 u16-per-char path is RETIRED, git history only);
//   ace_full RESERVE 600..10000 = DEFERRED — nobody writes in v5 (exhaustive
//   ACE dump layout designed LATER from measured ACE composition).
//   STALE-HOLD LAW: no writer ever zeroes body registers on channel loss/stop —
//   last values persist; heartbeat unaffected by channel state.
//   D3 DICTIONARY: current-default = 10001 regs / 20066 B (64 + 10001×2) so the
//   reserve physically exists.
//   FULL MIGRATION (user decision 2026-08-17): v3-full/v4-full 8404/16872 are
//   UPGRADE SOURCES — expanded IN PLACE to 10001/20066 on first open, NEVER
//   opened as final shapes.
//   D4 LIVENESS OWNERSHIP: the Lan header block (tick@0x13(19)..0x1A(26),
//   flag@0x1B(27), counter@0x29(41)..0x2C(44)) governs lan_stream_crio, writer =
//   AddIn. Com block (tick@10, flag@18, counter@37) ∧ Raw block (tick@28,
//   flag@36, counter@45) = AddIn, unchanged. These liveness cells are
//   FILE-HEADER bytes 0..63 — zero collision with data cells (body starts at
//   file offset 64). The future ace_full writer will claim the former Wits
//   block (49..63) — that IS a header schema change ⇒ layoutVer bump at that
//   time.
//
// SHAPE LAW v5 (FULL MIGRATION, user decision 2026-08-17 — schema delta 1.6.0):
//   FINAL shape valid ⇔ magic=='STNB' ∧ version==1 ∧ layoutVer==5 ∧ 650 ≤
//   regCount ≤ 65535 ∧ fileLength == 64 + regCount×2 (IsValidShape).
//   layoutVer < 5 = UPGRADE-SOURCE layouts, NEVER final shapes: the only
//   recognized legacy shapes are the exact upgrade sources (IsUpgradeable) —
//   v2 650/1364, v3-small 1650/3364, v3-full/v4-full 8404/16872 — each of which
//   expands ONCE, under the mutex, to 10001/20066 (layoutVer=5, regCount=10001,
//   old body preserved byte-exact, new tail zero-filled by SetLength). v2
//   additionally zeroes former-wits header bytes 49..63; v3-full/v4-full need NO
//   re-zeroing (49..63 already reserved-zero by induction) — the expansion must
//   NOT touch them. Any other legacy shape ⇒ fail-safe (rejected); v1 600/1264
//   is DEAD. After migration every layoutVer-5 file on disk is exactly
//   10001/20066.
//   BusLayout.RegCount (10001) = current-default dictionary, ¬ the acceptance
//   gate (future growth = ini-only, no layoutVer bump).
//   INC-03 ordered-restart: the MMF section capacity is frozen until ALL handles
//   close — the expansion runs while the AddIn is the FIRST/ONLY opener; a live
//   small-capacity section held by another process blocks the sized view ⇒ the
//   writer fails fast with BusAccessDeniedException.ForSectionCapacity carrying
//   the ordered-restart runbook (stop consumers; start producer iCem.exe FIRST,
//   POC consumers after).
//
// ENDIANNESS LAW (CONTRACT_BusV2.yaml §2/§4):
//   header multi-byte fields: LITTLE-endian (BitConverter natural order);
//   body registers (u16 words): BIG-endian bytes inside each word — the body
//   is a byte-exact Modbus-TCP wire image (Modbus layer = memcpy).
// ─────────────────────────────────────────────────────────────────────────────

using System;

namespace Burservis.iCem.DataMonitor.Bus
{
    public static class BusLayout
    {
        // ── Identity ──────────────────────────────────────────────────────────
        public const uint Magic = 0x424E5453;                       // file bytes 'STNB'
        public static readonly byte[] MagicBytes = { (byte)'S', (byte)'T', (byte)'N', (byte)'B' };
        public const ushort Version = 1;                            // frozen (bus protocol version, NOT layout)
        public const ushort LayoutVersion = 5;                      // v5 = bus register map v5 (user decree 2026-08-17)

        /// <summary>Current-default total holding-register count (HR 0..10000 — ace_full
        /// reserve 600..10000 DEFERRED, nobody writes). NOT the validity anchor: a
        /// valid FINAL bus file may declare ANY regCount in [650..65535] with the
        /// matching length at layoutVer==5 (shape law v5). v3/v4-full 8404/16872
        /// files are UPGRADE SOURCES — expanded to this dictionary on first open
        /// (FULL MIGRATION, user decision 2026-08-17); every migration path lands
        /// at exactly 10001/20066.</summary>
        public const ushort RegCount = 10001;

        // ── Sizes (current-default dictionary — ¬ the validity anchor) ────────
        public const int HeaderBytes = 64;
        public const int BytesPerReg = 2;
        public const int BodyBytes = 20002;                         // 10001 × u16
        public const int TotalFileBytes = HeaderBytes + BodyBytes;  // 20066

        // ── Historical shapes (upgrade sources / history) ─────────────────────
        public const ushort LayoutVersionV2 = 2;
        public const ushort RegCountV2 = 650;
        public const int TotalFileBytesV2 = 1364;                   // 64 + 1300

        public const ushort LayoutVersionV3 = 3;
        public const ushort RegCountV3Small = 1650;
        public const int TotalFileBytesV3Small = 3364;              // 64 + 3300

        public const ushort LayoutVersionV4 = 4;                    // frozen history (v4 replaced row-for-row by v5)
        public const ushort RegCountV4Full = 8404;                  // v3-full/v4-full dictionary (UPGRADE SOURCE — FULL MIGRATION)
        public const int TotalFileBytesV4Full = 16872;              // 64 + 16808

        // ── regCount acceptance bounds (length-derived rule) ──────────────────
        public const ushort RegCountMin = 650;
        public const ushort RegCountMax = 65535;

        // ── Names ─────────────────────────────────────────────────────────────
        /// <summary>Global mutex for the iCemDataMonitor shared-memory bus (v1 wire contract).</summary>
        public const string MutexName = @"Global\iCemDataMonitorBusMutex_v1";
        /// <summary>Global MMF section name for the iCemDataMonitor shared-memory bus (v1 wire contract).</summary>
        public const string BusName = @"Global\iCemDataMonitorBus_v1";
        public const string BackingFilePath = @"C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat";

        // ── Header field offsets (byte-exact, little-endian, v1-stable) ───────
        public const int HeaderMagicOffset = 0;                     // 0..3  'STNB'
        public const int HeaderVersionOffset = 4;                   // 4..5  u16 LE = 1
        public const int HeaderLayoutVersionOffset = 6;             // 6..7  u16 LE = 5
        public const int HeaderRegCountOffset = 8;                  // 8..9  u16 LE (runtime data ∈ [650..65535])

        public const int ZoneComLastWriteTickOffset = 10;           // 10..17 u64 LE — com_stream liveness (AddIn)
        public const int ZoneComActiveFlagOffset = 18;              // 18     u8  — com_stream activeFlag (AddIn)
        public const int ZoneLanLastWriteTickOffset = 19;           // 19..26 u64 LE — lan_stream_crio liveness (AddIn, D4)
        public const int ZoneLanActiveFlagOffset = 27;              // 27     u8  — lan_stream_crio activeFlag (AddIn, D4)
        public const int ZoneRawLastWriteTickOffset = 28;           // 28..35 u64 LE — raw_com_line liveness (AddIn)
        public const int ZoneRawActiveFlagOffset = 36;              // 36     u8  — raw_com_line activeFlag (AddIn)

        public const int ProducerComWriteCountOffset = 37;          // 37..40 u32 LE (AddIn com_stream)
        public const int ProducerLanWriteCountOffset = 41;          // 41..44 u32 LE (AddIn lan_stream_crio, D4)
        public const int ProducerRawWriteCountOffset = 45;          // 45..48 u32 LE (AddIn raw_com_line)

        public const int ZoneWitsLastWriteTickOffset = 49;          // 49..56 RESERVED-ZERO (former wits; must stay zero)
        public const int ZoneWitsActiveFlagOffset = 57;             // 57     RESERVED-ZERO
        public const int ProducerWitsWriteCountOffset = 58;         // 58..61 RESERVED-ZERO
        public const int HeaderReservedOffset = 62;                 // 62..63 reserved (must stay zero)
        public const int HeaderReservedBytes = 2;

        public const int BodyOffset = 64;                           // 64..(64+regCount×2−1) body

        // ── Header STATE register map v5 (body words 0..3 = file offsets 64..71;
        //    ALL AddIn-owned; POC writes NOTHING in the header state block) ─────
        // CURRENT-STATE law: the header block carries the state observed at the
        // last writer tick — no timeout/threshold-derived values. Stale-hold:
        // last values persist; no zeroing on channel loss (state regs 1..2 are
        // vendor passthroughs; reg 0 = latch truth: 0 = no device feeding).
        public const int RegComPort = 0;                            // LIVE — D1 latch: N of COMN actively feeding; 0=none
        public const int RegCaseState = 1;                          // LIVE — iCem case state (RealtimeState passthrough; unknown=0)
        public const int RegCaseLanState = 2;                       // LIVE — LAN/cRIO state (LANManager.LANStatus passthrough; unknown=0)
        public const int RegHeartbeat = 3;                          // LIVE — +1/s ALWAYS while AddIn loaded; wraps 32000→0 (D2)

        /// <summary>Header state register count (0..3) — v5: 4 regs.</summary>
        public const int HeaderRegCount = 4;

        /// <summary>D2 heartbeat wrap modulus: heartbeat wraps 32000→0.</summary>
        public const ushort HeartbeatWrap = 32000;

        // ── v5 ZONE BOUNDS (bus register map v5 — user decree 2026-08-17) ─────
        // Ownership: com_stream / lan_stream_crio / raw_com_line = AddIn (v5 has
        // NO POC writer); ace_full RESERVE 600..10000 = DEFERRED — nobody writes
        // in v5 (exhaustive ACE dump layout designed LATER).
        public const int ZoneBaseMin = 4;
        public const int ComStreamBaseReg = 4;                      // 4..199  (98 float32 CDAB pairs, addr=4+idx*2)
        public const int ComStreamRegCount = 196;
        /// <summary>com_stream idx0 (flecs 'Time &amp; Date') — contract v5.1
        /// CHANGE 1 (user decree 2026-08-18): written as UnixTimestampSeconds
        /// int32 RAW BITS in the CDAB word order (low word @ 4, high word @ 5,
        /// BE bytes per word) so an int32/uint32 read yields unix seconds;
        /// float32 read = garbage BY DECREE (consumer owns read formatting).
        /// Dispatch: DataMonitorModule com-write loop → WriteInt32Pair (every other
        /// com address → WriteFloatPair). idx1 'Unit Num' (reg 6) STAYS float32
        /// (small int, float-exact — documented asymmetry, Э2b row T03).</summary>
        public const int ComTimestampReg = 4;
        public const int LanStreamCrioBaseReg = 200;                // 200..399 (100 float32 CDAB pairs, addr=200+idx*2; 96 cRIO signals)
        public const int LanStreamCrioRegCount = 200;
        public const int RawComLineBaseReg = 400;                   // 400..599 (v5.1: float32 CDAB positional CSV fields, field i @ 400+i*2, ≤100 fields)
        public const int RawComLineRegCount = 200;
        public const int AceFullBaseReg = 600;                      // 600..10000 ace_full RESERVE (DEFERRED — nobody writes)
        public const int AceFullEndReg = 10000;
        public const int AceFullRegCount = 9401;                    // 600..10000

        /// <summary>First register NOT written by this AddIn (ace_full reserve floor).</summary>
        public const int AddInMaxReg = 599;

        // ── reg 1 caseState values — RealtimeState passthrough (tap-exposed ONLY;
        //    vendor enum Halliburton.iCem.CementingData.Objects.Enumerations.
        //    RealtimeState; NO invented states; unknown/unavailable = 0) ────────
        public const short CaseStateUnknown = 0;                    // Unknown ∧ no-active-case/unavailable
        public const short CaseStateNotStarted = 1;
        public const short CaseStateInProgress = 2;
        public const short CaseStateCompleted = 3;
        public const short CaseStateSuspended = 4;
        public const short CaseStateSimulationRunning = 5;

        // ── reg 2 caseLanState values — ComPortStates passthrough (tap-exposed
        //    ONLY; vendor enum Halliburton.iCem.RealTime.SerialDataSetup.
        //    ComPortStates via LANManager.LANStatus; unknown/unavailable = 0) ───
        public const short CaseLanStateNone = 0;
        public const short CaseLanStateSelected = 1;
        public const short CaseLanStateStreaming = 2;
        public const short CaseLanStateRecording = 4;
        public const short CaseLanStateLostConnection = 8;
        public const short CaseLanStatePausing = 16;

        // ── Shape validation (FULL MIGRATION, user decision 2026-08-17) ────────
        /// <summary>FINAL-shape acceptance law (v5 shape law) — THE validity anchor
        /// for files that may be mapped as-is.
        /// valid ⇔ version==1 ∧ layoutVer==5 ONLY ∧ 650 ≤ regCount ≤ 65535 ∧
        /// fileLength == 64 + regCount×2. layoutVer&lt;5 is an UPGRADE-SOURCE
        /// layout, NEVER a final shape (see IsUpgradeable); every other legacy
        /// shape ⇒ FALSE ⇒ caller fail-safe. v1 (600/1264) is DEAD.</summary>
        public static bool IsValidShape(ushort version, ushort layoutVer, ushort regCount, long fileLength)
        {
            if (version != Version)
            {
                return false;
            }
            if (layoutVer != LayoutVersion)
            {
                return false; // layoutVer < 5 = upgrade-source layout, ¬ a final shape
            }
            if (regCount < RegCountMin || regCount > RegCountMax)
            {
                return false;
            }
            return fileLength == HeaderBytes + (long)regCount * BytesPerReg;
        }

        /// <summary>In-place EXPANSION eligibility (FULL MIGRATION): every known
        /// legacy dictionary shape grows ONCE to the FULL v5 dictionary
        /// (10001 regs / 20066 B, layoutVer=5). TRUE ⇔ the exact v2 shape
        /// (1,2,650,1364) ∨ v3-small (1,3,1650,3364) ∨ v3-full (1,3,8404,16872)
        /// ∨ v4-full (1,4,8404,16872). Anything else (incl. every layoutVer-5
        /// shape — final, open as-is — ∧ the DEAD v1) ⇒ FALSE ⇒ fail-safe.</summary>
        public static bool IsUpgradeable(ushort version, ushort layoutVer, ushort regCount, long fileLength)
        {
            if (version != Version)
            {
                return false;
            }
            if (layoutVer == LayoutVersionV2 && regCount == RegCountV2 && fileLength == TotalFileBytesV2)
            {
                return true;
            }
            if (layoutVer == LayoutVersionV3 && regCount == RegCountV3Small && fileLength == TotalFileBytesV3Small)
            {
                return true;
            }
            if ((layoutVer == LayoutVersionV3 || layoutVer == LayoutVersionV4)
                && regCount == RegCountV4Full && fileLength == TotalFileBytesV4Full)
            {
                return true;
            }
            return false;
        }
    }

    /// <summary>Zone identity — byte underlying = header zone block order (positional
    /// binding). APPEND-ONLY order: NEVER renumber. v5 AddIn writes ComMain
    /// (com_stream) ∧ Lan (lan_stream_crio, D4 liveness block) ∧ RawReserve
    /// (raw_com_line); Wits/LanExt are historical (never written).</summary>
    public enum ZoneKind : byte
    {
        ComMain = 0,
        Lan = 1,
        RawReserve = 2,
        Wits = 3,
        LanExt = 4,
    }

    /// <summary>
    /// float32 ⇄ CDAB register pair codec (net48: no SingleToUInt32Bits).
    /// reg[N] = low word (bytes C,D), reg[N+1] = high word (bytes A,B).
    /// Pins: 1.0f → (0x0000, 0x3F80); 15.79f → (0xA3D7, 0x417C).
    /// </summary>
    public static class CdabCodec
    {
        public static void Split(float value, out ushort low, out ushort high)
        {
            byte[] b = BitConverter.GetBytes(value); // LE on all Windows targets
            uint bits = BitConverter.ToUInt32(b, 0);
            low = (ushort)(bits & 0xFFFF);
            high = (ushort)(bits >> 16);
        }

        public static float Combine(ushort low, ushort high)
        {
            uint bits = (uint)low | ((uint)high << 16);
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }
    }
}
