// iCemDataMonitor.Sdk.Bus — Shared-Memory Bus v5 contract, deployed as v5.1 (TZ_ARCH_V2.yaml, W1 → schema deltas v1.1.0..v1.6.0 + contract v5.1 changes 2026-08-18).
// Pure contract: BCL-only, attribute-free, no System.Text.Json, no dependencies.
//
// PRODUCER DUALITY (net48):
//   The iCem DataMonitor AddIn (net48, LangVersion 8.0 — bus code 7.3-style by
//   convention, FROZEN toolchain) does NOT compile this file. It re-declares
//   matching constants in its own project
//   (C:\Users\HALLIBURTON\iCemPOC\Burservis.iCem.DataMonitor\BusLayout.cs; same producer-duality
//   pattern as the archived DataTap contract, specs\archive\v1\CONTRACT_DataTap.yaml).
//   The AddIn copy must re-declare:
//     Magic, MagicBytes, Version, LayoutVersion, RegCount, HeaderBytes, BodyBytes,
//     TotalFileBytes, MutexName, BusName, all header offsets (49..63 reserved-zero
//     — former wits block), the header STATE register map RegComPort/RegCaseState/
//     RegCaseLanState/RegHeartbeat (0..3), zone bounds (com_stream 4..199,
//     lan_stream_crio 200..399, raw_com_line 400..599; ace_full reserve 600..10000
//     is DEFERRED — the AddIn MUST NOT write it), the upgrade-source shape
//     constants (v2 650/1364, v3-small 1650/3364, v3/v4-full 8404/16872 — ALL
//     expand to 10001/20066 under the mutex, FULL MIGRATION), the CdabCodec
//     pair split, and the v5.1 extras (user decree 2026-08-18):
//     ComTimestampReg=4 (com_stream idx0 = UnixTimestampSeconds int32 RAW BITS
//     in the CDAB word order) and AddInMaxReg=599 (first reg NOT written).
//   These values are the wire contract: changing ANY of them = layoutVer bump
//   (§9 evolution, CONTRACT_BusV2.yaml) + coordinated re-release of the AddIn.
//   The AddIn owns header regs 0..3 + com_stream + lan_stream_crio + raw_com_line
//   (single-writer-per-register law; v5 has NO POC writer — ace_full 600..10000
//   is reserved/DEFERRED).
//   The AddIn's open-validation MUST apply the v5 shape law: FINAL shapes are
//   layoutVer==5 with any regCount ∈ [650..65535] and the matching file length
//   (IsValidShape); layoutVer<5 is an UPGRADE-SOURCE layout — only the exact
//   legacy shapes (IsUpgradeable) are recognized and each expands ONCE to
//   10001/20066; anything else legacy = fail-safe reject.
//
// LAYOUT v5 (user decree 2026-08-17 — schema delta 1.5.0, bus register map v5):
//   header state block = body regs 0..3 ONLY: {comPortActive, caseState,
//   caseLanState, heartbeat}. HeaderRegCount 16 → 4. Com_stream starts at reg 4
//   (no overlap — the v4 4..15 overlap/skip rule is GONE).
//   heartbeat PARADIGM CHANGE: reg 3 advances +1/s ALWAYS while the AddIn is
//   loaded (iCem running) = pipeline-alive signal (iCem up ∧ POC reading ∧
//   Modbus publishing); NO LONGER tied to Streaming state. D2: wraps 32000→0.
//   Frozen ⇔ iCem/AddIn dead (NOT ⇔ ¬Streaming).
//   D1 multi-device law (reg 0): first-valid-wins latch — the dll locks onto the
//   FIRST COM device delivering parseable telemetry; others discarded;
//   re-arbitrate only on stream loss.
//   reg 1/reg 2 = iCem case state / LAN state — ONLY values actually exposed by
//   the iCem tap API; invented states forbidden; unknown/unavailable = 0.
//   Zones (ALL AddIn-owned — v5 has NO POC writer): com_stream 4..199 (98 CDAB
//   pairs, addr=4+idx*2 — v5.1 CHANGE 1 (user decree 2026-08-18): idx0 is
//   UnixTimestampSeconds int32 RAW BITS in the CDAB word order, float-read of
//   idx0 = garbage BY DECREE, the consumer owns read formatting; idx1 'Unit Num'
//   STAYS float32, documented asymmetry); lan_stream_crio 200..399 (100 float32
//   CDAB pairs, addr=200+idx*2, 96 cRIO signals per Wireshark/PROTOCOL.md
//   curated view); raw_com_line 400..599 (v5.1 CHANGE 2, user decree 2026-08-18:
//   last COM line split into CSV fields — float32 CDAB positional fields,
//   field i → 400+i*2, ≤100 fields, positional over the raw line AS-IS incl.
//   excludeFromTelemetry fields; unparseable field ⇒ pair not written,
//   stale-hold; the v5 u16-per-char path is RETIRED, git history only).
//   CONTRACT v5.1 (user decrees 2026-08-18): the two body changes above did NOT
//   bump layoutVer — semantic-only, header schema untouched, layoutVer stays 5.
//   D3 dictionary growth (user decision): default dictionary 8404 → 10001
//   regs (20066 B) so the reserved range physically exists.
//   ace_full 600..10000 DEFERRED (user decision 2026-08-17): reserved, NOBODY
//   writes in v5; the exhaustive ACE dump layout is designed LATER from measured
//   ACE composition (documentation-only future work).
//   FULL MIGRATION (user decision 2026-08-17): the v4-full shape 8404/16872 is
//   NO LONGER a valid final shape — it is an UPGRADE SOURCE: expanded IN PLACE
//   to 10001/20066 (layoutVer=5, regCount=10001, body 64..16871 preserved
//   byte-exact, tail 16872..20065 zero-filled). v3-full 8404/16872 (identical
//   geometry, layoutVer 3) migrates the same way; v2 650/1364 and v3-small
//   1650/3364 already target 10001. After migration every layoutVer-5 file on
//   disk is exactly 10001/20066.
//   v4-full header bytes 49..63 need NO re-zeroing on migration — they are
//   reserved-zero in every v4-full file by induction (v2-upgrade zeroing ∨
//   all-zero fresh create); pinned by the V34Full_To_V5Full_Expansion tests.
//   D4 header liveness ownership (architect decision): Lan block (tick@19..26,
//   flag@27, counter@41..44) governs lan_stream_crio, writer = AddIn (former
//   ace_full/POC claim removed). The future ace_full writer will claim the
//   former Wits block (49..63) — that IS a header schema change ⇒ layoutVer
//   bump at that time.
//   STALE-HOLD LAW (new global invariant): NO writer ever zeroes registers on
//   channel loss/stop — last values persist in cells; disabling a channel freezes
//   its zone; heartbeat keeps ticking.
//   layoutVer 4 → 5; v4→v5 = FULL MIGRATION expansion (body preserved, tail
//   zeroed — see IsUpgradeable docs). Capacity probe mechanism unchanged
//   (sized to RUNTIME regCount; default dictionary now 10001/20066).
//   INC-03 ordered-restart law: the MMF section capacity is frozen at creation
//   until ALL handles close — the expansion needs the upgrader as the
//   FIRST/ONLY opener; a live small-capacity section held by another process
//   blocks the sized view ⇒ the opener must fail fast with the typed
//   section-capacity access-denied error carrying the ordered-restart runbook
//   (stop consumers, then producer; start producer/upgrader FIRST).
//
// LAYOUT v4 (frozen history — schema delta 1.4.0):
//   header state block 0..15 (COM 0..7, LAN 8..15), live set {0,1,2,5,8,9,10};
//   HeaderRegCount 16. Replaced wholesale by v5 (row-for-row).
//
// LAYOUT v3 (operator directives — schema deltas 1.2.0 ∧ 1.3.0):
//   WITS REMOVED: zone 600..649 stays in the layout (no body shift) but is
//   RESERVED — never written by any writer; external A5 Modbus writes persist
//   there. Former wits header fields 49..61 → reserved-zero (49..63 all zero).
//   lan_ext APPENDED: 650..8403 (7754 regs = 3877 float32 — the FULL ACE numeric
//   dictionary extension), ZoneKind.LanExt = 4.
//   lan_ext has NO dedicated header block — it SHARES the Lan zone liveness
//   (tick@19..26, flag@27, counter@41..44) because the same single writer
//   (AceSniffer→LanBusSink — REMOVED 2026-08-27, producer-only decree: the
//   AddIn is now the only writer) owned both ranges. This is the shared-liveness law.
//
// SHAPE LAW v5 (FULL MIGRATION, user decision 2026-08-17 — schema delta 1.6.0):
//   FINAL shape valid ⇔ magic=='STNB' ∧ version==1 ∧ layoutVer==5 ∧ 650 ≤ regCount
//   ≤ 65535 ∧ fileLength == 64 + regCount × 2 (header self-description).
//   layoutVer < 5 = UPGRADE-SOURCE layouts, NEVER final shapes: the only
//   recognized legacy shapes are the exact upgrade sources (IsUpgradeable) —
//   v2 650/1364, v3-small 1650/3364, v3-full/v4-full 8404/16872 — each of which
//   expands ONCE, under the mutex, to 10001/20066. Any other legacy shape ⇒
//   fail-safe (rejected).
//   BusLayout.RegCount (10001) is the CURRENT-DEFAULT dictionary, NOT the
//   acceptance gate: layers size their views from the header regCount / ini
//   total= / snapshot, ¬ from the consts. Future dictionary growth = ini-only
//   (total=) — no layoutVer bump, no recompile.
//
// ENDIANNESS LAW:
//   Header multi-byte fields: LITTLE-endian (both producers/consumers are Windows
//   .NET — BitConverter natural order).
//   Body registers (u16 words): BIG-endian bytes inside each word — the body is a
//   byte-exact copy of the Modbus-TCP wire frame (TZ_ARCH_V2 §4: "Modbus-слой =
//   memcpy"). CdabCodec operates on WORD VALUES (endianness-free); byte layout of
//   a word in the file is the mapping layer's duty (AddIn writer / BusReader).

