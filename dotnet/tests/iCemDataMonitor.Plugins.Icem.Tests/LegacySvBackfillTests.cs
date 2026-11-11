using System.Globalization;
using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Plugins.Icem.Tests;

/// <summary>
/// debt_2 coverage: legacy lan entries carry BACKFILLED sv fields resolved
/// against the lantable. Every backfilled (sv, wireName) pair matches exactly
/// ONE numeric signal in the live Lnd sv range (0x131202xx — unambiguous by
/// construction); legacy addresses stay 200 + idx × 2 (stable); the 4 wireNames
/// without any numeric dictionary signal stay sv-less (wireName-keyed).
/// </summary>
public class LegacySvBackfillTests
{
    private const uint LndSvMask = 0xFFFFFF00;
    private const uint LndSvRange = 0x13120200;

    [Fact]
    public void Legacy96_HaveBackfilledSv_ResolvedInLndRange()
    {
        var reg = ModbusRegistryFixture.LoadRegistry();
        var legacy = reg.GetFamilyVariables("lan").Where(v => v.Index < 96).ToList();
        Assert.Equal(96, legacy.Count);

        var lantable = LantableRegistry.Load(
            Path.Combine(AppContext.BaseDirectory, "templates", "lantable_registry.json"));

        var withoutSv = new List<string>();
        int backfilled = 0;
        foreach (var v in legacy)
        {
            if (v.Sv is not { Length: > 0 } sv)
            {
                withoutSv.Add(v.WireName);
                continue;
            }
            backfilled++;
            uint svNum = uint.Parse(sv.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            // In the live Lnd sv range.
            Assert.Equal(LndSvRange, svNum & LndSvMask);

            // The lantable carries exactly one numeric signal with this
            // wireName in the 0x131202xx range (unambiguous resolution).
            int matches = 0;
            foreach (var svDef in lantable.ServiceVectors.Where(d => (d.SvNumber & LndSvMask) == LndSvRange))
            {
                foreach (var s in svDef.Signals)
                {
                    if (s.ValueType is "UC" or "HX")
                        continue; // numeric only — UC/HX are text (excluded dictionary)
                    if (string.Equals(s.Name, v.WireName, StringComparison.OrdinalIgnoreCase))
                        matches++;
                }
            }
            Assert.Equal(1, matches);
        }

        Assert.Equal(92, backfilled);
        // The 4 sv-less wireNames have NO numeric dictionary signal at all
        // (text/absent) — pin the exact set so a dictionary change surfaces here.
        Assert.Equal(new[]
        {
            "Auto or Manual Mix", "Cmnt Ops Track Descript",
            "Side A Kicked Out?", "Side B Kicked Out?",
        }, withoutSv.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void LegacyAddresses_Stable_AfterBackfill()
    {
        var reg = ModbusRegistryFixture.LoadRegistry();
        var entries = reg.GetRegisterEntriesV2("lan").Take(96).ToList();

        for (int i = 0; i < entries.Count; i++)
        {
            Assert.Equal(ZoneKind.Lan, entries[i].Zone);
            Assert.Equal(ZoneDescriptor.Lan.BaseReg + i * RegisterMapV2.WordsPerFloat, entries[i].Address);
        }
    }
}
