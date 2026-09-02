using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Plugins.Icem.Tests;

public class UnifiedVariableRegistryTests
{
    private static UnifiedVariableRegistry LoadRegistry()
    {
        return ModbusRegistryFixture.LoadRegistry();
    }

    [Fact]
    public void Registry_Parses_3507Variables()
    {
        var reg = LoadRegistry();
        Assert.Equal(3507, reg.Count); // 34 flecs + 10 plc + 96 legacy lan + 3367 lan_ext
    }

    [Fact]
    public void Registry_Flecs_Has34()
    {
        var reg = LoadRegistry();
        var vars = reg.GetFamilyVariables("flecs");
        Assert.Equal(34, vars.Count);
    }

    [Fact]
    public void Registry_Plc_Has10()
    {
        var reg = LoadRegistry();
        var vars = reg.GetFamilyVariables("plc");
        Assert.Equal(10, vars.Count);
    }

    [Fact]
    public void Registry_Lookup_SlurryDensity()
    {
        var reg = LoadRegistry();
        var entry = reg.Lookup("SlurryDensity");
        Assert.NotNull(entry);
        Assert.Equal("SlurryDensity", entry.Value.Name);
        Assert.Equal("ppg", entry.Value.Uom);
        Assert.Equal("#CC3300", entry.Value.Color);
        Assert.Equal("flecs", entry.Value.Family);
        Assert.Equal("Recirc Density", entry.Value.Alias);
    }

    [Fact]
    public void Registry_LookupByAlias_RecircDensity()
    {
        var reg = LoadRegistry();
        var entry = reg.LookupByAlias("flecs", "Recirc Density");
        Assert.NotNull(entry);
        Assert.Equal("SlurryDensity", entry.Value.Name);
    }

    [Fact]
    public void Registry_LookupByIndex_Flecs2()
    {
        var reg = LoadRegistry();
        var entry = reg.LookupByIndex("flecs", 2);
        Assert.NotNull(entry);
        Assert.Equal("SlurryDensity", entry.Value.Name);
    }

    [Fact]
    public void Registry_LookupByIndex_Plc2()
    {
        var reg = LoadRegistry();
        var entry = reg.LookupByIndex("plc", 2);
        Assert.NotNull(entry);
        Assert.Equal("Suction Pressure", entry.Value.Name);
    }

    [Fact]
    public void Registry_GetModbusBase_MatchesRegistryFile()
    {
        var reg = LoadRegistry();
        var expected = ModbusRegistryFixture.LoadExpectedModbusBases();
        Assert.Equal(ModbusRegistryFixture.FamilyOrdinals.Count, expected.Count);
        foreach (var (family, baseAddress) in expected)
            Assert.Equal(baseAddress, reg.GetModbusBase(family));
    }

