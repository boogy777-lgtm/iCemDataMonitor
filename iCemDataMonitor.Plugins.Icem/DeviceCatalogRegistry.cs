using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

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
    private UnifiedVariableRegistry? _modbusRegistry;

    public DeviceCatalogRegistry(UnifiedVariableRegistry? registry = null)
    {
        _registry = registry;
    }

    public IVariableCatalog GetCatalog(string family)
    {
        return family.ToLowerInvariant() switch
        {
            "flecs" => new FlecsVariableCatalog(_registry),
            "plc"   => new PlcVariableCatalog(_registry),
            "lan"   => new LanVariableCatalog(_registry),
            _       => new FallbackCatalog(),
        };
    }

    /// <summary>
    /// Resolves the Modbus register base for a device family from variable_registry.json.
    /// Lazy-loads the registry once, then caches. Falls back to 2 for unknown families.
    /// Known bases (2000-register blocks, data at base+2): flecs=2, plc=2002, unipro=4002,
    /// compupac=6002, other=8002, lan=10002, hes64=12002.
    /// </summary>
    public int GetModbusBase(string family)
    {
        EnsureRegistryLoaded();
        return _modbusRegistry?.GetModbusBase(family) ?? 2;
    }

    /// <summary>All family names defined in the variable registry.</summary>
    public IEnumerable<string> GetFamilies()
    {
        EnsureRegistryLoaded();
        return _modbusRegistry?.Families ?? Array.Empty<string>();
    }

    /// <summary>
    /// Ordered register entries for a family using canonical names from the registry.
    /// Register address = GetModbusBase(family) + Index × 2.
    /// </summary>
    public IReadOnlyList<iCemDataMonitor.Sdk.Models.ModbusRegisterEntry> GetRegisterEntries(string family)
    {
        EnsureRegistryLoaded();
        if (_modbusRegistry == null)
            return Array.Empty<iCemDataMonitor.Sdk.Models.ModbusRegisterEntry>();

        int baseOffset = _modbusRegistry.GetModbusBase(family);
        return _modbusRegistry.GetFamilyVariables(family)
            .Where(e => !e.ExcludeFromTelemetry)
            .OrderBy(e => e.Index)
            .Select(e => new iCemDataMonitor.Sdk.Models.ModbusRegisterEntry(
                e.Name, baseOffset + e.Index * 2))
            .ToList();
    }

    private void EnsureRegistryLoaded()
    {
        if (_modbusRegistry == null)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "templates", "variable_registry.json");
            if (File.Exists(path))
                _modbusRegistry = UnifiedVariableRegistry.Load(path);
        }
    }
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
