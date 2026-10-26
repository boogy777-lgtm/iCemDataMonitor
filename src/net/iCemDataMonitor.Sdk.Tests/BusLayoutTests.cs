using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Sdk.Tests;

// W1 contract tests: layout geometry, zone bounds, header offsets, magic/version,
// dynamic-regCount acceptance law and expansion paths.
// Pins against TZ_ARCH_V2 §3/§4 and CONTRACT_BusV2.yaml (v1.3.0, layout v3).
public class BusLayoutTests
{
    [Fact]
    public void Layout_Geometry_Matches_Spec()
    {
        Assert.Equal(10001, BusLayout.RegCount);
        Assert.Equal(64, BusLayout.HeaderBytes);
        Assert.Equal(20002, BusLayout.BodyBytes);
        Assert.Equal(20066, BusLayout.TotalFileBytes);
        Assert.Equal(2, BusLayout.BytesPerReg);
        Assert.Equal(BusLayout.RegCount * BusLayout.BytesPerReg, BusLayout.BodyBytes);
        Assert.Equal(BusLayout.HeaderBytes + BusLayout.BodyBytes, BusLayout.TotalFileBytes);
    }

    [Fact]
    public void DynamicLaw_Bound_Consts_Are_Pinned()
    {
        Assert.Equal(650, BusLayout.RegCountMin);
        Assert.Equal(65535, BusLayout.RegCountMax);
        // The full default dictionary is inside the law's range.
        Assert.True(BusLayout.RegCount >= BusLayout.RegCountMin && BusLayout.RegCount <= BusLayout.RegCountMax);
    }

    [Fact]
    public void ZoneDefaults_Bounds_And_Capacities_Match_Spec()
    {
        // com_main 4..199 (196 regs = 98 floats)
        Assert.Equal(4, ZoneDescriptor.ComMain.BaseReg);
        Assert.Equal(196, ZoneDescriptor.ComMain.RegCount);
        Assert.Equal(199, ZoneDescriptor.ComMain.EndReg);
        Assert.Equal(98, ZoneDescriptor.ComMain.FloatCapacity);
        Assert.Equal(ZoneKind.ComMain, ZoneDescriptor.ComMain.Kind);

        // lan 200..449 (250 regs = 125 floats)
        Assert.Equal(200, ZoneDescriptor.Lan.BaseReg);
        Assert.Equal(250, ZoneDescriptor.Lan.RegCount);
        Assert.Equal(449, ZoneDescriptor.Lan.EndReg);
        Assert.Equal(125, ZoneDescriptor.Lan.FloatCapacity);
        Assert.Equal(ZoneKind.Lan, ZoneDescriptor.Lan.Kind);

        // raw_reserve 450..599 (150 regs = 75 floats) — stays EXACTLY in place (operator directive)
        Assert.Equal(450, ZoneDescriptor.RawReserve.BaseReg);
        Assert.Equal(150, ZoneDescriptor.RawReserve.RegCount);
        Assert.Equal(599, ZoneDescriptor.RawReserve.EndReg);
        Assert.Equal(75, ZoneDescriptor.RawReserve.FloatCapacity);
        Assert.Equal(ZoneKind.RawReserve, ZoneDescriptor.RawReserve.Kind);

        // wits 600..649 (50 regs) — RESERVED in layout v3 (wits removed)
        Assert.Equal(600, ZoneDescriptor.Wits.BaseReg);
        Assert.Equal(50, ZoneDescriptor.Wits.RegCount);
        Assert.Equal(649, ZoneDescriptor.Wits.EndReg);
        Assert.Equal(25, ZoneDescriptor.Wits.FloatCapacity);
        Assert.Equal(ZoneKind.Wits, ZoneDescriptor.Wits.Kind);

        // lan_ext 650..8403 (7754 regs = 3877 floats) — FULL ACE numeric dictionary extension
        Assert.Equal(650, ZoneDescriptor.LanExt.BaseReg);
        Assert.Equal(7754, ZoneDescriptor.LanExt.RegCount);
        Assert.Equal(8403, ZoneDescriptor.LanExt.EndReg);
        Assert.Equal(3877, ZoneDescriptor.LanExt.FloatCapacity);
        Assert.Equal(ZoneKind.LanExt, ZoneDescriptor.LanExt.Kind);
    }

    [Fact]
    public void FullDictionary_Capacity_Note_Is_Pinned()
    {
        // 3973 numeric = 96 legacy (lan) + 3877 extension (lan_ext); 413 text UC/HX excluded.
        Assert.Equal(96 + 3877, 3973);
        Assert.True(ZoneDescriptor.Lan.FloatCapacity >= 96, "lan zone must hold the 96 legacy-mapped floats");
        Assert.True(ZoneDescriptor.Lan.FloatCapacity + ZoneDescriptor.LanExt.FloatCapacity >= 3973,
                    "lan + lan_ext must cover the full numeric dictionary");
    }

