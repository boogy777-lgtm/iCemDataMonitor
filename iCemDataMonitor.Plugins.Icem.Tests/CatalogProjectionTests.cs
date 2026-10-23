using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Plugins.Icem.Tests;

public sealed class CatalogProjectionTests
{
    // Captured legacy fallback vectors: order is intentional (LAN is not sorted by position).
    [Fact]
    public void AbsentSnapshot_PreservesExactFallbackVectorsAndCanonicalLookup()
    {
        var registry = DeviceCatalogRegistry.FromLoader(() => throw new FileNotFoundException());
        VerifyFallback(registry.GetCatalog("flecs"), """
            UnixTimestamp|Time & Date||#888888|1
            UnitNum|Unit Num||#888888|2
            SlurryDensity|Recirc Density|ppg|#CC3300|3
            DH_Density|DH Density|ppg|#339933|4
            MixWaterRate|Mix Water Rate|bpm|#006699|5
            PumpRate|Comb Pump Rate|bpm|#FF9933|6
            PumpPressure|PS Pump Press|psi|#634B9F|7
            DSPumpPress|DS Pump Press|psi|#FFCC05|8
            WaterStgTot|Water Stg Tot|bbl|#D86C27|9
            MixWaterTotal|Mix Water Total|bbl|#009999|10
            PumpStgTot|Pump Stg Tot|bbl|#660066|11
            CombPumpTotal|Comb Pump Total|bbl|#A1A744|12
            CementValvePsn|Cmnt Valve Psn|%|#CC3300|13
            WaterValvePsn|Water Valve Psn|%|#339933|14
            PSPumpRate|PS Pump Rate|bpm|#006699|15
            DSPumpRate|DS Pump Rate|bpm|#FF9933|16
            DigOut|Dig out||#634B9F|17
            HalWinEvent|HalWin Event||#FFCC05|18
            Temperature|Temperature|degF|#D86C27|19
            rho12_hat|rho12_hat|ppg|#009999|20
            h2_hat|h2_hat|ft|#660066|21
            vhp_density_HP|vhp_density_HP|ppg|#A1A744|22
            TubHeight|Tub Height|ft|#CC3300|23
            ExtraPress1|Extra Press 1|psi|#339933|24
            ExtraPress2|Extra Press 2|psi|#006699|25
            ExtraRate1|Extra Rate 1|bpm|#FF9933|26
            ExtraTotal1|Extra Total 1|bbl|#634B9F|27
            ExtraRate2|Extra Rate 2|bpm|#FFCC05|28
            ExtraTotal2|Extra Total 2|bbl|#D86C27|29
            WtrSlryRatio|Wtr/slry Ratio||#009999|30
            MudCupDensity|MudCup Density|ppg|#660066|31
            PSPmpStgTot|PS Pmp Stg Tot|bbl|#A1A744|32
            DSPmpStgTot|DS Pmp Stg Tot|bbl|#CC3300|33
            VolumetricMeter|VolumetricMeter|bpm|#339933|34
            """);
        VerifyFallback(registry.GetCatalog("plc"), """
            PLC Timestamp|PLC Timestamp||#888888|100
            PLC Status|PLC Status||#888888|101
            Suction Pressure|Suction Pressure|psi|#CC3300|102
            Discharge Pressure|Discharge Pressure|psi|#339933|103
            Pump Rate|Pump Rate|bpm|#006699|104
            Flow Rate|Flow Rate|gpm|#FF9933|105
            Tank Level|Tank Level|ft|#634B9F|106
            Tank Temperature|Tank Temperature|degF|#FFCC05|107
            Motor Speed|Motor Speed|rpm|#D86C27|108
            Valve Position|Valve Position|%|#009999|109
            """);
        VerifyFallback(registry.GetCatalog("lan"), """
            RecircDensity|Recirc Density|ppg|#E65100|400
            RecircFlowRate|Recirc Flow Rate|bpm|#00796B|401
            PumpAPressure|Pump A Pressure|psi|#283593|433
            OPKOASetting|OPKO A Setting|psi|#558B2F|435
            PumpARate|Pump A Rate|bpm|#BF360C|438
            UnitNumber|Unit Number||#4A148C|427
            ControlMode|Control Mode||#004D40|428
            MixMode|Mix Mode||#880E4F|429
            """);
    }

    private static void VerifyFallback(iCemDataMonitor.Sdk.IVariableCatalog catalog, string expected)
    {
        var entries = expected.Split('\n').Select(row => row.Trim().Split('|'))
            .Select(p => new VariableDefinition(p[0], p[1], p[2], p[3], int.Parse(p[4]), false, "catalog")).ToArray();
        Assert.Equal(entries, catalog.GetCatalog());
        foreach (var entry in entries)
            Assert.Equal((entry.Description, entry.Uom, (int?)entry.Position), catalog.Lookup(entry.Name.ToUpperInvariant()));
        Assert.Null(catalog.Lookup("not a canonical name"));
    }

    [Theory]
    [InlineData("flecs", "#CC3300,#339933,#006699,#FF9933,#634B9F,#FFCC05,#D86C27,#009999,#660066,#A1A744")]
    [InlineData("plc", "#CC3300,#339933,#006699,#FF9933,#634B9F,#FFCC05,#D86C27,#009999,#660066,#A1A744")]
    [InlineData("lan", "#E65100,#00796B,#283593,#558B2F,#BF360C,#4A148C,#004D40,#880E4F,#1A237E,#33691E,#B71C1C,#311B92,#00695C,#827717,#3E2723,#0D47A1,#1B5E20,#F57F17,#212121,#01579B")]
    public void Palette_RetainsEveryColorAndWrap(string family, string palette)
    {
        var catalog = DeviceCatalogRegistry.FromLoader(() => throw new FileNotFoundException()).GetCatalog(family);
        var colors = palette.Split(',');
        for (var i = 0; i < colors.Length * 3; i++)
            Assert.Equal(colors[i % colors.Length], catalog.GetColor(i));
    }

    [Fact]
    public void NewFamily_UsesSharedProjectionWithOnlyLocalOffsetFallbackAndPalettePolicy()
    {
        var snapshot = CatalogSnapshotTests.Read(CatalogSnapshotTests.SyntheticJson);
        var fallback = new[] { new VariableDefinition("Backup", "Backup display", "ft", "#000000", 19, false, "catalog") };
        var palette = new[] { "#010203", "#040506" };
        var healthy = CatalogProjection.Create(snapshot, "EXTRA", 17, () => throw new Exception("Fallback must not run"), palette);
        Assert.Equal(new VariableDefinition("ExtraSignal", "Extra alias", "", "", 22, false, "registry"), Assert.Single(healthy.GetCatalog()));
        Assert.Equal(("Extra alias", "", (int?)22), healthy.Lookup("EXTRASIGNAL"));
        Assert.Null(healthy.Lookup("Extra alias"));
        var degraded = CatalogProjection.Create(null, "new-family", 17, () => fallback, palette);
        fallback[0] = default;
        palette[0] = "changed";
        Assert.Equal("Backup", Assert.Single(degraded.GetCatalog()).Name);
        Assert.Equal(("Backup display", "ft", (int?)19), degraded.Lookup("BACKUP"));
        Assert.Equal("#010203", healthy.GetColor(2));
        Assert.Equal("#010203", degraded.GetColor(2));
        Assert.Equal("bbl", healthy.GuessUom("TankLevel"));
    }
}
