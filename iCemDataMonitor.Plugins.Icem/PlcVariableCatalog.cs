using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Plugins.Icem;

public class PlcVariableCatalog : IVariableCatalog
{
    private const string Family = "plc";

    private readonly IReadOnlyList<VariableDefinition> _catalog;
    private readonly IReadOnlyDictionary<string, (string Display, string Uom, int? ModbusOffset)> _lookup;

    public PlcVariableCatalog(UnifiedVariableRegistry? registry = null)
    {
        registry ??= TryLoadRegistry();
        if (registry != null)
            (_catalog, _lookup) = BuildFromRegistry(registry);
        else
            (_catalog, _lookup) = BuildFallback();
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
            int modbusOffset = 100 + r.Index; // keeps current 100-109 range
            catalog[i] = new VariableDefinition(r.Name, r.Alias, r.Uom, r.Color, modbusOffset, false, "registry");
            lookup[r.Name] = (r.Alias, r.Uom, modbusOffset);
        }
        return (catalog, lookup);
    }

    private static UnifiedVariableRegistry? TryLoadRegistry()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "templates", "variable_registry.json");
            if (!File.Exists(path)) return null;
            return UnifiedVariableRegistry.Load(path);
        }
        catch { return null; }
    }

    private static (VariableDefinition[] Catalog, IReadOnlyDictionary<string, (string, string, int?)> Lookup)
        BuildFallback()
    {
        var catalog = new VariableDefinition[]
        {
            new("PLC Timestamp",         "PLC Timestamp",         "",      "#888888", 100, false, "catalog"),
            new("PLC Status",            "PLC Status",            "",      "#888888", 101, false, "catalog"),
            new("Suction Pressure",      "Suction Pressure",      "psi",   "#CC3300", 102, false, "catalog"),
            new("Discharge Pressure",    "Discharge Pressure",    "psi",   "#339933", 103, false, "catalog"),
            new("Pump Rate",             "Pump Rate",             "bpm",   "#006699", 104, false, "catalog"),
            new("Flow Rate",             "Flow Rate",             "gpm",   "#FF9933", 105, false, "catalog"),
            new("Tank Level",            "Tank Level",            "ft",    "#634B9F", 106, false, "catalog"),
            new("Tank Temperature",      "Tank Temperature",      "degF",  "#FFCC05", 107, false, "catalog"),
            new("Motor Speed",           "Motor Speed",           "rpm",   "#D86C27", 108, false, "catalog"),
            new("Valve Position",        "Valve Position",        "%",     "#009999", 109, false, "catalog"),
        };

        // Case-insensitive: parity with the registry-built lookup (OrdinalIgnoreCase).
        var lookup = catalog.ToDictionary(v => v.Name, v => (v.Description, v.Uom, (int?)v.Position), StringComparer.OrdinalIgnoreCase);
        return (catalog, lookup);
    }

    private static readonly string[] Colors =
    {
        "#CC3300", "#339933", "#006699", "#FF9933", "#634B9F",
        "#FFCC05", "#D86C27", "#009999", "#660066", "#A1A744",
    };

    public IReadOnlyList<VariableDefinition> GetCatalog() => _catalog;

    public string GuessUom(string variableName) => VariableUnitGuesser.GuessUom(variableName);

    public string GetColor(int index) => Colors[index % Colors.Length];

    public (string Display, string Uom, int? ModbusOffset)? Lookup(string name) =>
        _lookup.TryGetValue(name, out var v) ? v : null;
}