    [Fact]
    public void ZoneDefaults_Cover_Exactly_Regs_4_To_8403_Without_Overlap()
    {
        // v4-frozen ZoneDescriptor geometry (Plugins agent rewires these rows to
        // the v5 map); the descriptors tile 4..8403 contiguously. The v5
        // dictionary extends to 10000 — the ace_full reserve 600..10000 covers
        // everything beyond the descriptor tile.
        var zones = new[]
        {
            ZoneDescriptor.ComMain,
            ZoneDescriptor.Lan,
            ZoneDescriptor.RawReserve,
            ZoneDescriptor.Wits,
            ZoneDescriptor.LanExt,
        };

        // Zone-map floor (4) + zones tile 4..8403 exactly.
        Assert.Equal(BusLayout.ZoneBaseMin, zones[0].BaseReg);
        for (int i = 1; i < zones.Length; i++)
        {
            Assert.Equal(zones[i - 1].EndReg + 1, zones[i].BaseReg); // contiguous, no gap
        }
        Assert.Equal(8403, zones[^1].EndReg);

        // Pairwise no overlap (half-open ranges).
        for (int i = 0; i < zones.Length; i++)
        {
            for (int j = i + 1; j < zones.Length; j++)
            {
                long aEnd = (long)zones[i].BaseReg + zones[i].RegCount;
                long bEnd = (long)zones[j].BaseReg + zones[j].RegCount;
                Assert.False(zones[i].BaseReg < bEnd && zones[j].BaseReg < aEnd,
                    $"overlap: zone {zones[i].Kind} vs {zones[j].Kind}");
            }
        }

        // Float capacity = reg count / 2 (CDAB pairs).
        foreach (var z in zones)
            Assert.Equal(z.RegCount / 2, z.FloatCapacity);
    }

    [Fact]
    public void DefaultFor_Returns_Spec_Defaults_Per_Kind()
    {
        Assert.Equal(ZoneDescriptor.ComMain, ZoneDescriptor.DefaultFor(ZoneKind.ComMain));
        Assert.Equal(ZoneDescriptor.Lan, ZoneDescriptor.DefaultFor(ZoneKind.Lan));
        Assert.Equal(ZoneDescriptor.RawReserve, ZoneDescriptor.DefaultFor(ZoneKind.RawReserve));
        Assert.Equal(ZoneDescriptor.Wits, ZoneDescriptor.DefaultFor(ZoneKind.Wits));
        Assert.Equal(ZoneDescriptor.LanExt, ZoneDescriptor.DefaultFor(ZoneKind.LanExt));
    }

    [Fact]
    public void ZoneKind_Values_Are_Byte_Ordered_0_1_2_3_4_AppendOnly()
    {
        Assert.Equal((byte)0, (byte)ZoneKind.ComMain);
        Assert.Equal((byte)1, (byte)ZoneKind.Lan);
        Assert.Equal((byte)2, (byte)ZoneKind.RawReserve);
        Assert.Equal((byte)3, (byte)ZoneKind.Wits);
        Assert.Equal((byte)4, (byte)ZoneKind.LanExt); // APPENDED — existing 0..3 not reordered
        // Zone block order in header ≡ ZoneKind byte value (CONTRACT_BusV2 §2 zone_order_axiom).
        Assert.Equal(0, (byte)ZoneDescriptor.ComMain.Kind);
        Assert.Equal(1, (byte)ZoneDescriptor.Lan.Kind);
        Assert.Equal(2, (byte)ZoneDescriptor.RawReserve.Kind);
        Assert.Equal(3, (byte)ZoneDescriptor.Wits.Kind);
        Assert.Equal(4, (byte)ZoneDescriptor.LanExt.Kind);
    }

    [Fact]
    public void HeaderOffsets_Are_Byte_Exact_Per_Spec()
    {
        Assert.Equal(0, BusLayout.HeaderMagicOffset);
        Assert.Equal(4, BusLayout.HeaderVersionOffset);
        Assert.Equal(6, BusLayout.HeaderLayoutVersionOffset);
        Assert.Equal(8, BusLayout.HeaderRegCountOffset);

        Assert.Equal(10, BusLayout.ZoneComLastWriteTickOffset);
        Assert.Equal(18, BusLayout.ZoneComActiveFlagOffset);
        Assert.Equal(19, BusLayout.ZoneLanLastWriteTickOffset);
        Assert.Equal(27, BusLayout.ZoneLanActiveFlagOffset);
        Assert.Equal(28, BusLayout.ZoneRawLastWriteTickOffset);
        Assert.Equal(36, BusLayout.ZoneRawActiveFlagOffset);

        Assert.Equal(37, BusLayout.ProducerComWriteCountOffset);
        Assert.Equal(41, BusLayout.ProducerLanWriteCountOffset);
        Assert.Equal(45, BusLayout.ProducerRawWriteCountOffset);

        // Former wits block (49/57/58) — reserved-zero in v3, offsets frozen.
        Assert.Equal(49, BusLayout.ZoneWitsLastWriteTickOffset);
        Assert.Equal(57, BusLayout.ZoneWitsActiveFlagOffset);
        Assert.Equal(58, BusLayout.ProducerWitsWriteCountOffset);

        Assert.Equal(62, BusLayout.HeaderReservedOffset);
        Assert.Equal(2, BusLayout.HeaderReservedBytes);
        Assert.Equal(BusLayout.HeaderBytes, BusLayout.HeaderReservedOffset + BusLayout.HeaderReservedBytes);

        Assert.Equal(64, BusLayout.BodyOffset);
    }

