using System.Linq;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

public class BusLayoutTests
{
    [Fact]
    public void Sizes_MatchV5Contract()
    {
        Assert.Equal(64, BusLayout.HeaderBytes);
        Assert.Equal(20002, BusLayout.BodyBytes);
        Assert.Equal(20066, BusLayout.TotalFileBytes);
        Assert.Equal(10001, BusLayout.RegCount);
        Assert.Equal(64, BusLayout.BodyOffset);
        Assert.Equal(2, BusLayout.BytesPerReg);
        Assert.Equal(BusLayout.HeaderBytes + BusLayout.RegCount * BusLayout.BytesPerReg, BusLayout.TotalFileBytes);
    }

    [Fact]
    public void HistoricalShapes_MatchContract()
    {
        // upgrade-source pins (FULL MIGRATION)
        Assert.Equal(2, BusLayout.LayoutVersionV2);
        Assert.Equal(650, BusLayout.RegCountV2);
        Assert.Equal(1364, BusLayout.TotalFileBytesV2);
        Assert.Equal(3, BusLayout.LayoutVersionV3);
        Assert.Equal(1650, BusLayout.RegCountV3Small);
        Assert.Equal(3364, BusLayout.TotalFileBytesV3Small);
        Assert.Equal(4, BusLayout.LayoutVersionV4);
        Assert.Equal(8404, BusLayout.RegCountV4Full);      // v3-full/v4-full upgrade source
        Assert.Equal(16872, BusLayout.TotalFileBytesV4Full);
    }

    [Fact]
    public void RegCountBounds_MatchLengthDerivedRule()
    {
        // valid ⇔ RegCountMin ≤ regCount ≤ RegCountMax ∧ fileLength == 64 + regCount*2
        Assert.Equal(650, BusLayout.RegCountMin);
        Assert.Equal(65535, BusLayout.RegCountMax);
        Assert.True(BusLayout.RegCountMin <= BusLayout.RegCountMax);
    }

    [Fact]
    public void Identity_MatchesV5Contract()
    {
        Assert.Equal(0x424E5453u, BusLayout.Magic);
        Assert.Equal(new byte[] { 0x53, 0x54, 0x4E, 0x42 }, BusLayout.MagicBytes);
        Assert.Equal(1, BusLayout.Version);
        Assert.Equal(5, BusLayout.LayoutVersion); // v5 = bus register map v5
        Assert.Equal(@"Global\iCemDataMonitorBus_v1", BusLayout.BusName);
        Assert.Equal(@"Global\iCemDataMonitorBusMutex_v1", BusLayout.MutexName);
        Assert.Equal(@"C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat", BusLayout.BackingFilePath);
    }

    [Fact]
    public void HeaderOffsets_AreDistinct_AndInsideHeader()
    {
        int[] offsets =
        {
            BusLayout.HeaderMagicOffset, BusLayout.HeaderVersionOffset,
            BusLayout.HeaderLayoutVersionOffset, BusLayout.HeaderRegCountOffset,
            BusLayout.ZoneComLastWriteTickOffset, BusLayout.ZoneComActiveFlagOffset,
            BusLayout.ZoneLanLastWriteTickOffset, BusLayout.ZoneLanActiveFlagOffset,
            BusLayout.ZoneRawLastWriteTickOffset, BusLayout.ZoneRawActiveFlagOffset,
            BusLayout.ProducerComWriteCountOffset, BusLayout.ProducerLanWriteCountOffset,
            BusLayout.ProducerRawWriteCountOffset,
            BusLayout.ZoneWitsLastWriteTickOffset, BusLayout.ZoneWitsActiveFlagOffset,
            BusLayout.ProducerWitsWriteCountOffset, BusLayout.HeaderReservedOffset,
        };

        Assert.Equal(offsets.Length, offsets.Distinct().Count());
        Assert.All(offsets, o => Assert.InRange(o, 0, BusLayout.HeaderBytes - 1));
    }

    [Fact]
    public void ReservedZeroBlock_MatchContract()
    {
        // former wits block (49..61) + reserved tail (62..63) — must stay zero
        Assert.Equal(49, BusLayout.ZoneWitsLastWriteTickOffset);
        Assert.Equal(57, BusLayout.ZoneWitsActiveFlagOffset);
        Assert.Equal(58, BusLayout.ProducerWitsWriteCountOffset);
        Assert.Equal(62, BusLayout.HeaderReservedOffset);
        Assert.Equal(2, BusLayout.HeaderReservedBytes);
        Assert.Equal(BusLayout.BodyOffset, BusLayout.HeaderReservedOffset + BusLayout.HeaderReservedBytes);
    }

