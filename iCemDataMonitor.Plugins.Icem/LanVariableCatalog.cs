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

    private readonly CatalogProjection _projection;

    public LanVariableCatalog(UnifiedVariableRegistry? registry = null)
        : this(registry ?? OptionalRegistryLoader.LoadDefault(), true) { }

    internal static LanVariableCatalog FromSnapshot(UnifiedVariableRegistry? registry) => new(registry, true);

    private LanVariableCatalog(UnifiedVariableRegistry? registry, bool fromSnapshot)
    {
        _projection = CatalogProjection.Create(registry, Family, MbBase, BuildFallback, Colors);
    }

    private static VariableDefinition[] BuildFallback()
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
        return catalog;
    }

    private static readonly string[] Colors =
    {
        "#E65100", "#00796B", "#283593", "#558B2F", "#BF360C",
        "#4A148C", "#004D40", "#880E4F", "#1A237E", "#33691E",
        "#B71C1C", "#311B92", "#00695C", "#827717", "#3E2723",
        "#0D47A1", "#1B5E20", "#F57F17", "#212121", "#01579B",
    };

    public IReadOnlyList<VariableDefinition> GetCatalog() => _projection.GetCatalog();

    public string GuessUom(string variableName) => _projection.GuessUom(variableName);

    public string GetColor(int index) => _projection.GetColor(index);

    public (string Display, string Uom, int? ModbusOffset)? Lookup(string name) => _projection.Lookup(name);
}
