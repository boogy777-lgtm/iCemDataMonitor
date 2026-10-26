using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Plugins.Icem.Tests;

/// <summary>
/// lan_ext two-range addressing tests (layout v3, gate LIFTED — full numeric
/// dictionary generated). Synthetic registries pin the rule mechanics; the real
/// registry pins legacy stability of the 96 against templates\variable_registry.json.
/// </summary>
public class LanExtensionAddressingTests : IDisposable
{
    // ═══ 1. Two-range rule: idx < 96 legacy (200 + idx×2), idx ≥ 96 ext (650 + (idx−96)×2) ═══

    [Fact]
    public void TwoRangeRule_LegacyAndExtensionAddresses()
    {
        var reg = LoadSynthetic(new[] { 0, 95, 96, 97, 125, 595, 3972 });
        var entries = reg.GetRegisterEntriesV2("lan").ToDictionary(e => e.CanonicalName);

        Assert.Equal(200, entries["Lan/Sig0"].Address);   // idx 0  → 200 + 0×2
        Assert.Equal(390, entries["Lan/Sig95"].Address);  // idx 95 → 200 + 95×2
        Assert.Equal(650, entries["Lan/Sig96"].Address);  // idx 96 → 650 + 0×2
        Assert.Equal(652, entries["Lan/Sig97"].Address);  // idx 97 → 650 + 1×2
        Assert.Equal(708, entries["Lan/Sig125"].Address); // idx 125 → 650 + 29×2
        Assert.Equal(1648, entries["Lan/Sig595"].Address); // idx 595 → 650 + 499×2
        Assert.Equal(8402, entries["Lan/Sig3972"].Address); // idx 3972 → 650 + 3876×2 = LAST slot (EndReg 8403)

        // Zone tags: legacy entries stay Lan; extension entries are LanExt (v3).
        Assert.Equal(ZoneKind.Lan, entries["Lan/Sig0"].Zone);
        Assert.Equal(ZoneKind.Lan, entries["Lan/Sig95"].Zone);
        Assert.Equal(ZoneKind.LanExt, entries["Lan/Sig96"].Zone);
        Assert.Equal(ZoneKind.LanExt, entries["Lan/Sig3972"].Zone);

        // Extension addresses never land in the reserved wits range 600..649.
        foreach (var e in entries.Values)
            Assert.False(e.Address is >= 600 and <= 649);
    }

    // ═══ 2. Overflow: idx ≥ 96 + 3877 ⇒ FAIL-FAST throw (generation invariant) ═══

    [Fact]
    public void ExtensionOverflow_Throws_FailFast()
    {
        var reg = LoadSynthetic(new[] { 0, 3973, 5000 });
        var ex = Assert.Throws<InvalidOperationException>(() => reg.GetRegisterEntriesV2("lan", _ => { }));
        Assert.Contains("lan_ext capacity exceeded", ex.Message);
    }

    // ═══ 3. Legacy stability: the REAL first-96 lan entries keep 200 + idx×2 ═══

    [Fact]
    public void RealRegistry_Legacy96_AddressesStable_ThenExt()
    {
        var reg = ModbusRegistryFixture.LoadRegistry();
        var lan = reg.GetFamilyVariables("lan");
        Assert.Equal(3463, lan.Count);

        var entries = reg.GetRegisterEntriesV2("lan");
        Assert.Equal(3463, entries.Count);

        for (int i = 0; i < 96; i++)
        {
            Assert.Equal(lan[i].Name, entries[i].CanonicalName);
            Assert.Equal(ZoneDescriptor.Lan.BaseReg + i * RegisterMapV2.WordsPerFloat, entries[i].Address);
        }
    }

    // ═══ 4. Additive sv field: parsed verbatim from the JSON ═══

    [Fact]
    public void SvField_ParsedFromJson()
    {
        var reg = LoadSynthetic(new[] { 0 }, withSv: true);
        var entry = reg.Lookup("Lan/Sig0");
        Assert.NotNull(entry);
        Assert.Equal("0x13120205", entry.Value.Sv);
    }

    // ═══ 5. Non-lan families: com_main skips the v4 state block 4..15 ═══

    [Fact]
    public void ComFamily_SkipsStateBlock_FirstPairAt16()
    {
        var json = """
            {
              "version": 1,
              "families": { "flecs": { "modbusBase": 4 }, "lan": { "modbusBase": 200 } },
              "variables": [
                { "name": "SlurryDensity", "families": { "flecs": { "index": 0, "alias": "Recirc Density" } } },
                { "name": "Lan/A", "families": { "lan": { "index": 0, "alias": "A" } } }
              ]
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"lanrule_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var reg = UnifiedVariableRegistry.Load(path);
            var com = reg.GetRegisterEntriesV2("flecs");
            Assert.Single(com);
            // v4 §3: the state block 0..15 overlaps com_main 4..15 by design —
            // the com registry skips it: index 0 → reg 16 (not 4).
            Assert.Equal(16, com[0].Address);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Helpers ──

    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort */ }
        }
    }

    private UnifiedVariableRegistry LoadSynthetic(int[] indexes, bool withSv = false)
    {
        var vars = indexes.Select(idx =>
        {
            string name = $"Lan/Sig{idx}";
            string svField = withSv && idx == 0 ? ", \"sv\": \"0x13120205\"" : "";
            return $$"""
                    { "name": "{{name}}", "families": { "lan": { "index": {{idx}}, "alias": "Sig{{idx}}", "wireName": "Sig{{idx}}"{{svField}} } } }
                """.Trim();
        });

        var json = $$"""
            {
              "version": 1,
              "families": { "lan": { "modbusBase": 200 } },
              "variables": [ {{string.Join(", ", vars)}} ]
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"lanrule_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return UnifiedVariableRegistry.Load(path);
    }
}
