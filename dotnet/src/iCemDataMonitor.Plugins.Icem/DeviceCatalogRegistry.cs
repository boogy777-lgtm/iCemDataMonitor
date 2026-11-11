using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;
using iCemDataMonitor.Sdk.Collections;

namespace iCemDataMonitor.Plugins.Icem;

/// <summary>
/// Family → IVariableCatalog mapping.
/// flecs → FlecsVariableCatalog (34 canonical Mongo keys)
/// plc   → PlcVariableCatalog   (SSF display names + 10 emulator names)
/// lan   → LanVariableCatalog   (96 signals from 6 known ACE service vectors)
/// other → FallbackCatalog       (UOM via guesser, SSF-loaded)
/// </summary>
public class DeviceCatalogRegistry : IDeviceCatalogRegistry
{
    private readonly UnifiedVariableRegistry? _registry;
    private readonly IReadOnlyDictionary<string, IVariableCatalog> _catalogs;
    private readonly IVariableCatalog _fallback = new FallbackCatalog();
    private readonly IReadOnlyList<string> _families;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ModbusRegisterEntry>> _registerEntries;

    public DeviceCatalogRegistry(UnifiedVariableRegistry? registry = null)
        : this(registry ?? OptionalRegistryLoader.LoadDefault(), true) { }

    // The snapshot may deliberately be absent; never retry loading in catalogs/getters.
    private DeviceCatalogRegistry(UnifiedVariableRegistry? registry, bool fromSnapshot)
    {
        _registry = registry;
        _catalogs = new Dictionary<string, IVariableCatalog>(StringComparer.OrdinalIgnoreCase)
        {
            ["flecs"] = FlecsVariableCatalog.FromSnapshot(registry),
            ["plc"] = PlcVariableCatalog.FromSnapshot(registry),
            ["lan"] = LanVariableCatalog.FromSnapshot(registry),
        };
        _families = new OwnedReadOnlyList<string>(registry?.Families.ToArray() ?? Array.Empty<string>());
        _registerEntries = _families.ToDictionary(f => f, ProjectRegisterEntries, StringComparer.OrdinalIgnoreCase);
    }

    internal static DeviceCatalogRegistry FromLoader(Func<UnifiedVariableRegistry> load) =>
        new(OptionalRegistryLoader.Load(load), true);

    public IVariableCatalog GetCatalog(string family) =>
        _catalogs.TryGetValue(family, out var catalog) ? catalog : _fallback;

    /// <summary>Snapshot family base; unknown families default to 2.</summary>
    public int GetModbusBase(string family) => _registry?.GetModbusBase(family) ?? 2;

    /// <summary>Only family names declared by the snapshot.</summary>
    public IEnumerable<string> GetFamilies() => _families;

    /// <summary>Canonical telemetry entries, ordered by Index, at family base + Index × 2.</summary>
    public IReadOnlyList<ModbusRegisterEntry> GetRegisterEntries(string family) =>
        _registerEntries.TryGetValue(family, out var entries) ? entries : ProjectRegisterEntries(family);

    // Load permits variable-only families: their entries still use default base 2,
    // while GetFamilies deliberately exposes declared family metadata only.
    private IReadOnlyList<ModbusRegisterEntry> ProjectRegisterEntries(string family) =>
        new OwnedReadOnlyList<ModbusRegisterEntry>(_registry?.GetFamilyVariables(family)
            .Where(e => !e.ExcludeFromTelemetry)
            .Select(e => new ModbusRegisterEntry(e.Name, _registry.GetModbusBase(family) + e.Index * 2))
            .ToArray() ?? Array.Empty<ModbusRegisterEntry>());

}

/// <summary>
/// Generic fallback catalog for families without a dedicated catalog.
/// GuessUom via VariableUnitGuesser; Lookup returns best-effort result.
/// </summary>
internal sealed class FallbackCatalog : IVariableCatalog
{
    public IReadOnlyList<VariableDefinition> GetCatalog() => Array.Empty<VariableDefinition>();

    public string GuessUom(string variableName) => VariableUnitGuesser.GuessUom(variableName);

    public string GetColor(int index) => "#606060";

    public (string Display, string Uom, int? ModbusOffset)? Lookup(string name)
    {
        var uom = VariableUnitGuesser.GuessUom(name);
        return (name, uom, null);
    }
}
