using System.Collections.Frozen;
using System.Text.Json;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Plugins.Icem;

public readonly record struct RegistrySim(double Base, double Amplitude, double Frequency, string Format);

public readonly record struct RegistryEntry(
    string Name,
    string Display,
    string Uom,
    string Color,
    string Family,
    int Index,
    string Alias,
    string WireName,
    bool ExcludeFromTelemetry,
    RegistrySim? Simulation,
    string? TapName,
    string Zone,
    string? Sv = null
);

public sealed class UnifiedVariableRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly FrozenDictionary<string, RegistryEntry> _byName;
    private readonly FrozenDictionary<string, RegistryEntry> _byAlias;   // key = "family::alias"
    private readonly FrozenDictionary<string, RegistryEntry> _byIndex;   // key = "family::index"
    private readonly FrozenDictionary<string, RegistryEntry> _byWireName; // key = "family::wireName"
    private readonly FrozenDictionary<string, RegistryEntry> _byTapName;  // key = "family::tapName"
    private readonly FrozenDictionary<string, double> _modbusBases;
    private readonly IReadOnlyList<RegistryEntry> _all;
    private readonly ILookup<string, RegistryEntry> _byFamily;

    private UnifiedVariableRegistry(
        IReadOnlyList<RegistryEntry> entries,
        IReadOnlyDictionary<string, double> modbusBases)
    {
        _all = entries;
        _byName = entries.Where(e => e.Name.Length > 0)
            .ToFrozenDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        var aliasDict = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
        var indexDict = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
        var wireNameDict = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
        var tapNameDict = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            aliasDict[$"{e.Family}::{e.Alias}"] = e;
            indexDict[$"{e.Family}::{e.Index}"] = e;
            wireNameDict[$"{e.Family}::{e.WireName}"] = e;
            if (!string.IsNullOrEmpty(e.TapName))
                tapNameDict[$"{e.Family}::{e.TapName}"] = e;
        }
        _byAlias = aliasDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _byIndex = indexDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _byWireName = wireNameDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _byTapName = tapNameDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        _modbusBases = modbusBases.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        _byFamily = entries.ToLookup(e => e.Family, StringComparer.OrdinalIgnoreCase);
    }

    public static UnifiedVariableRegistry Load(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<RegistryDocument>(json, JsonOpts)
                  ?? throw new InvalidOperationException("variable_registry.json deserialized to null");

        if (doc.Version != 1)
            throw new InvalidOperationException($"Unsupported registry version {doc.Version}");

        var modbusBases = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (doc.Families != null)
        {
            foreach (var (family, famDef) in doc.Families)
                modbusBases[family] = famDef.ModbusBase;
        }

        var entries = new List<RegistryEntry>((doc.Variables?.Length) ?? 0);
        if (doc.Variables != null)
        {
            foreach (var v in doc.Variables)
            {
                if (v.Families == null) continue;
                foreach (var (family, famInfo) in v.Families)
                {
                    entries.Add(new RegistryEntry(
                        Name: v.Name,
                        Display: v.Display,
                        Uom: v.Uom,
                        Color: v.Color,
                        Family: family,
                        Index: famInfo.Index,
                        Alias: famInfo.Alias,
                        WireName: famInfo.WireName ?? v.Name,
                        ExcludeFromTelemetry: v.ExcludeFromTelemetry,
                        Simulation: v.Simulation is { } s
                            ? new RegistrySim(s.Base, s.Amplitude, s.Frequency, s.Format ?? "f2")
                            : null,
                        TapName: famInfo.TapName,
                        Zone: ResolveZone(family, famInfo.Zone),
                        Sv: famInfo.Sv
                    ));
                }
            }
        }

        return new UnifiedVariableRegistry(entries, modbusBases);
    }

    public RegistryEntry? Lookup(string canonicalName) =>
        _byName.TryGetValue(canonicalName, out var e) ? e : null;

    public RegistryEntry? LookupByAlias(string family, string alias) =>
        _byAlias.TryGetValue($"{family}::{alias}", out var e) ? e : null;

    public RegistryEntry? LookupByIndex(string family, int index) =>
        _byIndex.TryGetValue($"{family}::{index}", out var e) ? e : null;

    public RegistryEntry? LookupByWireName(string family, string wireName) =>
        _byWireName.TryGetValue($"{family}::{wireName}", out var e) ? e : null;

    /// <summary>
    /// Resolve a raw DataMonitor name (iCem producer display name) to a registry entry.
    /// Null when no family entry declares the given tapName.
    /// </summary>
    public RegistryEntry? LookupByTapName(string family, string tapName) =>
        _byTapName.TryGetValue($"{family}::{tapName}", out var e) ? e : null;

    public IReadOnlyList<RegistryEntry> GetFamilyVariables(string family) =>
        _byFamily[family].OrderBy(e => e.Index).ToList();

    public int GetModbusBase(string family) =>
        _modbusBases.TryGetValue(family, out var val) ? (int)val : 2;

    /// <summary>
    /// Register map v2 for a family (TZ_ARCH_V2 §3): zone-assigned entries with
    /// ABSOLUTE v2 addresses = zoneBase + Index × 2 (CDAB float pair).
    /// Entries with Zone == "none" are not addressable on the bus and are skipped.
    /// Overflow (index ≥ zone float capacity) ⇒ entry truncated in registry order,
    /// one warning per dropped name via onOverflow (spec'd behavior, ¬ exception).
    ///
    /// LAN two-range rule (lan_ext, layout v3): the first LanLegacySplit lan
    /// entries keep legacy addresses (200 + idx × 2 — stable); idx ≥ 96 spills
    /// into lan_ext (650 + (idx − 96) × 2), tagged ZoneKind.LanExt. Bounds come
    /// from the zone geometry (ZoneDescriptor.Lan / ZoneDescriptor.LanExt),
    /// ¬ literals.
    ///
    /// Wits-range guard: 600..649 is RESERVED in layout v3 (wits removed — the
    /// registry MUST NOT map entries into it). lan_ext (650..1649) starts AFTER
    /// the wits range and is not affected.
    /// </summary>
    public IReadOnlyList<RegisterEntryV2> GetRegisterEntriesV2(
        string family, Action<string>? onOverflow = null)
    {
        var result = new List<RegisterEntryV2>();
        foreach (var e in _byFamily[family].OrderBy(e => e.Index))
        {
            var kind = e.Zone switch
            {
                "com" => ZoneKind.ComMain,
                "lan" => ZoneKind.Lan,
                _ => (ZoneKind?)null,
            };
            if (kind is null)
                continue;

            var (zone, addr) = AddressFor(kind.Value, e.Index, e.Name, onOverflow);
            if (addr < 0)
                continue;

            result.Add(new RegisterEntryV2(e.Name, zone, addr));
        }
        return result;
    }

    /// <summary>
    /// Legacy lan registry section size: the first 96 lan entries keep legacy
    /// addresses (addresses stable). Task-mandated two-range split point —
    /// new/extended entries start at index 96 and land in lan_ext.
    /// </summary>
    private const int LanLegacySplit = 96;

    /// <summary>
    /// com_main registry skip (contract v4 §3): the header STATE block 0..15
    /// overlaps com_main's nominal zone start 4..15 BY OPERATOR DESIGN — the
    /// com registry MUST NOT map float pairs into 4..15. First usable com_main
    /// pair = reg 16 ⇒ the registry skips the first 6 pairs (regs 4..15).
    /// </summary>
    private const int ComSkipPairs = 6;

    /// <summary>
    /// Resolve the absolute register address for one entry. Returns (−1) when the
    /// entry overflows its range (legacy zones: truncate + warn — spec'd,
    /// ¬ exception). lan_ext overflow is a GENERATION INVARIANT violation
    /// (the dictionary is sized to fit by construction) ⇒ fail-fast throw,
    /// ¬ silent truncate. Extension entries are tagged ZoneKind.LanExt.
    /// com_main entries skip the state-block overlap (v4 §3): index 0 → reg 16.
    /// </summary>
    private static (ZoneKind Zone, int Address) AddressFor(
        ZoneKind kind, int index, string name, Action<string>? onOverflow)
    {
        // Two-range lan addressing: idx ≥ 96 ⇒ lan_ext (650 + (idx−96) × 2).
        if (kind == ZoneKind.Lan && index >= LanLegacySplit)
        {
            int extIdx = index - LanLegacySplit;
            if (extIdx >= ZoneDescriptor.LanExt.FloatCapacity)
                throw new InvalidOperationException(
                    $"lan_ext capacity exceeded: '{name}' extIdx={extIdx} >= " +
                    $"{ZoneDescriptor.LanExt.FloatCapacity} — registry generation invariant broken");
            return (ZoneKind.LanExt, ZoneDescriptor.LanExt.BaseReg + extIdx * RegisterMapV2.WordsPerFloat);
        }

        var desc = ZoneDescriptor.DefaultFor(kind);

        // com_main: state-block skip (v4 §3) — the effective index starts past
        // regs 4..15; overflow is judged against the EFFECTIVE index (the pair
        // must still fit inside the zone geometry 4..199).
        int effectiveIndex = kind == ZoneKind.ComMain ? index + ComSkipPairs : index;
        if (RegisterMapV2.IsOverflow(desc, effectiveIndex))
        {
            onOverflow?.Invoke(name);
            return (kind, -1);
        }

        int addr = RegisterMapV2.AddressFor(desc, effectiveIndex);
        if (addr >= ZoneDescriptor.Wits.BaseReg && addr <= ZoneDescriptor.Wits.EndReg)
        {
            onOverflow?.Invoke(name); // reserved wits range — registry MUST NOT map there
            return (kind, -1);
        }
        return (kind, addr);
    }

    /// <summary>Zone assignment: explicit registry value wins; else inferred from family (v2 zones).</summary>
    private static string ResolveZone(string family, string? explicitZone)
    {
        if (!string.IsNullOrEmpty(explicitZone))
            return explicitZone.Trim().ToLowerInvariant();
        return family.Trim().ToLowerInvariant() switch
        {
            "flecs" => "com",
            "lan" => "lan",
            _ => "none",
        };
    }

    public int Count => _all.Count;
    public IEnumerable<string> Families => _modbusBases.Keys;
}

