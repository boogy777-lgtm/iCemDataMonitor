using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Plugins.Icem;

/// <summary>
/// LAN family catalog — signals from the 6 known ACE service vectors
/// (Lnd Mix Section, Lnd Automation, Lnd Pump Sect, Lnd RAWIO Data,
///  Land Addon IO, Lnd Ops Track).
/// Implements IVariableCatalog. Sources data from UnifiedVariableRegistry
/// (variable_registry.json "lan" family entries). Sentinel -9999 is
/// handled by LantableRegistry.DecodeSignals — this catalog returns
/// metadata only; value decode lives there.
/// </summary>
public class LanVariableCatalog : IVariableCatalog
{
    private const string Family = "lan";
    private const int MbBase = 400;

    private readonly IReadOnlyList<VariableDefinition> _catalog;
    private readonly IReadOnlyDictionary<string, (string Display, string Uom, int? ModbusOffset)> _lookup;

    public LanVariableCatalog(UnifiedVariableRegistry? registry = null)
        : this(registry ?? OptionalRegistryLoader.LoadDefault(), true) { }

    internal static LanVariableCatalog FromSnapshot(UnifiedVariableRegistry? registry) => new(registry, true);

    private LanVariableCatalog(UnifiedVariableRegistry? registry, bool fromSnapshot)
    {
        if (registry != null)
            (_catalog, _lookup) = BuildFromRegistry(registry);
        else
            (_catalog, _lookup) = BuildFallback();
        _catalog = Array.AsReadOnly(_catalog.ToArray());
    }

    private (VariableDefinition[], IReadOnlyDictionary<string, (string, string, int?)>) BuildFromRegistry(
        UnifiedVariableRegistry reg)
    {
        var vars = reg.GetFamilyVariables(Family);
        var catalog = new VariableDefinition[vars.Count];
        var lookup = new Dictionary<string, (string, string, int?)>(vars.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < vars.Count; i++)
        {
            var r = vars[i];
            int modbusOffset = MbBase + r.Index;
            catalog[i] = new VariableDefinition(r.Name, r.Alias, r.Uom, r.Color, modbusOffset, false, "registry");
            lookup[r.Name] = (r.Alias, r.Uom, modbusOffset);
        }
        return (catalog, lookup);
    }

    private static (VariableDefinition[] Catalog, IReadOnlyDictionary<string, (string, string, int?)> Lookup)
        BuildFallback()
    {
        // Minimal fallback: 6 known service vectors, key signals only
        var catalog = new VariableDefinition[]
        {
            new("RecircDensity",         "Recirc Density",        "ppg",  "#E65100", MbBase + 0,  false, "catalog"),
            new("RecircFlowRate",        "Recirc Flow Rate",      "bpm",  "#00796B", MbBase + 1,  false, "catalog"),
            new("PumpAPressure",         "Pump A Pressure",       "psi",  "#283593", MbBase + 33, false, "catalog"),
            new("OPKOASetting",          "OPKO A Setting",        "psi",  "#558B2F", MbBase + 35, false, "catalog"),
            new("PumpARate",             "Pump A Rate",           "bpm",  "#BF360C", MbBase + 38, false, "catalog"),
            new("UnitNumber",            "Unit Number",           "",     "#4A148C", MbBase + 27, false, "catalog"),
            new("ControlMode",           "Control Mode",          "",     "#004D40", MbBase + 28, false, "catalog"),
            new("MixMode",               "Mix Mode",              "",     "#880E4F", MbBase + 29, false, "catalog"),
        };
        // Case-insensitive: parity with the registry-built lookup (OrdinalIgnoreCase).
        var lookup = catalog.ToDictionary(v => v.Name, v => (v.Description, v.Uom, (int?)v.Position), StringComparer.OrdinalIgnoreCase);
        return (catalog, lookup);
    }

    private static readonly string[] Colors =
    {
        "#E65100", "#00796B", "#283593", "#558B2F", "#BF360C",
        "#4A148C", "#004D40", "#880E4F", "#1A237E", "#33691E",
        "#B71C1C", "#311B92", "#00695C", "#827717", "#3E2723",
        "#0D47A1", "#1B5E20", "#F57F17", "#212121", "#01579B",
    };

    public IReadOnlyList<VariableDefinition> GetCatalog() => _catalog;

    public string GuessUom(string variableName) => VariableUnitGuesser.GuessUom(variableName);

    public string GetColor(int index) => Colors[index % Colors.Length];

    public (string Display, string Uom, int? ModbusOffset)? Lookup(string name) =>
        _lookup.TryGetValue(name, out var v) ? v : null;
}