namespace iCemDataMonitor.Sdk.Bus;

/// <summary>
/// Bus v5 layout constants (deployed as contract v5.1 — body-semantic changes
/// 2026-08-18, layoutVer NOT bumped). All byte offsets are EXACT file offsets
/// within the persisted mapping (Data.dat). Current-default dictionary:
/// 20066 B = 64 B header + 20002 B body (10001 regs — ace_full reserve
/// 600..10000, DEFERRED, nobody writes; v4-full 8404/16872 is an UPGRADE SOURCE
/// — expanded in place to 10001/20066 on first open, NEVER a valid final shape).
///
/// DYNAMIC-REGCOUNT LAW (v1.3.0): the validity anchor is the header
/// self-description, ¬ a compile-time constant — RegCount/BodyBytes/
/// TotalFileBytes are the CURRENT-DEFAULT dictionary size only. Layers MUST
/// size their views from the header regCount / ini total= / snapshot; see
/// IsValidShape for the acceptance law and IsUpgradeable for the expansion paths.
/// </summary>
public static class BusLayout
{
    // ── Identity ────────────────────────────────────────────────────────────
    /// <summary>Magic 'STNB' as little-endian u32 (file bytes: 53 54 4E 42).</summary>
    public const uint Magic = 0x424E5453;

