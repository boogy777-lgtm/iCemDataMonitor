using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Plugins.Icem;

public class FlecsVariableCatalog : IVariableCatalog
{
    private const string Family = "flecs";
    private const int MbHeartbeat = 0;

    private readonly IReadOnlyList<VariableDefinition> _catalog;
    private readonly IReadOnlyDictionary<string, (string Display, string Uom, int? ModbusOffset)> _lookup;

    public FlecsVariableCatalog(UnifiedVariableRegistry? registry = null)
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
            int modbusOffset = r.Index + 1; // keeps current 1-34 range
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
            new("UnixTimestamp",   "Time & Date",       "",     "#888888", (MbHeartbeat + 2)  / 2, false, "catalog"),
            new("UnitNum",         "Unit Num",          "",     "#888888", (MbHeartbeat + 4)  / 2, false, "catalog"),
            new("SlurryDensity",   "Recirc Density",    "ppg",  "#CC3300", (MbHeartbeat + 6)  / 2, false, "catalog"),
            new("DH_Density",      "DH Density",        "ppg",  "#339933", (MbHeartbeat + 8)  / 2, false, "catalog"),
            new("MixWaterRate",    "Mix Water Rate",    "bpm",  "#006699", (MbHeartbeat + 10) / 2, false, "catalog"),
            new("PumpRate",        "Comb Pump Rate",    "bpm",  "#FF9933", (MbHeartbeat + 12) / 2, false, "catalog"),
            new("PumpPressure",    "PS Pump Press",     "psi",  "#634B9F", (MbHeartbeat + 14) / 2, false, "catalog"),
            new("DSPumpPress",     "DS Pump Press",     "psi",  "#FFCC05", (MbHeartbeat + 16) / 2, false, "catalog"),
            new("WaterStgTot",     "Water Stg Tot",     "bbl",  "#D86C27", (MbHeartbeat + 18) / 2, false, "catalog"),
            new("MixWaterTotal",   "Mix Water Total",   "bbl",  "#009999", (MbHeartbeat + 20) / 2, false, "catalog"),
            new("PumpStgTot",      "Pump Stg Tot",      "bbl",  "#660066", (MbHeartbeat + 22) / 2, false, "catalog"),
            new("CombPumpTotal",   "Comb Pump Total",   "bbl",  "#A1A744", (MbHeartbeat + 24) / 2, false, "catalog"),
            new("CementValvePsn",  "Cmnt Valve Psn",    "%",    "#CC3300", (MbHeartbeat + 26) / 2, false, "catalog"),
            new("WaterValvePsn",   "Water Valve Psn",   "%",    "#339933", (MbHeartbeat + 28) / 2, false, "catalog"),
            new("PSPumpRate",      "PS Pump Rate",      "bpm",  "#006699", (MbHeartbeat + 30) / 2, false, "catalog"),
            new("DSPumpRate",      "DS Pump Rate",      "bpm",  "#FF9933", (MbHeartbeat + 32) / 2, false, "catalog"),
            new("DigOut",          "Dig out",           "",     "#634B9F", (MbHeartbeat + 34) / 2, false, "catalog"),
            new("HalWinEvent",     "HalWin Event",      "",     "#FFCC05", (MbHeartbeat + 36) / 2, false, "catalog"),
            new("Temperature",     "Temperature",       "degF", "#D86C27", (MbHeartbeat + 38) / 2, false, "catalog"),
            new("rho12_hat",       "rho12_hat",         "ppg",  "#009999", (MbHeartbeat + 40) / 2, false, "catalog"),
            new("h2_hat",          "h2_hat",            "ft",   "#660066", (MbHeartbeat + 42) / 2, false, "catalog"),
            new("vhp_density_HP",  "vhp_density_HP",    "ppg",  "#A1A744", (MbHeartbeat + 44) / 2, false, "catalog"),
            new("TubHeight",       "Tub Height",        "ft",   "#CC3300", (MbHeartbeat + 46) / 2, false, "catalog"),
            new("ExtraPress1",     "Extra Press 1",     "psi",  "#339933", (MbHeartbeat + 48) / 2, false, "catalog"),
            new("ExtraPress2",     "Extra Press 2",     "psi",  "#006699", (MbHeartbeat + 50) / 2, false, "catalog"),
            new("ExtraRate1",      "Extra Rate 1",      "bpm",  "#FF9933", (MbHeartbeat + 52) / 2, false, "catalog"),
            new("ExtraTotal1",     "Extra Total 1",     "bbl",  "#634B9F", (MbHeartbeat + 54) / 2, false, "catalog"),
            new("ExtraRate2",      "Extra Rate 2",      "bpm",  "#FFCC05", (MbHeartbeat + 56) / 2, false, "catalog"),
            new("ExtraTotal2",     "Extra Total 2",     "bbl",  "#D86C27", (MbHeartbeat + 58) / 2, false, "catalog"),
            new("WtrSlryRatio",    "Wtr/slry Ratio",    "",     "#009999", (MbHeartbeat + 60) / 2, false, "catalog"),
            new("MudCupDensity",   "MudCup Density",    "ppg",  "#660066", (MbHeartbeat + 62) / 2, false, "catalog"),
            new("PSPmpStgTot",     "PS Pmp Stg Tot",    "bbl",  "#A1A744", (MbHeartbeat + 64) / 2, false, "catalog"),
            new("DSPmpStgTot",     "DS Pmp Stg Tot",    "bbl",  "#CC3300", (MbHeartbeat + 66) / 2, false, "catalog"),
            new("VolumetricMeter", "VolumetricMeter",   "bpm",  "#339933", (MbHeartbeat + 68) / 2, false, "catalog"),
        };

        // Case-insensitive: parity with the registry-built lookup (OrdinalIgnoreCase),
        // so the degraded no-registry path resolves names identically.
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
