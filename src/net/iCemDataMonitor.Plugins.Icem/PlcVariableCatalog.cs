using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Plugins.Icem;

public class PlcVariableCatalog : IVariableCatalog
{
    private const string Family = "plc";

    private readonly CatalogProjection _projection;

    public PlcVariableCatalog(UnifiedVariableRegistry? registry = null)
        : this(registry ?? OptionalRegistryLoader.LoadDefault(), true) { }

    internal static PlcVariableCatalog FromSnapshot(UnifiedVariableRegistry? registry) => new(registry, true);

    private PlcVariableCatalog(UnifiedVariableRegistry? registry, bool fromSnapshot)
    {
        _projection = CatalogProjection.Create(registry, Family, 100, BuildFallback, Colors);
    }

    private static VariableDefinition[] BuildFallback()
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

        return catalog;
    }

    private static readonly string[] Colors =
    {
        "#CC3300", "#339933", "#006699", "#FF9933", "#634B9F",
        "#FFCC05", "#D86C27", "#009999", "#660066", "#A1A744",
    };

    public IReadOnlyList<VariableDefinition> GetCatalog() => _projection.GetCatalog();

    public string GuessUom(string variableName) => _projection.GuessUom(variableName);

    public string GetColor(int index) => _projection.GetColor(index);

    public (string Display, string Uom, int? ModbusOffset)? Lookup(string name) => _projection.Lookup(name);
}