    [Fact]
    public void Registry_GetModbusBase_BlockLayoutRelational()
    {
        // Invariant: modbusBase == blockBase + 4, blockBase == 2000 × family ordinal.
        // Heartbeat register = blockBase + 2 (== modbusBase − 2).
        // Block stride between consecutive families is exactly 2000.
        var reg = LoadRegistry();
        var expected = ModbusRegistryFixture.LoadExpectedModbusBases();

        var ordered = ModbusRegistryFixture.FamilyOrdinals
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            string family = ordered[i];
            int ordinal = ModbusRegistryFixture.FamilyOrdinals[family];
            int blockBase = ordinal * 2000;
            int baseAddress = reg.GetModbusBase(family);

            Assert.Equal(blockBase + 4, baseAddress);        // data registers start at blockBase+4
            Assert.Equal(blockBase + 2, baseAddress - 2);    // per-family heartbeat register
            Assert.Equal(expected[family], baseAddress);     // consistency with the file

            if (i > 0)
                Assert.Equal(2000, baseAddress - reg.GetModbusBase(ordered[i - 1]));
        }
    }

    [Fact]
    public void Registry_Families_DoNotSpillIntoNextBlock()
    {
        // Highest register used by a family's variables must stay inside its
        // 2000-register block (addresses are base + Index × 2). The lan family
        // is EXEMPT: since bus v2 its addressing is zone-based (lan 200..449 +
        // lan_ext 650..8403 via GetRegisterEntriesV2), ¬ the legacy v1 modbus
        // block — 3463 entries cannot and need not fit the 2000-reg block.
        var reg = LoadRegistry();
        var ordered = ModbusRegistryFixture.FamilyOrdinals
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var family in ordered)
        {
            if (family.Equals("lan", StringComparison.OrdinalIgnoreCase))
                continue; // bus-zoned since v2 — modbus block law does not apply

            var vars = reg.GetFamilyVariables(family);
            if (vars.Count == 0)
                continue;

            int baseAddress = reg.GetModbusBase(family);
            int maxAddress = baseAddress + vars.Max(v => v.Index) * 2;

            var next = ordered.SkipWhile(f => f != family).Skip(1).FirstOrDefault();
            if (next == null)
                continue;

            Assert.True(maxAddress < reg.GetModbusBase(next),
                $"family '{family}' spills into '{next}' block: max {maxAddress} >= {reg.GetModbusBase(next)}");
        }
    }

    [Fact]
    public void Registry_GetModbusBase_Unknown_Returns2()
    {
        var reg = LoadRegistry();
        Assert.Equal(2, reg.GetModbusBase("unknown_family"));
    }

    [Fact]
    public void Registry_Lookup_NullOnUnknown()
    {
        var reg = LoadRegistry();
        Assert.Null(reg.Lookup("NonExistentVariable"));
        Assert.Null(reg.LookupByAlias("flecs", "NoSuchAlias"));
        Assert.Null(reg.LookupByIndex("flecs", 999));
        Assert.Null(reg.LookupByWireName("flecs", "NoSuchWireName"));
    }

    [Fact]
    public void Registry_ExcludeFromTelemetry_FlecsIdx01()
    {
        var reg = LoadRegistry();
        var e0 = reg.LookupByIndex("flecs", 0);
        Assert.NotNull(e0);
        Assert.True(e0.Value.ExcludeFromTelemetry);

        var e1 = reg.LookupByIndex("flecs", 1);
        Assert.NotNull(e1);
        Assert.True(e1.Value.ExcludeFromTelemetry);

        var e2 = reg.LookupByIndex("flecs", 2);
        Assert.NotNull(e2);
        Assert.False(e2.Value.ExcludeFromTelemetry);
    }

    [Fact]
    public void Registry_ExcludeFromTelemetry_PlcIdx0()
    {
        var reg = LoadRegistry();
        var e0 = reg.LookupByIndex("plc", 0);
        Assert.NotNull(e0);
        Assert.True(e0.Value.ExcludeFromTelemetry);

        var e1 = reg.LookupByIndex("plc", 1);
        Assert.NotNull(e1);
        Assert.False(e1.Value.ExcludeFromTelemetry);
    }

    [Fact]
    public void Registry_Simulation_Params()
    {
        var reg = LoadRegistry();
        var entry = reg.Lookup("SlurryDensity");
        Assert.NotNull(entry);
        var sim = entry.Value.Simulation;
        Assert.NotNull(sim);
        Assert.Equal(15.80, sim.Value.Base);
        Assert.Equal(0.40, sim.Value.Amplitude);
        Assert.Equal(0.10, sim.Value.Frequency);
        Assert.Equal("f2", sim.Value.Format);
    }

    [Fact]
    public void Registry_Simulation_FormatElapsed()
    {
        var reg = LoadRegistry();
        var e0 = reg.LookupByIndex("flecs", 0);
        Assert.NotNull(e0);
        Assert.Equal("elapsed", e0.Value.Simulation?.Format);
    }

    [Fact]
    public void Registry_Simulation_FormatInt()
    {
        var reg = LoadRegistry();
        var e1 = reg.LookupByIndex("flecs", 1);
        Assert.NotNull(e1);
        Assert.Equal("int", e1.Value.Simulation?.Format);
    }

    [Fact]
    public void Registry_Families_ContainFlecsPlcLan()
    {
        var reg = LoadRegistry();
        Assert.Contains("flecs", reg.Families);
        Assert.Contains("plc", reg.Families);
        Assert.Contains("lan", reg.Families);
    }

    [Fact]
    public void FlecsCatalog_MatchesRegistry()
    {
        var reg = LoadRegistry();
        var flecs = new FlecsVariableCatalog(reg);
        var catalog = flecs.GetCatalog();
        Assert.Equal(34, catalog.Count);

        // first and last
        Assert.Equal("UnixTimestamp", catalog[0].Name);
        Assert.Equal("Time & Date", catalog[0].Description);
        Assert.Equal("VolumetricMeter", catalog[33].Name);
        Assert.Equal("VolumetricMeter", catalog[33].Description);

        // spot-check Lookup
        var r = flecs.Lookup("SlurryDensity");
        Assert.NotNull(r);
        Assert.Equal("Recirc Density", r.Value.Display);
        Assert.Equal("ppg", r.Value.Uom);
    }

    [Fact]
    public void PlcCatalog_MatchesRegistry()
    {
        var reg = LoadRegistry();
        var plc = new PlcVariableCatalog(reg);
        var catalog = plc.GetCatalog();
        Assert.Equal(10, catalog.Count);

        var r = plc.Lookup("Suction Pressure");
        Assert.NotNull(r);
        Assert.Equal("Suction Pressure", r.Value.Display);
        Assert.Equal("psi", r.Value.Uom);
    }

    [Fact]
    public void Registry_FlecsVariables_OrderedByIndex()
    {
        var reg = LoadRegistry();
        var vars = reg.GetFamilyVariables("flecs");
        for (int i = 0; i < vars.Count; i++)
            Assert.Equal(i, vars[i].Index);
    }

    [Fact]
    public void Registry_PlcVariables_OrderedByIndex()
    {
        var reg = LoadRegistry();
        var vars = reg.GetFamilyVariables("plc");
        for (int i = 0; i < vars.Count; i++)
            Assert.Equal(i, vars[i].Index);
    }

    // ── WireName resolution (Option C: dual-key registry) ──

    [Fact]
    public void Registry_LookupByWireName_MismatchedEntries_ResolveToCanonical()
    {
        var reg = LoadRegistry();

        // Wire name = spaced string; canonical = PascalCase
        var e = reg.LookupByWireName("flecs", "Mix Water Rate");
        Assert.NotNull(e);
        Assert.Equal("MixWaterRate", e.Value.Name);
        Assert.Equal("bpm", e.Value.Uom);

        e = reg.LookupByWireName("flecs", "DS Pump Press");
        Assert.NotNull(e);
        Assert.Equal("DSPumpPress", e.Value.Name);

        e = reg.LookupByWireName("flecs", "Water Stg Tot");
        Assert.NotNull(e);
        Assert.Equal("WaterStgTot", e.Value.Name);

        e = reg.LookupByWireName("flecs", "PS Pump Rate");
        Assert.NotNull(e);
        Assert.Equal("PSPumpRate", e.Value.Name);

        e = reg.LookupByWireName("flecs", "Dig out");
        Assert.NotNull(e);
        Assert.Equal("DigOut", e.Value.Name);

        e = reg.LookupByWireName("flecs", "Extra Press 1");
        Assert.NotNull(e);
        Assert.Equal("ExtraPress1", e.Value.Name);

        e = reg.LookupByWireName("flecs", "Wtr/slry Ratio");
        Assert.NotNull(e);
        Assert.Equal("WtrSlryRatio", e.Value.Name);

        e = reg.LookupByWireName("flecs", "PS Pmp Stg Tot");
        Assert.NotNull(e);
        Assert.Equal("PSPmpStgTot", e.Value.Name);
    }

    [Fact]
    public void Registry_LookupByWireName_MatchedEntries_ResolveToSelf()
    {
        var reg = LoadRegistry();

        // Wire name == canonical name (no explicit wireName in JSON)
        var e = reg.LookupByWireName("flecs", "SlurryDensity");
        Assert.NotNull(e);
        Assert.Equal("SlurryDensity", e.Value.Name);
        Assert.Equal("SlurryDensity", e.Value.WireName); // defaulted to Name

        e = reg.LookupByWireName("flecs", "PumpRate");
        Assert.NotNull(e);
        Assert.Equal("PumpRate", e.Value.Name);

        e = reg.LookupByWireName("flecs", "Temperature");
        Assert.NotNull(e);
        Assert.Equal("Temperature", e.Value.Name);

        e = reg.LookupByWireName("flecs", "VolumetricMeter");
        Assert.NotNull(e);
        Assert.Equal("VolumetricMeter", e.Value.Name);
    }

    [Fact]
    public void Registry_LookupByWireName_Plc_AllMatchCanonical()
    {
        var reg = LoadRegistry();
        var plcVars = reg.GetFamilyVariables("plc");

        foreach (var v in plcVars)
        {
            Assert.Equal(v.Name, v.WireName); // all PLC entries: wireName == canonical
            var resolved = reg.LookupByWireName("plc", v.WireName);
            Assert.NotNull(resolved);
            Assert.Equal(v.Name, resolved.Value.Name);
        }
    }

    [Fact]
    public void Registry_LookupByWireName_FlecsCount_34()
    {
        var reg = LoadRegistry();
        var flecsVars = reg.GetFamilyVariables("flecs");

        // Every flecs variable must be resolvable by its wireName
        int count = 0;
        foreach (var v in flecsVars)
        {
            var resolved = reg.LookupByWireName("flecs", v.WireName);
            Assert.NotNull(resolved);
            Assert.Equal(v.Name, resolved.Value.Name);
            count++;
        }
        Assert.Equal(34, count);
    }

    [Fact]
    public void Registry_WireName_MatchesEmulatorTemplate()
    {
        // Cross-check every flecs wireName against flecs_4.53.json references
        var reg = LoadRegistry();
        var flecsVars = reg.GetFamilyVariables("flecs");

        // Known wire names from the emulator template (index → wireName)
        var templateWireNames = new Dictionary<int, string>
        {
            [0] = "Time & Date",   [1] = "Unit Num",
            [2] = "SlurryDensity", [3] = "DH_Density",
            [4] = "Mix Water Rate",[5] = "PumpRate",
            [6] = "PumpPressure",  [7] = "DS Pump Press",
            [8] = "Water Stg Tot", [9] = "Mix Water Total",
            [10] = "Pump Stg Tot", [11] = "CombPumpTotal",
            [12] = "CementValvePsn",[13] = "WaterValvePsn",
            [14] = "PS Pump Rate", [15] = "DS Pump Rate",
            [16] = "Dig out",      [17] = "HalWin Event",
            [18] = "Temperature",  [19] = "rho12_hat",
            [20] = "h2_hat",       [21] = "vhp_density_HP",
            [22] = "TubHeight",    [23] = "Extra Press 1",
            [24] = "Extra Press 2",[25] = "Extra Rate 1",
            [26] = "Extra Total 1",[27] = "Extra Rate 2",
            [28] = "Extra Total 2",[29] = "Wtr/slry Ratio",
            [30] = "MudCup Density",[31] = "PS Pmp Stg Tot",
            [32] = "DS Pmp Stg Tot",[33] = "VolumetricMeter",
        };

        foreach (var v in flecsVars)
        {
            Assert.True(templateWireNames.TryGetValue(v.Index, out var expectedWire),
                $"Registry flecs variable '{v.Name}' has index {v.Index} not in template wire-name map");
            Assert.Equal(expectedWire, v.WireName);
        }
    }

    // ── TapName resolution (DataMonitor consumer mapping) ──

    [Fact]
    public void Registry_LookupByTapName_DisplayNames()
    {
        var reg = LoadRegistry();

        // README §4.2 RealtimeDB display names (differ from serial aliases).
        var e = reg.LookupByTapName("flecs", "Pump Pressure");
        Assert.NotNull(e);
        Assert.Equal("PumpPressure", e.Value.Name);

        e = reg.LookupByTapName("flecs", "Pump Rate");
        Assert.NotNull(e);
        Assert.Equal("PumpRate", e.Value.Name);

        e = reg.LookupByTapName("flecs", "Slurry Density");
        Assert.NotNull(e);
        Assert.Equal("SlurryDensity", e.Value.Name);
    }

    [Fact]
    public void Registry_TapName_AllFlecsResolve()
    {
        var reg = LoadRegistry();
        var flecsVars = reg.GetFamilyVariables("flecs");

        int count = 0;
        foreach (var v in flecsVars)
        {
            Assert.False(string.IsNullOrEmpty(v.TapName), $"flecs '{v.Name}' missing tapName");
            var resolved = reg.LookupByTapName("flecs", v.TapName!);
            Assert.NotNull(resolved);
            Assert.Equal(v.Name, resolved.Value.Name);
            count++;
        }
        Assert.Equal(34, count);
    }

    [Fact]
    public void Registry_LookupByTapName_NullOnUnknown()
    {
        var reg = LoadRegistry();
        Assert.Null(reg.LookupByTapName("flecs", "NoSuchTapName"));
        Assert.Null(reg.LookupByTapName("plc", "Pump Pressure"));
    }

    // ── LAN family tests ──

    [Fact]
    public void Registry_Lan_Has3463()
    {
        var reg = LoadRegistry();
        var vars = reg.GetFamilyVariables("lan");
        Assert.Equal(3463, vars.Count); // 96 legacy + 3367 lan_ext (full numeric dictionary)
    }

    [Fact]
    public void Registry_LanVariables_OrderedByIndex()
    {
        var reg = LoadRegistry();
        var vars = reg.GetFamilyVariables("lan");
        for (int i = 0; i < vars.Count; i++)
            Assert.Equal(i, vars[i].Index);
    }

    [Fact]
    public void Registry_Lan_LookupByAlias()
    {
        var reg = LoadRegistry();
        // Known signal names from Lnd Pump Sect
        var entry = reg.LookupByAlias("lan", "Pump A Pressure");
        Assert.NotNull(entry);
        Assert.Equal("Lan/PumpAPressure", entry.Value.Name);
        Assert.Equal("psi", entry.Value.Uom);
    }

    [Fact]
    public void LanCatalog_MatchesRegistry()
    {
        var reg = LoadRegistry();
        var lan = new LanVariableCatalog(reg);
        var catalog = lan.GetCatalog();
        Assert.Equal(3463, catalog.Count);

        // First LAN entry should be Recirc Density (Lnd Mix Section, index 0)
        Assert.Equal("Lan/RecircDensity", catalog[0].Name);
        Assert.Equal("Recirc Density", catalog[0].Description);

        // Lookup a specific signal
        var r = lan.Lookup("Lan/PumpAPressure");
        Assert.NotNull(r);
        Assert.Equal("Pump A Pressure", r.Value.Display);
        Assert.Equal("psi", r.Value.Uom);
        Assert.Equal(433, r.Value.ModbusOffset); // 400 + 33 (index of Pump A Pressure)
    }

    [Fact]
    public void LanCatalog_LookupAlias_AllSignalsResolve()
    {
        var reg = LoadRegistry();
        var lan = new LanVariableCatalog(reg);
        var vars = reg.GetFamilyVariables("lan");

        int count = 0;
        foreach (var v in vars)
        {
            var r = lan.Lookup(v.Name);
            Assert.NotNull(r);
            Assert.Equal(v.Alias, r.Value.Display);
            count++;
        }
        Assert.Equal(3463, count);
    }

    [Fact]
    public void Registry_Lan_WireNameResolvesToEntryWithSameWireName()
    {
        // Wire names repeat ACROSS service vectors (e.g. 'Truck Number' in many
        // SVs) — LookupByWireName is a last-wins surface. Identity can't be
        // asserted; resolution CAN: every entry's wireName resolves to SOME
        // entry carrying the same wireName (case-insensitive — the lantable
        // mixes 'N2 Flowmeter Slope'/'N2 Flowmeter slope').
        var reg = LoadRegistry();
        var vars = reg.GetFamilyVariables("lan");
        foreach (var v in vars)
        {
            var resolved = reg.LookupByWireName("lan", v.WireName);
            Assert.NotNull(resolved);
            Assert.Equal(v.WireName, resolved.Value.WireName, ignoreCase: true);
        }
    }

    // ── Wits range guard (layout v3: 600..649 is RESERVED) ──

    [Fact]
    public void Registry_GetRegisterEntriesV2_NoAddressesInWitsRange()
    {
        var reg = LoadRegistry();
        foreach (var family in new[] { "flecs", "lan" })
        {
            foreach (var e in reg.GetRegisterEntriesV2(family))
            {
                // Reserved range 600..649 — legacy zones end at 448, lan_ext
                // starts at 650: nothing may land inside it.
                Assert.False(e.Address is >= 600 and <= 649,
                    $"{family}:{e.CanonicalName} @ {e.Address} lands in the reserved wits range 600..649");
            }
        }
    }

    [Fact]
    public void Registry_ComMainAddresses_SkipStateBlock_StayInZone()
    {
        // com_main (v4 §3): the registry skips the state-block overlap 4..15 —
        // first pair = reg 16; all pairs stay inside the zone geometry 4..199
        // and never land in the reserved wits range.
        var reg = LoadRegistry();
        var entries = reg.GetRegisterEntriesV2("flecs");
        Assert.Equal(34, entries.Count);
        Assert.Equal(16, entries[0].Address);
        foreach (var e in entries)
        {
            Assert.InRange(e.Address, 16, 198);
            Assert.False(e.Address is >= 600 and <= 649);
        }
    }

    [Fact]
    public void Registry_ZoneWits_NotAddressable_Skipped()
    {
        // The wits zone is a FIXED slot table — the registry has NO zone string
        // that resolves into 600..649; an explicit "wits" zone entry is skipped.
        var json = """
            {
              "version": 1,
              "families": { "x": { "modbusBase": 4 } },
              "variables": [
                { "name": "FakeWits", "families": { "x": { "index": 0, "alias": "w", "zone": "wits" } } }
              ]
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"registry_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var reg = UnifiedVariableRegistry.Load(path);
            Assert.Empty(reg.GetRegisterEntriesV2("x"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