    /// <summary>Magic as raw bytes 'S','T','N','B' (compare against mapping prefix).</summary>
    public static readonly byte[] MagicBytes = { (byte)'S', (byte)'T', (byte)'N', (byte)'B' };

    /// <summary>Bus protocol version. Frozen at 1 (producer never re-released).</summary>
    public const ushort Version = 1;

    /// <summary>Header layout version. Bump ONLY on header schema change (TZ_ARCH_V2 §4 evolution).
    /// v5 (this): bus register map v5 (user decree 2026-08-17) — header state block
    /// 0..3 {comPortActive, caseState, caseLanState, heartbeat(always-tick, wrap
    /// 32000)}, HeaderRegCount 16 → 4, zones com_stream 4..199 / lan_stream_crio
    /// 200..399 / raw_com_line 400..599 (ALL AddIn-owned) / ace_full reserve
    /// 600..10000 (DEFERRED — nobody writes); default dictionary 10001 regs (D3);
    /// Lan liveness block governs lan_stream_crio (D4); stale-hold law (no
    /// zero-on-stop). Contract v5.1 (user decrees 2026-08-18) did NOT bump
    /// layoutVer: com_stream idx0 → UnixTimestampSeconds int32 RAW BITS (CDAB
    /// word order) ∧ raw_com_line → CSV positional float32 fields are body
    /// semantics only — the header schema is untouched.
    /// v4: 16 state registers (0..15), HeaderRegCount 16, ownership AddIn 0..11 /
    /// POC 12..15. v4/v3 files carry UPGRADE-SOURCE layouts in v5 (FULL
    /// MIGRATION — expanded to 10001/20066 on first open, see IsUpgradeable).
    /// v3: lan_ext appended (650..8403), wits removed → reserved, former wits header
    /// fields 49..61 reserved-zero. Dictionary GROWTH alone (regCount change with
    /// the same header schema) does NOT bump this — see the dynamic-regCount law.</summary>
    public const ushort LayoutVersion = 5;

    /// <summary>layoutVer of the v4 file this layout upgrades from. v4→v5 is a
    /// FULL MIGRATION expansion (user decision 2026-08-17): v4-full 8404/16872
    /// is reshaped IN PLACE to 10001/20066 (layoutVer=5, regCount=10001, body
    /// 64..16871 preserved byte-exact, tail 16872..20065 zero-filled) — v4 is
    /// an UPGRADE SOURCE, never a final shape.</summary>
    public const ushort LayoutVersionV4 = 4;

    /// <summary>layoutVer of the v3 file this layout upgrades from. v3 shapes are
    /// UPGRADE SOURCES in v5 (FULL MIGRATION): v3-small 1650/3364 AND v3-full
    /// 8404/16872 both expand to 10001/20066 (see IsUpgradeable) — v3 is never
    /// a final shape.</summary>
    public const ushort LayoutVersionV3 = 3;

    /// <summary>Current-default total holding-register count (HR 0..10000 — ace_full
    /// reserve 600..10000 DEFERRED, nobody writes). NOT the validity anchor: a valid
    /// FINAL bus file may declare ANY regCount in [RegCountMin..RegCountMax] with
    /// fileLength == 64 + regCount × 2 at layoutVer==5 (shape law v5). v3/v4-full
    /// 8404/16872 files are UPGRADE SOURCES — expanded to this dictionary on first
    /// open (FULL MIGRATION, user decision 2026-08-17); every migration path lands
    /// at exactly 10001/20066. Layers size from the header regCount / ini total=,
    /// ¬ from this const.</summary>
    public const ushort RegCount = 10001;

    // ── Sizes (current-default dictionary — ¬ the validity anchor) ──────────
    /// <summary>Header size in bytes (64).</summary>
    public const int HeaderBytes = 64;

    /// <summary>Bytes per register word (u16).</summary>
    public const int BytesPerReg = 2;

    /// <summary>Body size in bytes for the current-default dictionary (10001 regs × 2).</summary>
    public const int BodyBytes = 20002;

    /// <summary>Total file size in bytes for the current-default dictionary (64 + 20002 = 20066).
    /// A valid file's actual size is 64 + headerRegCount × 2 — compute at runtime.</summary>
    public const int TotalFileBytes = HeaderBytes + BodyBytes;

    // ── Dynamic-regCount law bounds (v1.3.0) ────────────────────────────────
    /// <summary>Smallest accepted regCount (650 = v2 shape, the smallest accepted dictionary).</summary>
    public const ushort RegCountMin = 650;
    /// <summary>Largest accepted regCount (u16 header field ceiling).</summary>
    public const ushort RegCountMax = 65535;

    // ── v2 shape (upgrade source → v3-full) ─────────────────────────────────
    /// <summary>layoutVer of the v2 file this layout upgrades from.</summary>
    public const ushort LayoutVersionV2 = 2;
    /// <summary>regCount of the v2 file (650).</summary>
    public const ushort RegCountV2 = 650;
    /// <summary>Total file size of the v2 file (64 + 1300).</summary>
    public const int TotalFileBytesV2 = 1364;

    // ── v3-small shape (upgrade source → v3-full) ───────────────────────────
    /// <summary>regCount of the v3-small file (delta 1.2.0: lan_ext 650..1649).</summary>
    public const ushort RegCountV3Small = 1650;
    /// <summary>Total file size of the v3-small file (64 + 3300).</summary>
    public const int TotalFileBytesV3Small = 3364;

