using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Sdk;

/// <summary>
/// Per-device-family variable catalog resolution + Modbus register map building.
/// Seam = manifest-value: new family → new catalog DATA + one registry entry.
/// ¬ new class hierarchies.
/// </summary>
public interface IDeviceCatalogRegistry
{
    /// <summary>Resolve the IVariableCatalog for a device family string.</summary>
    IVariableCatalog GetCatalog(string family);

    /// <summary>
    /// Get the Modbus register base address for a device family.
    /// Default: 2 (flecs-compatible). Implementations MAY read from variable_registry.json.
    /// </summary>
    int GetModbusBase(string family);

    /// <summary>All family names defined in the variable registry.</summary>
    IEnumerable<string> GetFamilies();

    /// <summary>
    /// Ordered (by Index) register entries for a family from the variable registry.
    /// Entry name is the CANONICAL registry name (e.g., "Lan/RecircDensity", "SlurryDensity").
    /// Excludes variables marked ExcludeFromTelemetry.
    /// </summary>
    IReadOnlyList<ModbusRegisterEntry> GetRegisterEntries(string family);
}
