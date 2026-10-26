using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Plugins.Icem.Tests;

/// <summary>
/// Full-dictionary coverage (gate-lifted generation, operator-binding numbers):
/// every numeric lantable signal inside the 124 B ACE payload that is not the
/// legacy-mapped first occurrence of a legacy wireName has EXACTLY ONE registry
/// entry keyed by (sv, wireName); every legacy address is unchanged; extension
/// addresses follow 650 + (idx − 96) × 2 with ZoneKind.LanExt; overflow is
/// impossible (fail-fast invariant, ¬ silent truncate).
/// </summary>
public class LanFullCoverageTests
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "FF", "UL", "SL", "SI", "FD", "UI", "US", "SS", "SC",
    };

    private const int PayloadBytes = 124;   // ACE payload law (AceFrameBuilder.PayloadSize)
    private const int LanLegacySplit = 96;  // two-range split (pinned in UnifiedVariableRegistry)

    private static readonly (uint Sv, int Off, string Name)[] LantableSignals = LoadLantableSignals();

    private static (uint Sv, int Off, string Name)[] LoadLantableSignals()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "templates", "lantable_registry.json");
        Assert.True(File.Exists(path), $"lantable_registry.json not found at {path}");
        var lantable = LantableRegistry.Load(path);
        // Runtime law: FIRST vector definition per sv number wins — the 19
        // duplicate entries (4 sv numbers) are dead at runtime, so the registry
        // MUST NOT map signals that only their dropped definitions declare.
        Assert.Equal(194, lantable.Count);

        var sigs = new List<(uint, int, string)>();
        int numeric = 0, text = 0, unmappable = 0;
        foreach (var sv in lantable.ServiceVectors)
        {
            foreach (var s in sv.Signals)
            {
                if (!NumericTypes.Contains(s.ValueType))
                {
                    text++;
                    continue;
                }
                numeric++;
                if (s.ByteOffset + s.Size > PayloadBytes)
                {
                    unmappable++;
                    continue;
                }
                sigs.Add((sv.SvNumber, s.ByteOffset, s.Name));
            }
        }

        Assert.Equal(3518, numeric);  // operator-binding dictionary math (runtime-deduped: raw JSON 3973 − 455 phantom)
        Assert.Equal(411, text);
        Assert.Equal(25, unmappable); // all sit at ByteOffset=124, Size=4 ⇒ 128 > 124
        sigs.Sort((a, b) =>
        {
            int bySv = a.Item1.CompareTo(b.Item1);
            return bySv != 0 ? bySv : a.Item2.CompareTo(b.Item2);
        });
        return sigs.ToArray();
    }

    [Fact]
    public void EveryNumericSignal_HasExactlyOneRegistryEntry()
    {
        var reg = ModbusRegistryFixture.LoadRegistry();
        var lan = reg.GetFamilyVariables("lan");

        // Legacy 96: indexes 0..95. debt_2 backfilled sv on every entry with a
        // numeric lantable signal (92 of 96 — the other 4 wireNames are
        // text/absent in the dictionary).
        var legacy = lan.Where(v => v.Index < LanLegacySplit).ToList();
        Assert.Equal(96, legacy.Count);
        Assert.Equal(92, legacy.Count(v => v.Sv is { Length: > 0 }));

        var legacyWires = new HashSet<string>(legacy.Select(v => v.WireName), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(96, legacyWires.Count);

        // Legacy marking = the BACKFILLED (Sv, WireName) pairs — the legacy
        // slot is owned by the live Lnd source sv, ¬ the sv-asc first
        // occurrence (0x131201xx echo vectors go to lan_ext instead).
        var legacyMarked = new HashSet<(uint, string)>();
        foreach (var v in legacy.Where(v => v.Sv is { Length: > 0 }))
        {
            uint sv = uint.Parse(v.Sv!.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
            legacyMarked.Add((sv, v.WireName));
        }
        Assert.Equal(92, legacyMarked.Count);

        // Expected extension set: mappable numeric, not legacy-marked,
        // deduped by (sv, wireName) first-wins.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sv, off, name) in LantableSignals)
        {
            if (legacyMarked.Contains((sv, name)))
                continue;
            if (seen.Add($"{sv}::{name}"))
                expected.Add($"{SvHex(sv)}::{name}");
        }
        Assert.Equal(3367, expected.Count); // 3518 − 92 legacy − 34 dups − 25 unmappable

        // Registry extension: exactly the expected set, one entry per (sv, wireName).
        var ext = lan.Where(v => v.Index >= LanLegacySplit).ToList();
        Assert.Equal(expected.Count, ext.Count);

        var extByKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in ext)
        {
            Assert.NotNull(v.Sv);
            Assert.True(extByKey.Add($"{v.Sv}::{v.WireName}"),
                $"duplicate ext key {v.Sv}::{v.WireName} (registry generation must dedupe)");
        }
        Assert.True(expected.SetEquals(extByKey),
            $"expected={expected.Count} ext keys, registry has {extByKey.Count}; " +
            $"missing={expected.Except(extByKey).Take(5).Aggregate("", (a, s) => a + s + " ")} " +
            $"extra={extByKey.Except(expected).Take(5).Aggregate("", (a, s) => a + s + " ")}");

        // No ext entry duplicates a legacy-mapped (sv, wireName) pair.
        foreach (var (sv, name) in legacyMarked)
            Assert.DoesNotContain($"{SvHex(sv)}::{name}", extByKey);

        // Canonical names are unique across the whole lan family.
        var names = new HashSet<string>(lan.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(lan.Count, names.Count);
    }

    [Fact]
    public void EveryRegistryEntry_HasDeterministicAddress()
    {
        var reg = ModbusRegistryFixture.LoadRegistry();
        var entries = reg.GetRegisterEntriesV2("lan"); // must not throw (no overflow)
        Assert.Equal(3463, entries.Count);

        var lan = reg.GetFamilyVariables("lan");
        for (int i = 0; i < LanLegacySplit; i++)
        {
            var e = entries[i];
            Assert.Equal(ZoneKind.Lan, e.Zone);
            Assert.Equal(ZoneDescriptor.Lan.BaseReg + i * RegisterMapV2.WordsPerFloat, e.Address);
            Assert.Equal(lan[i].Name, e.CanonicalName);
        }

        for (int i = LanLegacySplit; i < entries.Count; i++)
        {
            var e = entries[i];
            Assert.Equal(ZoneKind.LanExt, e.Zone);
            Assert.Equal(ZoneDescriptor.LanExt.BaseReg + (i - LanLegacySplit) * RegisterMapV2.WordsPerFloat, e.Address);
        }

        // Last slot: idx 3462 → 650 + 3366 × 2 = 7382 (3877-slot zone: 510 spare at the tail).
        Assert.Equal(7382, entries[^1].Address);
        Assert.True(entries[^1].Address + 1 <= ZoneDescriptor.LanExt.EndReg);
    }

    [Fact]
    public void ExtensionOverflow_IsImpossible_WithRealRegistry()
    {
        // The zone holds 3877 floats; the dictionary uses 3367. GetRegisterEntriesV2
        // fails FAST on overflow — with the real registry it must simply not throw.
        var reg = ModbusRegistryFixture.LoadRegistry();
        var entries = reg.GetRegisterEntriesV2("lan", name => Assert.Fail($"unexpected overflow: {name}"));
        Assert.Equal(3463, entries.Count);
    }

    private static string SvHex(uint sv) => $"0x{sv:X8}";
}
