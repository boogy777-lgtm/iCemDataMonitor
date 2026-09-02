using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk;

namespace iCemDataMonitor.Plugins.Icem.Tests;

public class CatalogTests
{
    // ── FlecsVariableCatalog ──

    [Fact]
    public void FlecsCatalog_Has34Entries()
    {
        var catalog = new FlecsVariableCatalog();
        var entries = catalog.GetCatalog();
        Assert.Equal(34, entries.Count);
    }

    [Fact]
    public void FlecsCatalog_Lookup_ReturnsCorrectDisplay()
    {
        var catalog = new FlecsVariableCatalog();
        var result = catalog.Lookup("SlurryDensity");
        Assert.NotNull(result);
        Assert.Equal("Recirc Density", result.Value.Display);
        Assert.Equal("ppg", result.Value.Uom);

        result = catalog.Lookup("VolumetricMeter");
        Assert.NotNull(result);
        Assert.Equal("bpm", result.Value.Uom);
    }

    [Fact]
    public void FlecsCatalog_AllDescriptions_MatchSsfDisplayNames()
    {
        var catalog = new FlecsVariableCatalog();
        var entries = catalog.GetCatalog();

        // Load SSF entries for comparison
        var ssfVars = SsfTemplateLoader.Load("flecs_ver4p53_ppg_bpm_psi");

        // Every catalog entry's Description must equal at least one SSF col1
        foreach (var entry in entries)
        {
            var match = ssfVars.Any(s => s.Name == entry.Description);
            Assert.True(match,
                $"Catalog Description '{entry.Description}' (key: {entry.Name}) not found in SSF display names");
        }
    }

    // ── PlcVariableCatalog ──

    [Fact]
    public void PlcCatalog_HasEntries()
    {
        var catalog = new PlcVariableCatalog();
        var entries = catalog.GetCatalog();
        Assert.NotEmpty(entries);
    }

    [Theory]
    [InlineData("PLC Timestamp", "")]
    [InlineData("PLC Status", "")]
    [InlineData("Suction Pressure", "psi")]
    [InlineData("Discharge Pressure", "psi")]
    [InlineData("Pump Rate", "bpm")]
    [InlineData("Flow Rate", "gpm")]
    [InlineData("Tank Level", "ft")]
    [InlineData("Tank Temperature", "degF")]
    [InlineData("Motor Speed", "rpm")]
    [InlineData("Valve Position", "%")]
    public void PlcCatalog_EmulatorNames_ResolveCorrectly(string name, string expectedUom)
    {
        var catalog = new PlcVariableCatalog();
        var result = catalog.Lookup(name);
        Assert.NotNull(result);
        Assert.Equal(expectedUom, result.Value.Uom);
    }

    [Fact]
    public void PlcCatalog_LookupSsfName_Resolves()
    {
        var catalog = new PlcVariableCatalog();
        var result = catalog.Lookup("Pump Rate");
        Assert.NotNull(result);
        Assert.Equal("bpm", result.Value.Uom);

        result = catalog.Lookup("Flow Rate");
        Assert.NotNull(result);
        Assert.Equal("gpm", result.Value.Uom);

        result = catalog.Lookup("Motor Speed");
        Assert.NotNull(result);
        Assert.Equal("rpm", result.Value.Uom);
    }

    // ── DeviceCatalogRegistry ──

    [Fact]
    public void Registry_Flecs_GivesFlecsCatalog()
    {
        var registry = new DeviceCatalogRegistry();
        var catalog = registry.GetCatalog("flecs");
        Assert.IsType<FlecsVariableCatalog>(catalog);
        Assert.Equal(34, catalog.GetCatalog().Count);
    }

    [Fact]
    public void Registry_Plc_GivesPlcCatalog()
    {
        var registry = new DeviceCatalogRegistry();
        var catalog = registry.GetCatalog("plc");
        Assert.IsType<PlcVariableCatalog>(catalog);
        Assert.NotEmpty(catalog.GetCatalog());
    }

    [Fact]
    public void Registry_Unknown_GivesFallback()
    {
        var registry = new DeviceCatalogRegistry();
        var catalog = registry.GetCatalog("unipro");
        Assert.NotNull(catalog);
        // Fallback catalog has empty GetCatalog()
        Assert.Empty(catalog.GetCatalog());
        // But GuessUom still works
        Assert.Equal("psi", catalog.GuessUom("SomePressure"));
    }