    // ── v3-full / v4-full shape (FULL MIGRATION upgrade source → v5-full) ──
    /// <summary>regCount of the v3-full/v4-full file (8404 — the pre-v5 full dictionary).
    /// UPGRADE SOURCE (user decision 2026-08-17): expanded to RegCount on first open,
    /// never opened as a final shape.</summary>
    public const ushort RegCountV4Full = 8404;
    /// <summary>Total file size of the v3-full/v4-full file (64 + 16808).</summary>
    public const int TotalFileBytesV4Full = 16872;

    /// <summary>
    /// FINAL-shape acceptance law (v5 shape law, FULL MIGRATION — user decision
    /// 2026-08-17) — THE validity anchor for files that may be mapped as-is.
    /// shape valid ⇔ magic=='STNB' ∧ version==1 ∧ layoutVer==5 ∧
    /// 650 ≤ regCount ≤ 65535 ∧ fileLength == 64 + regCount × 2.
    /// The header self-describes the dictionary size; total size is RUNTIME DATA
    /// (station.ini total= + header regCount), ¬ a compile-time constant.
    /// layoutVer &lt; LayoutVersion is an UPGRADE-SOURCE layout, NEVER a final
    /// shape: the only recognized legacy shapes are the exact upgrade sources in
    /// IsUpgradeable (v2 650/1364, v3-small 1650/3364, v3-full/v4-full 8404/16872)
    /// — each expands ONCE to 10001/20066. Every other legacy shape ⇒ FALSE ⇒
    /// caller fail-safe. layoutVer=5 files open as-is (never upgrade candidates).
    /// </summary>
    public static bool IsValidShape(uint magic, ushort version, ushort layoutVer, ushort regCount, long fileLength) =>
        magic == Magic &&
        version == Version &&
        layoutVer == LayoutVersion &&
        regCount >= RegCountMin && regCount <= RegCountMax &&
        fileLength == HeaderBytes + (long)regCount * BytesPerReg;

    /// <summary>
    /// In-place EXPANSION eligibility (FULL MIGRATION, user decision 2026-08-17):
    /// every known legacy dictionary shape grows ONCE to the FULL v5 dictionary
    /// (RegCount = 10001, layoutVer = 5). Upgrade executes ONCE at open, under
    /// the mutex (first opener upgrades — in practice the AddIn producer: the
    /// INC-03 ordered-restart law puts the producer/upgrader FIRST).
    /// TRUE ⇔ the mapped file has an EXACT legacy shape:
    ///   v2       (version 1, layoutVer 2, 650 regs, 1364 B)
    ///   v3-small (version 1, layoutVer 3, 1650 regs, 3364 B)
    ///   v3-full  (version 1, layoutVer 3, 8404 regs, 16872 B)
    ///   v4-full  (version 1, layoutVer 4, 8404 regs, 16872 B)
    /// Migration procedure (byte-preserving for all four):
    ///   expand to TotalFileBytes; set layoutVer=5 + regCount=10001; PRESERVE
    ///   the old body verbatim; zero the new tail.
    ///   v2→v5-full: preserve body 64..1363; zero new body 1364..20065; ALSO
    ///     zero former wits header fields 49..63 (reserved-zero law).
    ///   v3-small→v5-full: preserve body 64..3363; zero 3364..20065.
    ///   v3-full/v4-full→v5-full: preserve body 64..16871; zero 16872..20065.
    ///     Header 49..63 need NO re-zeroing — reserved-zero in every v3/v4-full
    ///     file by induction (v2-upgrade zeroing ∨ all-zero fresh create); the
    ///     expansion must NOT touch them.
    /// After migration every layoutVer-5 file on disk is exactly 10001/20066.
    /// INC-03 ordered-restart law: the MMF section capacity is frozen at
    /// creation until ALL handles close — the expansion must run while the
    /// upgrader is the FIRST/ONLY opener; a live small-capacity section held by
    /// another process blocks the sized view ⇒ the opener must fail fast with
    /// the typed section-capacity access-denied error carrying the
    /// ordered-restart runbook (stop consumers, then producer; start
    /// producer/upgrader FIRST, then consumers).
    ///
    /// Any other shape (incl. every layoutVer=5 shape — final, open as-is — and
    /// the DEAD v1 600/1264) ⇒ FALSE ⇒ caller fail-safe.
    /// </summary>
    public static bool IsUpgradeable(ushort version, ushort layoutVer, ushort regCount, long fileLength) =>
        version == Version &&
        ((layoutVer == LayoutVersionV2 && regCount == RegCountV2 && fileLength == TotalFileBytesV2) ||
         (layoutVer == LayoutVersionV3 && regCount == RegCountV3Small && fileLength == TotalFileBytesV3Small) ||
         ((layoutVer == LayoutVersionV3 || layoutVer == LayoutVersionV4) && regCount == RegCountV4Full && fileLength == TotalFileBytesV4Full));

    // ── Names (contract, not config) ────────────────────────────────────────
    /// <summary>Named mutex guarding bus access (TZ_ARCH_V2 §4 atomicity); opened as
    /// Global\iCemDataMonitorBusMutex_v1 by both sides.</summary>
    public const string MutexName = "iCemDataMonitorBusMutex_v1";

    /// <summary>Session-local mapping name — documented variant only; the deployed
    /// AddIn writes Global\iCemDataMonitorBus_v1 and IniOptions.MmfName defaults to it
    /// (risk R3 retired).</summary>
    public const string BusName = "iCemDataMonitorBus_v1";

    /// <summary>PRIMARY mapping name — the deployed AddIn writes Global\iCemDataMonitorBus_v1
    /// and IniOptions.MmfName defaults to it; the session-local variant is
    /// documented only (risk R3 retired).</summary>
    public const string GlobalBusName = @"Global\iCemDataMonitorBus_v1";