    [Fact]
    public void V5Zones_AreNonOverlapping_AndAddInSurfaceEndsAt599()
    {
        // com_stream 4..199 / lan_stream_crio 200..399 / raw_com_line 400..599 /
        // ace_full RESERVE 600..10000 (DEFERRED — nobody writes in v5)
        Assert.Equal(4, BusLayout.ComStreamBaseReg);
        Assert.Equal(196, BusLayout.ComStreamRegCount);
        Assert.Equal(200, BusLayout.LanStreamCrioBaseReg);
        Assert.Equal(200, BusLayout.LanStreamCrioRegCount);
        Assert.Equal(400, BusLayout.RawComLineBaseReg);
        Assert.Equal(200, BusLayout.RawComLineRegCount);
        Assert.Equal(600, BusLayout.AceFullBaseReg);
        Assert.Equal(10000, BusLayout.AceFullEndReg);
        Assert.Equal(9401, BusLayout.AceFullRegCount);
        Assert.Equal(599, BusLayout.AddInMaxReg);

        Assert.True(BusLayout.ComStreamBaseReg + BusLayout.ComStreamRegCount - 1 < BusLayout.LanStreamCrioBaseReg);
        Assert.True(BusLayout.LanStreamCrioBaseReg + BusLayout.LanStreamCrioRegCount - 1 < BusLayout.RawComLineBaseReg);
        Assert.True(BusLayout.RawComLineBaseReg + BusLayout.RawComLineRegCount - 1 < BusLayout.AceFullBaseReg);
        Assert.Equal(BusLayout.RegCount - 1, BusLayout.AceFullEndReg); // reserve runs to the dictionary end
        Assert.True(BusLayout.AddInMaxReg < BusLayout.AceFullBaseReg);
    }

    [Fact]
    public void ZoneCapacities_MatchV5Contract()
    {
        // 98 / 100 / 100-fields / ace_full reserve 9401 (600..10000)
        Assert.Equal(98, BusLayout.ComStreamRegCount / 2);
        Assert.Equal(100, BusLayout.LanStreamCrioRegCount / 2);
        Assert.Equal(100, BusLayout.RawComLineRegCount / 2); // v5.1: float32 CDAB positional CSV fields
        Assert.Equal(100, RawCsvFields.MaxFields);
        Assert.Equal(4, BusLayout.ComTimestampReg); // v5.1 CHANGE 1: idx0 = unix seconds int32 raw bits
    }

    [Fact]
    public void ZoneKind_ByteOrder_IsPositionalBinding()
    {
        // header zone block index ≡ ZoneKind value; APPEND-ONLY order
        Assert.Equal(0, (byte)ZoneKind.ComMain);
        Assert.Equal(1, (byte)ZoneKind.Lan);
        Assert.Equal(2, (byte)ZoneKind.RawReserve);
        Assert.Equal(3, (byte)ZoneKind.Wits);
        Assert.Equal(4, (byte)ZoneKind.LanExt);
    }

    [Theory]
    // FULL MIGRATION: every known legacy dictionary shape is an expansion source.
    [InlineData(1, 2, 650, 1364, true)]     // exact v2 shape ⇒ expansion candidate
    [InlineData(1, 3, 1650, 3364, true)]    // exact v3-small shape ⇒ expansion candidate
    [InlineData(1, 3, 8404, 16872, true)]   // v3-full ⇒ expansion candidate
    [InlineData(1, 4, 8404, 16872, true)]   // v4-full ⇒ expansion candidate
    [InlineData(1, 2, 650, 1363, false)]    // wrong length
    [InlineData(1, 2, 651, 1366, false)]    // wrong regCount
    [InlineData(2, 2, 650, 1364, false)]    // wrong version
    [InlineData(1, 4, 8404, 16870, false)]  // v4-full length off-by-two
    [InlineData(1, 5, 8404, 16872, false)]  // layoutVer 5 = FINAL shape, ¬ upgrade source
    [InlineData(1, 5, 10001, 20066, false)] // current v5-full: open as-is
    [InlineData(1, 3, 700, 1464, false)]    // arbitrary legacy regCount — NOT a source
    [InlineData(1, 1, 600, 1264, false)]    // v1 is DEAD
    [InlineData(0, 0, 0, 0, false)]
    public void IsUpgradeable_OnlyExactExpansionSources(int version, int layoutVer, int regCount, long fileLength, bool expected)
    {
        Assert.Equal(expected, BusLayout.IsUpgradeable((ushort)version, (ushort)layoutVer, (ushort)regCount, fileLength));
    }