    [Fact]
    public void ReservedWits_HeaderBlock_Is_49_To_63()
    {
        // v3 law: former wits block (49..61) + reserved tail (62..63) = 15 zero bytes.
        Assert.Equal(49, BusLayout.WitsHeaderReservedFrom);
        Assert.Equal(13, BusLayout.WitsHeaderReservedBytes);
        Assert.Equal(BusLayout.HeaderReservedOffset,
                     BusLayout.WitsHeaderReservedFrom + BusLayout.WitsHeaderReservedBytes);
        Assert.Equal(BusLayout.HeaderBytes,
                     BusLayout.WitsHeaderReservedFrom + BusLayout.WitsHeaderReservedBytes + BusLayout.HeaderReservedBytes);
    }

    [Fact]
    public void ZoneIndexed_OffsetHelpers_Match_Consts()
    {
        Assert.Equal(BusLayout.ZoneComLastWriteTickOffset, BusLayout.ZoneLastWriteTickOffset(ZoneKind.ComMain));
        Assert.Equal(BusLayout.ZoneComActiveFlagOffset, BusLayout.ZoneActiveFlagOffset(ZoneKind.ComMain));
        Assert.Equal(BusLayout.ZoneLanLastWriteTickOffset, BusLayout.ZoneLastWriteTickOffset(ZoneKind.Lan));
        Assert.Equal(BusLayout.ZoneLanActiveFlagOffset, BusLayout.ZoneActiveFlagOffset(ZoneKind.Lan));
        Assert.Equal(BusLayout.ZoneRawLastWriteTickOffset, BusLayout.ZoneLastWriteTickOffset(ZoneKind.RawReserve));
        Assert.Equal(BusLayout.ZoneRawActiveFlagOffset, BusLayout.ZoneActiveFlagOffset(ZoneKind.RawReserve));
        Assert.Equal(BusLayout.ZoneWitsLastWriteTickOffset, BusLayout.ZoneLastWriteTickOffset(ZoneKind.Wits));
        Assert.Equal(BusLayout.ZoneWitsActiveFlagOffset, BusLayout.ZoneActiveFlagOffset(ZoneKind.Wits));
    }

    [Fact]
    public void LanExt_Shares_Lan_Liveness_Block()
    {
        // Shared-liveness law (v3): lan_ext has NO dedicated header fields —
        // tick@19..26, flag@27, counter@41..44 are SHARED with lan.
        Assert.Equal(BusLayout.ZoneLanLastWriteTickOffset, BusLayout.ZoneLastWriteTickOffset(ZoneKind.LanExt));
        Assert.Equal(BusLayout.ZoneLanActiveFlagOffset, BusLayout.ZoneActiveFlagOffset(ZoneKind.LanExt));
        Assert.Equal(BusLayout.ProducerLanWriteCountOffset, BusLayout.ZoneWriteCountOffset(ZoneKind.LanExt));
        // Distinct from raw/wits offsets — lan_ext is NOT strided like the first 3 blocks.
        Assert.NotEqual(BusLayout.ZoneRawLastWriteTickOffset, BusLayout.ZoneLastWriteTickOffset(ZoneKind.LanExt));
        Assert.NotEqual(BusLayout.ZoneWitsLastWriteTickOffset, BusLayout.ZoneLastWriteTickOffset(ZoneKind.LanExt));
    }

    [Fact]
    public void ZoneWriteCountOffsets_Match_Spec_Per_Zone()
    {
        Assert.Equal(BusLayout.ProducerComWriteCountOffset, BusLayout.ZoneWriteCountOffset(ZoneKind.ComMain));
        Assert.Equal(BusLayout.ProducerLanWriteCountOffset, BusLayout.ZoneWriteCountOffset(ZoneKind.Lan));
        Assert.Equal(BusLayout.ProducerLanWriteCountOffset, BusLayout.ZoneWriteCountOffset(ZoneKind.LanExt));
        Assert.Equal(BusLayout.ProducerRawWriteCountOffset, BusLayout.ZoneWriteCountOffset(ZoneKind.RawReserve));
        Assert.Equal(BusLayout.ProducerWitsWriteCountOffset, BusLayout.ZoneWriteCountOffset(ZoneKind.Wits));
    }

    [Fact]
    public void ZoneBlocks_Are_Strided_And_Fit_In_Header()
    {
        // Per-zone block = u64 + u8 = 9 bytes; first three blocks end at offset 37 = first counter.
        Assert.Equal(9, BusLayout.ZoneStride);
        Assert.Equal(BusLayout.ZoneComLastWriteTickOffset + BusLayout.ZoneStride, BusLayout.ZoneLanLastWriteTickOffset);
        Assert.Equal(BusLayout.ZoneLanLastWriteTickOffset + BusLayout.ZoneStride, BusLayout.ZoneRawLastWriteTickOffset);
        Assert.Equal(BusLayout.ZoneRawActiveFlagOffset + 1, BusLayout.ProducerComWriteCountOffset);
        Assert.True(BusLayout.ZoneRawActiveFlagOffset < BusLayout.HeaderBytes);

        // Former wits block in the reserved area: tick u64 @49..56, flag u8 @57,
        // writeCount u32 @58..61, reserved 62..63 — all reserved-zero in v3.
        Assert.Equal(BusLayout.ZoneWitsLastWriteTickOffset + 8, BusLayout.ZoneWitsActiveFlagOffset);
        Assert.Equal(BusLayout.ZoneWitsActiveFlagOffset + 1, BusLayout.ProducerWitsWriteCountOffset);
        Assert.Equal(BusLayout.ProducerWitsWriteCountOffset + 4, BusLayout.HeaderReservedOffset);
    }