    // ── Header field offsets (byte-exact, little-endian) ────────────────────
    /// <summary>0..3 — magic 'STNB' (4 ASCII bytes).</summary>
    public const int HeaderMagicOffset = 0;
    /// <summary>4..5 — version u16 LE (1).</summary>
    public const int HeaderVersionOffset = 4;
    /// <summary>6..7 — layoutVer u16 LE (5).</summary>
    public const int HeaderLayoutVersionOffset = 6;
    /// <summary>8..9 — regCount u16 LE (10001 current default; RUNTIME DATA ∈ [650..65535], dynamic-regCount law).</summary>
    public const int HeaderRegCountOffset = 8;

    /// <summary>Per-zone header block stride: (lastWriteTick u64, activeFlag u8) = 9 B; zone order = ZoneKind order.</summary>
    public const int ZoneStride = 9;

    /// <summary>10..17 — com_stream lastWriteTick u64 LE (DateTime.UtcNow.Ticks at last write — age computable across reboot).</summary>
    public const int ZoneComLastWriteTickOffset = 10;
    /// <summary>18 — com_stream activeFlag u8 (1 = source active, zone rewritten each second; 0 = hold, TZ A4/A5).</summary>
    public const int ZoneComActiveFlagOffset = 18;
    /// <summary>19..26 — lan_stream_crio lastWriteTick u64 LE (D4: the former Lan block governs lan_stream_crio — writer = AddIn; v5 has no POC zone writer).</summary>
    public const int ZoneLanLastWriteTickOffset = 19;
    /// <summary>27 — lan_stream_crio activeFlag u8.</summary>
    public const int ZoneLanActiveFlagOffset = 27;
    /// <summary>28..35 — raw_com_line lastWriteTick u64 LE.</summary>
    public const int ZoneRawLastWriteTickOffset = 28;
    /// <summary>36 — raw_com_line activeFlag u8.</summary>
    public const int ZoneRawActiveFlagOffset = 36;

    // ── Producer counters (diagnostic, W1-pinned semantics) ─────────────────
    // One wrapping u32 per zone: incremented by the zone's writer on each
    // float-pair write. Liveness/diagnostic aid only — readers MUST NOT rely
    // on exact values (wrapping, optional increment). See CONTRACT_BusV2.yaml §2.
    /// <summary>37..40 — com_stream producer write counter u32 LE (AddIn).</summary>
    public const int ProducerComWriteCountOffset = 37;
    /// <summary>41..44 — lan_stream_crio producer write counter u32 LE (AddIn).</summary>
    public const int ProducerLanWriteCountOffset = 41;
    /// <summary>45..48 — raw_com_line producer write counter u32 LE (AddIn).</summary>
    public const int ProducerRawWriteCountOffset = 45;

    /// <summary>49..56 — former Wits lastWriteTick u64 LE. RESERVED-ZERO in v3 (wits removed; must remain zero).</summary>
    public const int ZoneWitsLastWriteTickOffset = 49;
    /// <summary>57 — former Wits activeFlag u8. RESERVED-ZERO in v3.</summary>
    public const int ZoneWitsActiveFlagOffset = 57;
    /// <summary>58..61 — former Wits producer write counter u32 LE. RESERVED-ZERO in v3.</summary>
    public const int ProducerWitsWriteCountOffset = 58;
    /// <summary>62..63 — reserved (2 B, must remain zero).</summary>
    public const int HeaderReservedOffset = 62;
    /// <summary>Reserved tail size in bytes.</summary>
    public const int HeaderReservedBytes = 2;

    /// <summary>49 — reserved-zero block start: former wits header block. v3 law: bytes 49..63 MUST remain zero.</summary>
    public const int WitsHeaderReservedFrom = 49;
    /// <summary>Reserved-zero bytes of the former wits block (49..61 = 13 B).</summary>
    public const int WitsHeaderReservedBytes = 13;

    /// <summary>64 — body start: headerRegCount × u16, byte-exact Modbus-wire copy
    /// (big-endian bytes per word). Current-default dictionary = 10001 regs.</summary>
    public const int BodyOffset = 64;

    // ── Header STATE register map v5 (body words 0..3 = file offsets 64..71) ─
    // 4 × u16, big-endian bytes on the wire/file (body word law). STATE LAW (v5,
    // user decree 2026-08-17): the header block is CURRENT state only — 4 LIVE
    // regs {0,1,2,3}, ALL AddIn-owned; POC writes NOTHING in the header.
    //   reg 0 comPortActive — D1 multi-device law: first-valid-wins latch; the
    //       dll locks onto the FIRST COM device delivering parseable telemetry,
    //       others discarded; re-arbitrate only on stream loss. 0 = none.
    //   reg 1 caseState — iCem case state; ONLY values actually exposed by the
    //       iCem tap API; invented states forbidden; unknown/unavailable = 0;
    //       enum filled by the dll author (values = tap capability).
    //   reg 2 caseLanState — LAN/cRIO state; same rule: tap-exposed only;
    //       unknown = 0.
    //   reg 3 heartbeat — PARADIGM CHANGE (v5): +1/s ALWAYS while the AddIn is
    //       loaded (iCem running) = pipeline-alive signal (iCem up ∧ POC reading
    //       ∧ Modbus publishing); NO LONGER tied to Streaming state. D2: wraps
    //       32000→0. Frozen ⇔ iCem/AddIn dead.
    // STALE-HOLD LAW (v5): NO writer zeroes these (or any) registers on channel
    // loss/stop — last values persist.
    //
    // FROZEN HISTORY (v4): the 16-reg block {0,1,2,5,8,9,10 live} is REPLACED
    // wholesale by v5. Regs 4..15 are no longer a state block — they are the
    // first com_stream float pairs (addr=4+idx*2); the v4 skip rule is GONE.
    /// <summary>reg 0 — comPortActive u16: N of COMN currently feeding; 0=none. D1 first-valid-wins latch. Writer: AddIn.</summary>
    public const int RegComPort = 0;
    /// <summary>reg 1 — caseState u16: iCem case state; tap-exposed values ONLY; unknown=0. Writer: AddIn.</summary>
    public const int RegCaseState = 1;
    /// <summary>reg 2 — caseLanState u16: LAN/cRIO state; tap-exposed values ONLY; unknown=0. Writer: AddIn.</summary>
    public const int RegCaseLanState = 2;
    /// <summary>reg 3 — heartbeat u16 (+1/s ALWAYS while AddIn loaded; wraps 32000→0, D2). MOVED from v4 reg 5. Writer: AddIn (pipeline-alive signal, NOT Streaming-gated).</summary>
    public const int RegHeartbeat = 3;