    [Theory]
    // v5 SHAPE LAW (FULL MIGRATION): FINAL shapes are layoutVer==5 ONLY with the
    // dynamic regCount range; layoutVer<5 is an upgrade-source layout, ¬ final.
    [InlineData(1, 5, 10001, 20066, true)]          // current-default v5-full
    [InlineData(1, 5, 8404, 16872, true)]           // v5 at any in-range regCount (dynamic law)
    [InlineData(1, 5, 650, 1364, true)]             // v5 small shape (dynamic law)
    [InlineData(1, 5, 65535, 64 + 65535 * 2, true)] // u16 max regCount
    [InlineData(1, 4, 8404, 16872, false)]          // v4-full ⇒ upgrade source, ¬ final shape
    [InlineData(1, 3, 8404, 16872, false)]          // v3-full ⇒ upgrade source, ¬ final shape
    [InlineData(1, 3, 1650, 3364, false)]           // v3-small ⇒ upgrade source, ¬ final shape
    [InlineData(1, 2, 650, 1364, false)]            // v2 ⇒ upgrade source, ¬ final shape
    [InlineData(1, 3, 700, 1464, false)]            // arbitrary legacy regCount ⇒ foreign
    [InlineData(1, 1, 600, 1264, false)]            // v1 ⇒ DEAD
    [InlineData(2, 5, 650, 1364, false)]            // wrong version
    [InlineData(1, 9, 650, 1364, false)]            // future layoutVer ⇒ fail-safe
    [InlineData(1, 5, 649, 64 + 649 * 2, false)]    // below MinRegCount
    [InlineData(1, 5, 650, 2000, false)]            // wrong length
    [InlineData(1, 5, 10001, 20064, false)]         // v5-full length off-by-two
    [InlineData(0, 0, 0, 0, false)]
    public void IsValidShape_V5FinalShapeLaw(int version, int layoutVer, int regCount, long fileLength, bool expected)
    {
        Assert.Equal(expected, BusLayout.IsValidShape((ushort)version, (ushort)layoutVer, (ushort)regCount, fileLength));
    }

    [Fact]
    public void HeaderStateRegisterMapV5_MatchesContract()
    {
        // v5 map: 4 LIVE regs, ALL AddIn-owned; POC writes NOTHING in the header
        Assert.Equal(0, BusLayout.RegComPort);
        Assert.Equal(1, BusLayout.RegCaseState);
        Assert.Equal(2, BusLayout.RegCaseLanState);
        Assert.Equal(3, BusLayout.RegHeartbeat);
        Assert.Equal(4, BusLayout.HeaderRegCount);
        Assert.Equal(4, BusLayout.ZoneBaseMin);

        // D2 heartbeat wrap
        Assert.Equal((ushort)32000, BusLayout.HeartbeatWrap);

        // reg 1 caseState values — RealtimeState passthrough (tap-exposed only)
        Assert.Equal(0, BusLayout.CaseStateUnknown);
        Assert.Equal(1, BusLayout.CaseStateNotStarted);
        Assert.Equal(2, BusLayout.CaseStateInProgress);
        Assert.Equal(3, BusLayout.CaseStateCompleted);
        Assert.Equal(4, BusLayout.CaseStateSuspended);
        Assert.Equal(5, BusLayout.CaseStateSimulationRunning);

        // reg 2 caseLanState values — ComPortStates passthrough (tap-exposed only)
        Assert.Equal(0, BusLayout.CaseLanStateNone);
        Assert.Equal(1, BusLayout.CaseLanStateSelected);
        Assert.Equal(2, BusLayout.CaseLanStateStreaming);
        Assert.Equal(4, BusLayout.CaseLanStateRecording);
        Assert.Equal(8, BusLayout.CaseLanStateLostConnection);
        Assert.Equal(16, BusLayout.CaseLanStatePausing);
    }

    [Fact]
    public void V5ZoneFloor_NoStateBlockOverlap()
    {
        // v5: the state block is regs 0..3 ONLY — com_stream starts at reg 4
        // (the v4 4..15 overlap/skip rule is GONE)
        Assert.True(BusLayout.ComStreamBaseReg >= BusLayout.HeaderRegCount);
        Assert.True(BusLayout.ComStreamBaseReg + BusLayout.ComStreamRegCount - 1 < BusLayout.LanStreamCrioBaseReg);
    }
}