// ── JSON DTO types (no public surface) ──
internal sealed class RegistryDocument
{
    public int Version { get; set; }
    public Dictionary<string, RegistryFamilyDef>? Families { get; set; }
    public RegistryVariableDto[]? Variables { get; set; }
}

internal sealed class RegistryFamilyDef
{
    public double ModbusBase { get; set; }
}

internal sealed class RegistryVariableDto
{
    public string Name { get; set; } = "";
    public string Display { get; set; } = "";
    public string Uom { get; set; } = "";
    public string Color { get; set; } = "";
    public Dictionary<string, RegistryFamilyEntry>? Families { get; set; }
    public RegistrySimDto? Simulation { get; set; }
    public bool ExcludeFromTelemetry { get; set; }
}

internal sealed class RegistryFamilyEntry
{
    public string Alias { get; set; } = "";
    public int Index { get; set; }
    public string? WireName { get; set; }
    public string? TapName { get; set; }
    public string? Zone { get; set; }
    /// <summary>
    /// Optional service-vector number ("0x........" hex string) disambiguating
    /// (sv, wireName) pairs. Additive field — entries without it keep the
    /// wireName-only key contract (legacy 96 lan entries).
    /// </summary>
    public string? Sv { get; set; }
}

internal sealed class RegistrySimDto
{
    public double Base { get; set; }
    public double Amplitude { get; set; }
    public double Frequency { get; set; }
    public string? Format { get; set; }
}