    /// <summary>Number of header state registers (0..3). The Modbus publisher mirrors words 0..3 (auto-follows the const).</summary>
    public const int HeaderRegCount = 4;

    /// <summary>Zone-map floor: data zones start at reg 4 (com_stream 4..199).
    /// v5: the header block 0..3 does NOT overlap com_stream — the v4
    /// skip-6-pairs rule is GONE; first com pair = reg 4. The ini validator
    /// keeps the floor rule (zone base ≥ 4).</summary>
    public const int ZoneBaseMin = 4;

    // ── v5 ZONE BOUNDS (bus register map v5 — user decree 2026-08-17) ───────
    // Ownership: com_stream / lan_stream_crio / raw_com_line = AddIn (v5 has
    // NO POC writer); ace_full reserve 600..10000 = DEFERRED, nobody writes
    // (exhaustive ACE dump layout designed LATER from measured ACE composition
    // — documentation-only future work). The whole 600..10000 range is
    // reserved in the schema; no writer may claim it until the layoutVer bump.
    /// <summary>com_stream zone start: reg 4 (98 CDAB pairs, addr=4+idx*2 — v5.1
    /// CHANGE 1: idx0 = UnixTimestampSeconds int32 RAW BITS in the CDAB word
    /// order, float-read = garbage BY DECREE, consumer owns read formatting;
    /// idx1 'Unit Num' STAYS float32).</summary>
    public const int ComStreamBaseReg = 4;
    /// <summary>com_stream zone length in registers: 196.</summary>
    public const int ComStreamRegCount = 196;
    /// <summary>lan_stream_crio zone start: reg 200 (100 float32 CDAB pairs, addr=200+idx*2; 96 cRIO signals per Wireshark/PROTOCOL.md curated view).</summary>
    public const int LanStreamCrioBaseReg = 200;
    /// <summary>lan_stream_crio zone length in registers: 200.</summary>
    public const int LanStreamCrioRegCount = 200;
    /// <summary>raw_com_line zone start: reg 400 (v5.1 CHANGE 2: float32 CDAB
    /// positional CSV fields — field i → 400+i*2, ≤100 fields, positional over
    /// the raw line AS-IS incl. excludeFromTelemetry fields; unparseable field
    /// ⇒ pair not written, stale-hold).</summary>
    public const int RawComLineBaseReg = 400;
    /// <summary>raw_com_line zone length in registers: 200.</summary>
    public const int RawComLineRegCount = 200;
    /// <summary>ace_full RESERVE start: reg 600. DEFERRED (user decree 2026-08-17) — the
    /// exhaustive ACE dump layout is designed LATER from measured ACE composition;
    /// nobody writes 600..10000 in v5.</summary>
    public const int AceFullBaseReg = 600;
    /// <summary>ace_full RESERVE end (inclusive): reg 10000 (user decree 2026-08-17 —
    /// reserve up to 10k). The whole 600..10000 range is reserved; no writer now.</summary>
    public const int AceFullEndReg = 10000;
    /// <summary>ace_full reserve length in registers: 9401 (600..10000).</summary>
    public const int AceFullRegCount = 9401;

    // ── FROZEN-HISTORY v4 state consts (compile-compat for Transport/Cli test
    //    projects; their v5 migration removes them. DO NOT use in new code.) ─
    /// <summary>FROZEN v4: reg 1 WAS comState bitmask — reg 1 IS RegCaseState in v5.</summary>
    public const int RegComState = 1;
    /// <summary>FROZEN v4: reg 2 WAS rawFeed state — reg 2 IS RegCaseLanState in v5.</summary>
    public const int RegRawFeedState = 2;
    /// <summary>FROZEN v4: reg 6 WAS reserved — now com_stream pair idx 1 (regs 6..7).</summary>
    public const int RegReserved6 = 6;
    /// <summary>FROZEN v4: reg 7 WAS reserved — now com_stream pair idx 1 (regs 6..7).</summary>
    public const int RegReserved7 = 7;
    /// <summary>FROZEN v4: reg 8 WAS lanStatus — now com_stream pair idx 2 (regs 8..9).</summary>
    public const int RegLanStatus = 8;
    /// <summary>FROZEN v4: reg 9 WAS lanConnected — now com_stream pair idx 2 (regs 8..9).</summary>
    public const int RegLanConnected = 9;
    /// <summary>FROZEN v4: reg 10 WAS lanRecording — now com_stream pair idx 3 (regs 10..11).</summary>
    public const int RegLanRecording = 10;

    // ── State register VALUES (v5) ───────────────────────────────────────────
    // reg 1 caseState / reg 2 caseLanState enums are filled by the dll author
    // from the iCem tap API capability — NO constants here (invented states
    // forbidden; unknown/unavailable = 0).
    /// <summary>D2 heartbeat wrap modulus: heartbeat wraps 32000→0 (v5 paradigm).</summary>
    public const ushort HeartbeatWrap = 32000;