    [Fact]
    public void HeaderStateRegisters_Are_Pinned_LiveSet_4()
    {
        // v5 state register map: 4 LIVE regs {0,1,2,3}, ALL AddIn-owned.
        Assert.Equal(0, BusLayout.RegComPort);
        Assert.Equal(1, BusLayout.RegCaseState);
        Assert.Equal(2, BusLayout.RegCaseLanState);
        Assert.Equal(3, BusLayout.RegHeartbeat);
        Assert.Equal(4, BusLayout.HeaderRegCount);

        // Zone-map floor == HeaderRegCount (v5: no overlap, com_stream starts at 4).
        Assert.Equal(4, BusLayout.ZoneBaseMin);
        Assert.Equal(BusLayout.HeaderRegCount, BusLayout.ZoneBaseMin);

        // Heartbeat wrap modulus (D2).
        Assert.Equal(32000, BusLayout.HeartbeatWrap);
    }

    [Fact]
    public void HeaderStateRegisterMapV5_MatchesContract()
    {
        // The wire contract the dll agent implements against (Э2 iCemAddIn):
        // reg0 comPortActive (D1 first-valid-wins), reg1 caseState (tap-only),
        // reg2 caseLanState (tap-only), reg3 heartbeat (+1/s always, wrap 32000).
        Assert.Equal(0, BusLayout.RegComPort);
        Assert.Equal(1, BusLayout.RegCaseState);
        Assert.Equal(2, BusLayout.RegCaseLanState);
        Assert.Equal(3, BusLayout.RegHeartbeat);
        Assert.Equal((ushort)32000, BusLayout.HeartbeatWrap);
        Assert.Equal(4, BusLayout.HeaderRegCount);

        // v5 zone bounds per the decree.
        Assert.Equal(4, BusLayout.ComStreamBaseReg);
        Assert.Equal(196, BusLayout.ComStreamRegCount);
        Assert.Equal(200, BusLayout.LanStreamCrioBaseReg);
        Assert.Equal(200, BusLayout.LanStreamCrioRegCount);
        Assert.Equal(400, BusLayout.RawComLineBaseReg);
        Assert.Equal(200, BusLayout.RawComLineRegCount);
        Assert.Equal(600, BusLayout.AceFullBaseReg);
        Assert.Equal(10000, BusLayout.AceFullEndReg);
        Assert.Equal(9401, BusLayout.AceFullRegCount);
    }

    [Fact]
    public void FrozenHistory_OldReg1_And_Reg2_Are_Superseded()
    {
        // v4 reg 1 (comState) ∧ reg 5 (heartbeat) are SUPERSEDED:
        // v5 reg 1 = caseState, reg 3 = heartbeat (moved 5→3).
        Assert.Equal(3, BusLayout.RegHeartbeat);
        Assert.NotEqual(5, BusLayout.RegHeartbeat);
        // FROZEN-HISTORY v4 consts still exist for Transport/Cli compile-compat:
        Assert.Equal(8, BusLayout.RegLanStatus);
        Assert.Equal(1, BusLayout.RegComState);
        Assert.Equal(2, BusLayout.RegRawFeedState);
        // No timeout-derived value consts exist anywhere in the map (state-only law).
        Assert.Equal(0, BusLayout.RawFeedIdle);
    }

    [Fact]
    public void ReservedRegisterConsts_Are_Absent()
    {
        // v4.1 trim (operator review): over-engineered regs 3,4,11..15 have NO consts.
        var type = typeof(BusLayout);
        Assert.Null(type.GetField("RegComFeedState"));
        Assert.Null(type.GetField("RegComPairsLastTick"));
        Assert.Null(type.GetField("RegLanEquipmentCount"));
        Assert.Null(type.GetField("RegSniffFrames"));
        Assert.Null(type.GetField("RegSniffValues"));
        Assert.Null(type.GetField("RegSniffUnknownSv"));
        Assert.Null(type.GetField("RegSniffUnmapped"));
    }

    [Fact]
    public void StateRegister_Values_Are_Pinned()
    {
        // reg 1 COM state bitmask.
        Assert.Equal(1, BusLayout.ComStateSelected);
        Assert.Equal(2, BusLayout.ComStateStreaming);
        Assert.Equal(4, BusLayout.ComStateRecording);
        Assert.Equal(8, BusLayout.ComStateLostConnection);
        Assert.Equal(16, BusLayout.ComStatePausing);
        // reg 2 raw feed states.
        Assert.Equal(0, BusLayout.RawFeedIdle);
        Assert.Equal(1, BusLayout.RawFeedFlowing);
        Assert.Equal(2, BusLayout.RawFeedSilent);
        // reg 8 vendor LAN status values (unchanged semantics).
        Assert.Equal(0, BusLayout.LanStatusInactive);
        Assert.Equal(2, BusLayout.LanStatusActivity);
        Assert.Equal(3, BusLayout.LanStatusRecording);
    }