    [Fact]
    public void Registry_ImplementsInterface()
    {
        IDeviceCatalogRegistry registry = new DeviceCatalogRegistry();
        Assert.NotNull(registry.GetCatalog("flecs"));
        Assert.NotNull(registry.GetCatalog("plc"));
        Assert.NotNull(registry.GetCatalog("unknown"));
    }

    // ── LanVariableCatalog ──

    [Fact]
    public void Registry_Lan_GivesLanCatalog()
    {
        var registry = new DeviceCatalogRegistry();
        var catalog = registry.GetCatalog("lan");
        Assert.IsType<LanVariableCatalog>(catalog);
        Assert.Equal(3463, catalog.GetCatalog().Count);
    }

    [Fact]
    public void LanCatalog_Has3463Entries()
    {
        var catalog = new LanVariableCatalog();
        var entries = catalog.GetCatalog();
        Assert.Equal(3463, entries.Count);
    }

    [Fact]
    public void LanCatalog_Lookup_ResolvesCorrectly()
    {
        var catalog = new LanVariableCatalog();
        // Verify key signals from Lnd Pump Sect
        var result = catalog.Lookup("Lan/PumpAPressure");
        Assert.NotNull(result);
        Assert.Equal("Pump A Pressure", result.Value.Display);
        Assert.Equal("psi", result.Value.Uom);

        result = catalog.Lookup("Lan/OPKOASetting");
        Assert.NotNull(result);
        Assert.Equal("OPKO A Setting", result.Value.Display);
        Assert.Equal("psi", result.Value.Uom);

        result = catalog.Lookup("Lan/UnitNumber");
        Assert.NotNull(result);
        Assert.Equal("", result.Value.Uom);
    }

    [Fact]
    public void LanCatalog_ModbusOffsets_StartAt400()
    {
        var catalog = new LanVariableCatalog();
        var first = catalog.Lookup("Lan/RecircDensity");
        Assert.NotNull(first);
        Assert.Equal(400, first.Value.ModbusOffset);
    }

    [Fact]
    public void LanCatalog_UomMatchesRegistry()
    {
        var catalog = new LanVariableCatalog();
        var entries = catalog.GetCatalog();

        // Spot-check UOMs from different SVs
        var pumpA = entries.First(e => e.Name == "Lan/PumpAPressure");
        Assert.Equal("psi", pumpA.Uom);

        // Recirc Flow Rate has UOM "bbl/min" in the XML (field-verified)
        var flowRate = entries.First(e => e.Name == "Lan/RecircFlowRate");
        Assert.Equal("bbl/min", flowRate.Uom);
    }

    [Fact]
    public void Registry_ImplementsInterface_WithLan()
    {
        IDeviceCatalogRegistry registry = new DeviceCatalogRegistry();
        Assert.NotNull(registry.GetCatalog("lan"));
        Assert.IsType<LanVariableCatalog>(registry.GetCatalog("lan"));
    }

    // ── DeviceCatalogRegistry Modbus map (registry file = source of truth) ──

    [Fact]
    public void DeviceCatalog_GetModbusBase_MatchesRegistryFile()
    {
        var expected = ModbusRegistryFixture.LoadExpectedModbusBases();
        var deviceRegistry = new DeviceCatalogRegistry();

        Assert.Equal(expected.Count, deviceRegistry.GetFamilies().Count());
        foreach (var (family, baseAddress) in expected)
            Assert.Equal(baseAddress, deviceRegistry.GetModbusBase(family));
    }

    [Fact]
    public void DeviceCatalog_RegisterEntries_FollowBasePlusIndexTimes2()
    {
        var reg = ModbusRegistryFixture.LoadRegistry();
        var deviceRegistry = new DeviceCatalogRegistry();

        foreach (var family in deviceRegistry.GetFamilies())
        {
            int baseAddress = deviceRegistry.GetModbusBase(family);
            var expectedEntries = reg.GetFamilyVariables(family)
                .Where(e => !e.ExcludeFromTelemetry)
                .OrderBy(e => e.Index)
                .Select(e => (Name: e.Name, Address: baseAddress + e.Index * 2))
                .ToList();

            var actual = deviceRegistry.GetRegisterEntries(family);
            Assert.Equal(expectedEntries.Count, actual.Count);
            for (int i = 0; i < expectedEntries.Count; i++)
            {
                Assert.Equal(expectedEntries[i].Name, actual[i].VariableName);
                Assert.Equal(expectedEntries[i].Address, actual[i].RegisterAddress);
            }
        }
    }
}