    // ── FROZEN-HISTORY v4 values (compile-compat; reg 1 is caseState in v5) ──
    /// <summary>FROZEN v4: reg 1 bit: port selected.</summary>
    public const short ComStateSelected = 1;
    /// <summary>FROZEN v4: reg 1 bit: stream active.</summary>
    public const short ComStateStreaming = 2;
    /// <summary>FROZEN v4: reg 1 bit: recording on.</summary>
    public const short ComStateRecording = 4;
    /// <summary>FROZEN v4: reg 1 bit: connection lost.</summary>
    public const short ComStateLostConnection = 8;
    /// <summary>FROZEN v4: reg 1 bit: pause in progress.</summary>
    public const short ComStatePausing = 16;
    /// <summary>FROZEN v4: reg 2 value: raw feed idle.</summary>
    public const short RawFeedIdle = 0;
    /// <summary>FROZEN v4: reg 2 value: raw flowing.</summary>
    public const short RawFeedFlowing = 1;
    /// <summary>FROZEN v4: reg 2 value: streaming but silent this tick.</summary>
    public const short RawFeedSilent = 2;
    /// <summary>FROZEN v4: reg 8 value: vendor LAN inactive.</summary>
    public const short LanStatusInactive = 0;
    /// <summary>FROZEN v4: reg 8 value: vendor LAN activity detected.</summary>
    public const short LanStatusActivity = 2;
    /// <summary>FROZEN v4: reg 8 value: vendor LAN recording enabled.</summary>
    public const short LanStatusRecording = 3;

    // ── WITS zone (absorbed into ace_full reserve — layout v5) ───────────────
    // WITS was REMOVED (2026-08-14) and 600..649 was RESERVED-DEAD in v3/v4.
    // v5: 600..10000 = ace_full RESERVE (DEFERRED — nobody writes; the
    // exhaustive dump layout is designed later from measured ACE composition).
    // The former v2 slot addresses (600/602/604/606/608) live ONLY in
    // specs\contracts\CONTRACT_BusV2.yaml §3 (history).

    // ── Offset helpers (zone-indexed) ───────────────────────────────────────
    /// <summary>Header byte offset of lastWriteTick for a zone.
    /// v5: the Lan liveness block (tick@19..26) governs lan_stream_crio
    /// (writer = AddIn — D4); LanExt aliases it for v4 compile-compat until the
    /// Plugins/Transport agents rewire. Wits returns the former offset
    /// (reserved-zero in v3): reads yield 0 ⇒ the reserved zone reads as
    /// never-written/inactive.</summary>
    public static int ZoneLastWriteTickOffset(ZoneKind kind) => kind switch
    {
        ZoneKind.ComMain => ZoneComLastWriteTickOffset,
        ZoneKind.Lan => ZoneLanLastWriteTickOffset,
        ZoneKind.LanExt => ZoneLanLastWriteTickOffset,
        ZoneKind.RawReserve => ZoneRawLastWriteTickOffset,
        ZoneKind.Wits => ZoneWitsLastWriteTickOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown ZoneKind"),
    };

    /// <summary>Header byte offset of activeFlag for a zone.
    /// v5: the Lan liveness block (flag@27) governs lan_stream_crio (D4);
    /// LanExt aliases it for v4 compile-compat until the rewire.</summary>
    public static int ZoneActiveFlagOffset(ZoneKind kind) => kind switch
    {
        ZoneKind.ComMain => ZoneComActiveFlagOffset,
        ZoneKind.Lan => ZoneLanActiveFlagOffset,
        ZoneKind.LanExt => ZoneLanActiveFlagOffset,
        ZoneKind.RawReserve => ZoneRawActiveFlagOffset,
        ZoneKind.Wits => ZoneWitsActiveFlagOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown ZoneKind"),
    };

    /// <summary>Header byte offset of the producer write counter for a zone.
    /// v5: the Lan counter (41..44) belongs to lan_stream_crio (D4); LanExt
    /// aliases it for v4 compile-compat until the rewire. Wits returns the
    /// former (reserved-zero) offset.</summary>
    public static int ZoneWriteCountOffset(ZoneKind kind) => kind switch
    {
        ZoneKind.ComMain => ProducerComWriteCountOffset,
        ZoneKind.Lan => ProducerLanWriteCountOffset,
        ZoneKind.LanExt => ProducerLanWriteCountOffset,
        ZoneKind.RawReserve => ProducerRawWriteCountOffset,
        ZoneKind.Wits => ProducerWitsWriteCountOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown ZoneKind"),
    };
}

/// <summary>
/// Bus zone identity (TZ_ARCH_V2 §3). Byte underlying = header zone order.
/// net48 AddIn re-declares: 0=ComMain, 1=Lan, 2=RawReserve, 3=Wits, 4=LanExt
/// (append-only order — NEVER renumber existing values).
///
/// V5 TRANSITION (2026-08-17): the v5 register map (BusLayout v5 zone consts)
/// is authoritative — com_stream 4..199, lan_stream_crio 200..399, raw_com_line
/// 400..599 (ALL AddIn-owned), ace_full reserve 600..10000 (DEFERRED — nobody
/// writes). This enum and ZoneDescriptor still carry v4 geometry. Rewire status:
/// the publisher is DONE (iCemDataMonitor.Transport ModbusTcpPublisher uses the v5 zone
/// consts); the registry rewire is still PENDING — iCemDataMonitor.Plugins.Icem
/// UnifiedVariableRegistry maps through these v4 rows. DO NOT renumber these
/// enum values (append-only law).
/// </summary>
public enum ZoneKind : byte
{
    /// <summary>v4: Main COM zone — POLL-ONLY full stream (recording-gated). v5: com_stream 4..199 (BusLayout.ComStreamBaseReg).</summary>
    ComMain = 0,
    /// <summary>v4: LAN/ACE zone (AceSnifferService). v5: this liveness block governs lan_stream_crio 200..399 (BusLayout.LanStreamCrioBaseReg — D4, AddIn writer).</summary>
    Lan = 1,
    /// <summary>v4: Raw COM preview reserve zone. v5: raw_com_line 400..599 (BusLayout.RawComLineBaseReg; float32 CDAB positional fields of the last CSV line).</summary>
    RawReserve = 2,
    /// <summary>v4: RESERVED zone (wits removed in layout v3). v5: absorbed into ace_full reserve 600..10000 — no reserved wits zone exists.</summary>
    Wits = 3,
    /// <summary>v4: LAN extension zone (lan_ext): 650..8403 — full ACE numeric dictionary extension (3877 floats). v5: absorbed into ace_full reserve 600..10000.</summary>
    LanExt = 4,
}