    [Fact]
    public void Magic_And_Version_Consts_Match_Spec()
    {
        Assert.Equal(0x424E5453u, BusLayout.Magic);
        Assert.Equal(new byte[] { (byte)'S', (byte)'T', (byte)'N', (byte)'B' }, BusLayout.MagicBytes);
        Assert.Equal(4, BusLayout.MagicBytes.Length);
        Assert.Equal(1, BusLayout.Version);
        Assert.Equal(5, BusLayout.LayoutVersion);
        Assert.Equal(10001, BusLayout.RegCount);
    }

    [Fact]
    public void Names_Are_Contract_Pinned()
    {
        Assert.Equal("iCemDataMonitorBusMutex_v1", BusLayout.MutexName);
        Assert.Equal("iCemDataMonitorBus_v1", BusLayout.BusName);
        Assert.Equal(@"Global\iCemDataMonitorBus_v1", BusLayout.GlobalBusName);
    }

    // ── v5 shape law: layoutVer==5 final shapes only (FULL MIGRATION) ───────
    [Fact]
    public void IsValidShape_Accepts_Final_V5_Shapes()
    {
        // Final shapes: layoutVer 5 ONLY — legacy layouts are upgrade sources,
        // never final (FULL MIGRATION, user decision 2026-08-17).
        Assert.True(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 10001, 20066)); // current v5 default
        // Boundary regCounts of the law's range (layoutVer 5).
        Assert.True(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 650, 64 + 650 * 2));
        Assert.True(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 65535, 64L + 65535 * 2));
        // A v5 file at the former 8404 dictionary is a VALID final shape (dynamic
        // regCount range) — but every MIGRATION path lands at exactly 10001.
        Assert.True(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 8404, 16872));
    }

    [Fact]
    public void IsValidShape_Rejects_Legacy_Layouts_As_Final_Shapes()
    {
        // v2/v3-small/v3-full/v4-full are UPGRADE SOURCES (IsUpgradeable), never
        // final shapes — even with in-range regCount and matching length.
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 2, 650, 1364));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 3, 1650, 3364));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 3, 8404, 16872));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 4, 8404, 16872));
        // The law is NOT compositional over layoutVer anymore: an in-range legacy
        // N with matching length is still NOT a final shape.
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 3, 650, 1364));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 4, 1650, 3364));
    }

    [Fact]
    public void IsValidShape_Accepts_Random_Valid_N()
    {
        // Final-shape law: ANY regCount in [650..65535] with matching length is
        // valid at layoutVer 5.
        int[] samples = { 651, 999, 2000, 5000, 8404, 12345, 60000 };
        foreach (var n in samples)
        {
            Assert.True(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, (ushort)n, 64 + (long)n * 2),
                        $"regCount {n} must be a valid v5 shape");
        }
    }

    [Fact]
    public void IsValidShape_Rejects_Bad_Magic_Version_Or_LayoutVer()
    {
        // Bad magic.
        Assert.False(BusLayout.IsValidShape(0x12345678u, 1, 5, 10001, 20066));
        // Wrong bus version.
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 2, 5, 10001, 20066));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 0, 5, 10001, 20066));
        // Legacy layoutVers (2..4) = upgrade-source layouts, never final.
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 2, 650, 1364));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 3, 8404, 16872));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 4, 8404, 16872));
        // layoutVer ∉ {2,3,4,5}: v1 is DEAD, future 6 is unknown.
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 1, 600, 1264));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 6, 8404, 16872));
    }

    [Fact]
    public void IsValidShape_Rejects_RegCount_Out_Of_Range()
    {
        // Below the law's lower bound (649 < 650 — the v1 600-reg era is dead).
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 649, 64 + 649 * 2));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 0, 64));
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 2, 600, 1264)); // dead v1-era shape
    }

    [Fact]
    public void IsValidShape_Rejects_Length_Mismatch()
    {
        // regCount does not match the file length ⇒ invalid regardless of everything else.
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 8404, 16871)); // one byte short
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 8404, 16873)); // one byte long
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 8404, 0));     // empty
        Assert.False(BusLayout.IsValidShape(BusLayout.Magic, 1, 5, 10001, 20065)); // one byte short of v5-full
    }

    // ── expansion paths: v2→v5-full ∧ v3-small→v5-full ∧ v3/v4-full→v5-full ──
    [Fact]
    public void IsUpgradeable_True_For_Exact_V2_Shape()
    {
        // version 1, layoutVer 2, 650 regs, 1364 B file.
        Assert.True(BusLayout.IsUpgradeable(
            version: 1, layoutVer: 2, regCount: 650, fileLength: 1364));
    }

    [Fact]
    public void IsUpgradeable_True_For_Exact_V3Small_Shape()
    {
        // version 1, layoutVer 3, 1650 regs, 3364 B file.
        Assert.True(BusLayout.IsUpgradeable(
            version: 1, layoutVer: 3, regCount: 1650, fileLength: 3364));
    }

    [Fact]
    public void IsUpgradeable_True_For_V3Full_And_V4Full_Shapes()
    {
        // FULL MIGRATION (user decision 2026-08-17): 8404/16872 at layoutVer 3
        // (v3-full) or layoutVer 4 (v4-full) = upgrade sources → expand to
        // 10001/20066 (layoutVer=5, regCount=10001, body preserved, tail zeroed).
        Assert.True(BusLayout.IsUpgradeable(
            version: 1, layoutVer: 3, regCount: 8404, fileLength: 16872));
        Assert.True(BusLayout.IsUpgradeable(
            version: 1, layoutVer: 4, regCount: 8404, fileLength: 16872));
        // Already-full v5 dictionary — not an upgrade candidate (open as-is).
        Assert.False(BusLayout.IsUpgradeable(
            version: 1, layoutVer: 5, regCount: 10001, fileLength: 20066));
        // A v5 file at the former 8404 dictionary is a FINAL shape, not an upgrade source.
        Assert.False(BusLayout.IsUpgradeable(
            version: 1, layoutVer: 5, regCount: 8404, fileLength: 16872));
    }

    [Fact]
    public void IsUpgradeable_False_For_V1_Shape_Dead()
    {
        // v1 (600/1264) is DEAD in v1.3.0 — no upgrade path, fail-safe.
        Assert.False(BusLayout.IsUpgradeable(
            version: 1, layoutVer: 1, regCount: 600, fileLength: 1264));
    }

    [Fact]
    public void IsUpgradeable_False_For_Any_Other_Mismatch()
    {
        // Wrong version.
        Assert.False(BusLayout.IsUpgradeable(2, 2, 650, 1364));
        Assert.False(BusLayout.IsUpgradeable(2, 3, 1650, 3364));
        // Wrong layoutVer for the v2 shape.
        Assert.False(BusLayout.IsUpgradeable(1, 1, 650, 1364));
        Assert.False(BusLayout.IsUpgradeable(1, 3, 650, 1364));
        // Wrong layoutVer for the v3-small shape.
        Assert.False(BusLayout.IsUpgradeable(1, 2, 1650, 3364));
        Assert.False(BusLayout.IsUpgradeable(1, 4, 1650, 3364));
        // Wrong regCount.
        Assert.False(BusLayout.IsUpgradeable(1, 2, 651, 1364));
        Assert.False(BusLayout.IsUpgradeable(1, 3, 1651, 3364));
        Assert.False(BusLayout.IsUpgradeable(1, 3, 8404, 3364));
        Assert.False(BusLayout.IsUpgradeable(1, 4, 8405, 16872));
        Assert.False(BusLayout.IsUpgradeable(1, 3, 8405, 16872));
        // Wrong layoutVer for the 8404 shape.
        Assert.False(BusLayout.IsUpgradeable(1, 2, 8404, 16872));
        Assert.False(BusLayout.IsUpgradeable(1, 5, 8404, 16872));
        // Wrong file length.
        Assert.False(BusLayout.IsUpgradeable(1, 2, 650, 1363));
        Assert.False(BusLayout.IsUpgradeable(1, 3, 1650, 3363));
        Assert.False(BusLayout.IsUpgradeable(1, 3, 1650, 16872));
        Assert.False(BusLayout.IsUpgradeable(1, 4, 8404, 16871));
        Assert.False(BusLayout.IsUpgradeable(1, 4, 8404, 20066));
        // Garbage.
        Assert.False(BusLayout.IsUpgradeable(1, 2, 650, 0));
        Assert.False(BusLayout.IsUpgradeable(0, 3, 1650, 3364));
    }

    [Fact]
    public void Shape_Consts_Pin_Upgrade_Sources()
    {
        // v2 shape — expansion source.
        Assert.Equal(2, BusLayout.LayoutVersionV2);
        Assert.Equal(650, BusLayout.RegCountV2);
        Assert.Equal(1364, BusLayout.TotalFileBytesV2);

        // v3 marker — upgrade-source layout (v3-small ∧ v3-full expand to v5-full).
        Assert.Equal(3, BusLayout.LayoutVersionV3);

        // v4 marker — upgrade-source layout (v4-full expands to v5-full).
        Assert.Equal(4, BusLayout.LayoutVersionV4);

        // v3-small shape — expansion source (delta 1.2.0).
        Assert.Equal(1650, BusLayout.RegCountV3Small);
        Assert.Equal(3364, BusLayout.TotalFileBytesV3Small);

        // v3-full/v4-full shape — expansion source (FULL MIGRATION, 2026-08-17).
        Assert.Equal(8404, BusLayout.RegCountV4Full);
        Assert.Equal(16872, BusLayout.TotalFileBytesV4Full);

        // Full v5 dictionary vs v2: +9351 regs, +18702 B.
        Assert.Equal(BusLayout.RegCount, BusLayout.RegCountV2 + 9351);
        Assert.Equal(BusLayout.TotalFileBytes, BusLayout.TotalFileBytesV2 + 9351 * 2);

        // Full v5 dictionary vs v3-small: +8351 regs, +16702 B.
        Assert.Equal(BusLayout.RegCount, BusLayout.RegCountV3Small + 8351);
        Assert.Equal(BusLayout.TotalFileBytes, BusLayout.TotalFileBytesV3Small + 8351 * 2);

        // Full v5 dictionary vs v3/v4-full: +1597 regs, +3194 B.
        Assert.Equal(BusLayout.RegCount, BusLayout.RegCountV4Full + 1597);
        Assert.Equal(BusLayout.TotalFileBytes, BusLayout.TotalFileBytesV4Full + 1597 * 2);
    }

    [Fact]
    public void V2_To_Full_Expansion_Preserves_Body_Byte_Exact_And_Zeros_Tail()
    {
        // Build a v2-shaped file with marker bytes everywhere.
        var src = new byte[BusLayout.TotalFileBytesV2];
        for (int i = 0; i < src.Length; i++)
            src[i] = (byte)(i & 0xFF);
        BusLayout.MagicBytes.CopyTo(src, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(src, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(BusLayout.LayoutVersionV2).CopyTo(src, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCountV2).CopyTo(src, BusLayout.HeaderRegCountOffset);

        // Documented procedure: expand; patch layoutVer=5 + regCount=10001; zero 49..63;
        // preserve body 64..1363; zero 1364..20065.
        var dst = new byte[BusLayout.TotalFileBytes];
        Array.Copy(src, dst, src.Length);

        BitConverter.GetBytes(BusLayout.LayoutVersion).CopyTo(dst, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCount).CopyTo(dst, BusLayout.HeaderRegCountOffset);
        for (int i = BusLayout.WitsHeaderReservedFrom; i < BusLayout.HeaderBytes; i++)
            dst[i] = 0;

        // Body preserved byte-exact.
        for (int i = BusLayout.BodyOffset; i < BusLayout.TotalFileBytesV2; i++)
            Assert.Equal(src[i], dst[i]);
        // New tail (lan_ext body) all zero.
        for (int i = BusLayout.TotalFileBytesV2; i < BusLayout.TotalFileBytes; i++)
            Assert.Equal(0, dst[i]);
        // Resulting shape is the current full shape.
        Assert.Equal(BusLayout.Magic, BitConverter.ToUInt32(dst, BusLayout.HeaderMagicOffset));
        Assert.Equal(BusLayout.Version, BitConverter.ToUInt16(dst, BusLayout.HeaderVersionOffset));
        Assert.Equal(BusLayout.LayoutVersion, BitConverter.ToUInt16(dst, BusLayout.HeaderLayoutVersionOffset));
        Assert.Equal(BusLayout.RegCount, BitConverter.ToUInt16(dst, BusLayout.HeaderRegCountOffset));
        Assert.True(BusLayout.IsValidShape(
            BitConverter.ToUInt32(dst, BusLayout.HeaderMagicOffset),
            BitConverter.ToUInt16(dst, BusLayout.HeaderVersionOffset),
            BitConverter.ToUInt16(dst, BusLayout.HeaderLayoutVersionOffset),
            BitConverter.ToUInt16(dst, BusLayout.HeaderRegCountOffset),
            dst.Length));
    }

    [Fact]
    public void V3Small_To_Full_Expansion_Preserves_Body_Byte_Exact_And_Zeros_Tail()
    {
        // Build a v3-small-shaped file (delta 1.2.0) with marker bytes everywhere.
        var src = new byte[BusLayout.TotalFileBytesV3Small];
        for (int i = 0; i < src.Length; i++)
            src[i] = (byte)(i & 0xFF);
        BusLayout.MagicBytes.CopyTo(src, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(src, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(BusLayout.LayoutVersionV3).CopyTo(src, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCountV3Small).CopyTo(src, BusLayout.HeaderRegCountOffset);

        // Documented procedure: expand; patch layoutVer=5 + regCount=10001;
        // preserve body 64..3363; zero 3364..20065.
        var dst = new byte[BusLayout.TotalFileBytes];
        Array.Copy(src, dst, src.Length);

        BitConverter.GetBytes(BusLayout.LayoutVersion).CopyTo(dst, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCount).CopyTo(dst, BusLayout.HeaderRegCountOffset);
        // Source carried layoutVer 3; the expansion lifts it to 5.
        Assert.Equal(BusLayout.LayoutVersionV3, BitConverter.ToUInt16(src, BusLayout.HeaderLayoutVersionOffset));
        Assert.Equal(BusLayout.LayoutVersion, BitConverter.ToUInt16(dst, BusLayout.HeaderLayoutVersionOffset));

        // Body preserved byte-exact (incl. the small lan_ext 650..1649 data).
        for (int i = BusLayout.BodyOffset; i < BusLayout.TotalFileBytesV3Small; i++)
            Assert.Equal(src[i], dst[i]);
        // New tail (the added 8351 regs) all zero.
        for (int i = BusLayout.TotalFileBytesV3Small; i < BusLayout.TotalFileBytes; i++)
            Assert.Equal(0, dst[i]);
        // Resulting shape is the current full shape.
        Assert.Equal(BusLayout.RegCount, BitConverter.ToUInt16(dst, BusLayout.HeaderRegCountOffset));
        Assert.True(BusLayout.IsValidShape(
            BitConverter.ToUInt32(dst, BusLayout.HeaderMagicOffset),
            BitConverter.ToUInt16(dst, BusLayout.HeaderVersionOffset),
            BitConverter.ToUInt16(dst, BusLayout.HeaderLayoutVersionOffset),
            BitConverter.ToUInt16(dst, BusLayout.HeaderRegCountOffset),
            dst.Length));
    }

    [Theory]
    [InlineData(3)] // v3-full — same 8404/16872 geometry, layoutVer 3
    [InlineData(4)] // v4-full
    public void V34Full_To_V5Full_Expansion_Preserves_Body_Byte_Exact_And_Zeros_Tail(ushort legacyLayoutVer)
    {
        // Build an 8404/16872 file (layoutVer 3 or 4) with marker bytes everywhere.
        var src = new byte[BusLayout.TotalFileBytesV4Full];
        for (int i = 0; i < src.Length; i++)
            src[i] = (byte)(i & 0xFF);
        BusLayout.MagicBytes.CopyTo(src, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(src, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(legacyLayoutVer).CopyTo(src, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCountV4Full).CopyTo(src, BusLayout.HeaderRegCountOffset);

        // Pinned guarantee: header 49..63 are reserved-zero in every v3/v4-full
        // file (born from the v2-upgrade zeroing ∨ an all-zero fresh create) —
        // so the migration needs NO re-zeroing of 49..63.
        for (int i = BusLayout.WitsHeaderReservedFrom; i < BusLayout.HeaderBytes; i++)
            src[i] = 0;

        // Documented FULL MIGRATION procedure: expand; patch layoutVer=5 +
        // regCount=10001; preserve body 64..16871 byte-exact; zero 16872..20065;
        // header 49..63 untouched (already zero).
        var dst = new byte[BusLayout.TotalFileBytes];
        Array.Copy(src, dst, src.Length);

        BitConverter.GetBytes(BusLayout.LayoutVersion).CopyTo(dst, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCount).CopyTo(dst, BusLayout.HeaderRegCountOffset);

        // Body preserved byte-exact.
        for (int i = BusLayout.BodyOffset; i < BusLayout.TotalFileBytesV4Full; i++)
            Assert.Equal(src[i], dst[i]);
        // New tail (the added 1597 regs) all zero.
        for (int i = BusLayout.TotalFileBytesV4Full; i < BusLayout.TotalFileBytes; i++)
            Assert.Equal(0, dst[i]);
        // Resulting shape = the current full shape; the source was an upgrade source.
        Assert.Equal(BusLayout.LayoutVersion, BitConverter.ToUInt16(dst, BusLayout.HeaderLayoutVersionOffset));
        Assert.Equal(BusLayout.RegCount, BitConverter.ToUInt16(dst, BusLayout.HeaderRegCountOffset));
        Assert.True(BusLayout.IsUpgradeable(1, legacyLayoutVer, BusLayout.RegCountV4Full, BusLayout.TotalFileBytesV4Full));
        Assert.True(BusLayout.IsValidShape(
            BitConverter.ToUInt32(dst, BusLayout.HeaderMagicOffset),
            BitConverter.ToUInt16(dst, BusLayout.HeaderVersionOffset),
            BitConverter.ToUInt16(dst, BusLayout.HeaderLayoutVersionOffset),
            BitConverter.ToUInt16(dst, BusLayout.HeaderRegCountOffset),
            dst.Length));
    }

    // ── reserved-wits convention (v3) ───────────────────────────────────────
    [Fact]
    public void ReservedWits_NoDescriptor_Except_Wits_Covers_600_To_649()
    {
        // Convention: among ALL spec-default descriptors, only Wits intersects
        // 600..649 — lan_ext starts at 650, raw ends at 599.
        var zones = new[]
        {
            ZoneDescriptor.ComMain,
            ZoneDescriptor.Lan,
            ZoneDescriptor.RawReserve,
            ZoneDescriptor.Wits,
            ZoneDescriptor.LanExt,
        };

        foreach (var z in zones)
        {
            bool coversWits = z.EndReg >= 600 && z.BaseReg <= 649;
            if (z.Kind == ZoneKind.Wits)
                Assert.True(coversWits, "Wits descriptor must cover 600..649");
            else
                Assert.False(coversWits, $"zone {z.Kind} must not cover 600..649 (reserved)");
        }

        // lan_ext is contiguous right after the reserved zone — no body shift.
        Assert.Equal(ZoneDescriptor.Wits.EndReg + 1, ZoneDescriptor.LanExt.BaseReg);
    }

    [Fact]
    public void Wits_Zone_Is_Reserved_Dead_No_Slot_Table_In_Sdk()
    {
        // wits functionality REMOVED (2026-08-14): the Sdk carries NO slot table —
        // the former slot addresses live only in CONTRACT_BusV2.yaml §3 (history).
        // The reserved zone still exists as a descriptor (body shift avoidance) but
        // must not be addressable by any registry/slot API surface.
        Assert.Equal(600, ZoneDescriptor.Wits.BaseReg);
        Assert.Equal(649, ZoneDescriptor.Wits.EndReg);
        // No WitsSlots/WitsSlotCount/WitsReservedFrom members exist on BusLayout
        // (compile-time absence is the pin; this test documents the removal).
        Assert.DoesNotContain(typeof(BusLayout).GetMembers(), m => m.Name.Contains("WitsSlot"));
        Assert.DoesNotContain(typeof(BusLayout).GetMembers(), m => m.Name.Contains("WitsReserved"));
    }
}