/// <summary>
/// Zone geometry: register range [BaseReg, BaseReg + RegCount) and float32
/// capacity (RegCount / 2, CDAB pairs). Immutable value type.
/// Defaults are TZ_ARCH_V2 §3 bounds; runtime bounds come from station.ini
/// (zone bounds = ini DATA, not code — §9 evolution).
///
/// V5 TRANSITION: these rows are v4 geometry (frozen). Rewire status: the
/// publisher is DONE (iCemDataMonitor.Transport ModbusTcpPublisher uses the v5 zone
/// consts); the registry rewire in iCemDataMonitor.Plugins.Icem (UnifiedVariableRegistry)
/// is still PENDING. The v5 contract bounds live in the BusLayout v5 zone consts
/// (ComStreamBaseReg/LanStreamCrioBaseReg/RawComLineBaseReg/AceFullBaseReg/
/// AceFullEndReg). DO NOT add v5-specific code against
/// these v4 rows — that transition is the plugin/transport agents' change.
/// </summary>
public readonly record struct ZoneDescriptor(ZoneKind Kind, int BaseReg, int RegCount, int FloatCapacity)
{
    /// <summary>Spec default: com_main 4..199 (196 regs = 98 floats).</summary>
    public static readonly ZoneDescriptor ComMain = new(ZoneKind.ComMain, 4, 196, 98);
    /// <summary>Spec default: lan 200..449 (250 regs = 125 floats).</summary>
    public static readonly ZoneDescriptor Lan = new(ZoneKind.Lan, 200, 250, 125);
    /// <summary>Spec default: raw_reserve 450..599 (150 regs = 75 floats).</summary>
    public static readonly ZoneDescriptor RawReserve = new(ZoneKind.RawReserve, 450, 150, 75);
    /// <summary>Spec default: wits 600..649 (50 regs = 25 floats). RESERVED in layout v3 — never written by any writer; external A5 writes persist.</summary>
    public static readonly ZoneDescriptor Wits = new(ZoneKind.Wits, 600, 50, 25);
    /// <summary>Spec default: lan_ext 650..8403 (7754 regs = 3877 floats) — full ACE numeric dictionary extension (3973 numeric − 96 legacy = 3877; 413 text UC/HX excluded by design).</summary>
    public static readonly ZoneDescriptor LanExt = new(ZoneKind.LanExt, 650, 7754, 3877);

    /// <summary>Last register of the zone (inclusive).</summary>
    public int EndReg => BaseReg + RegCount - 1;

    /// <summary>Spec-default descriptor for a zone kind.</summary>
    public static ZoneDescriptor DefaultFor(ZoneKind kind) => kind switch
    {
        ZoneKind.ComMain => ComMain,
        ZoneKind.Lan => Lan,
        ZoneKind.RawReserve => RawReserve,
        ZoneKind.Wits => Wits,
        ZoneKind.LanExt => LanExt,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown ZoneKind"),
    };
}

/// <summary>
/// float32 ⇄ CDAB register pair codec. Pinned against the production encoding in
/// iCemDataMonitor.Transport\ModbusTcpPublisher.cs (reg[N] = low word, reg[N+1] = high word).
///
/// Word order: CDAB — reg[N] carries bytes C,D (low word), reg[N+1] carries bytes
/// A,B (high word) of the IEEE-754 bit pattern. Bytes INSIDE each register word are
/// big-endian on the wire/file (see file header endianness law).
///
/// Pins: 1.0f → (0x0000, 0x3F80); 15.79f → (0xA3D7, 0x417C). Zero-allocation.
///
/// net48 re-declaration (BitConverter.SingleToUInt32Bits is netstandard2.1+):
///   Split:  var b = BitConverter.GetBytes(value);            // LE on all Windows targets
///           uint bits = BitConverter.ToUInt32(b, 0);
///           low = (ushort)(bits & 0xFFFF); high = (ushort)(bits >> 16);
///   Combine: float f = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
/// </summary>
public static class CdabCodec
{
    /// <summary>Split float into (Low, High) CDAB register words.</summary>
    /// <param name="value">float32 input.</param>
    /// <returns>Low = reg[N] value, High = reg[N+1] value.</returns>
    public static (ushort Low, ushort High) Split(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        return ((ushort)(bits & 0xFFFF), (ushort)(bits >> 16));
    }

    /// <summary>Recombine CDAB register words into float32.</summary>
    /// <param name="low">reg[N] value (bytes C,D).</param>
    /// <param name="high">reg[N+1] value (bytes A,B).</param>
    public static float Combine(ushort low, ushort high)
    {
        uint bits = (uint)low | ((uint)high << 16);
        return BitConverter.UInt32BitsToSingle(bits);
    }
}
