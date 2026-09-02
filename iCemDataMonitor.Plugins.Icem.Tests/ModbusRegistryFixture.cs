using System.Text.Json;

namespace iCemDataMonitor.Plugins.Icem.Tests;

/// <summary>
/// Shared Modbus-map test fixture. Expected modbusBase values are loaded from
/// templates\variable_registry.json — the SAME file (and the same content-link
/// mechanism) the production DeviceCatalogRegistry/UnifiedVariableRegistry consume.
/// No hardcoded base addresses in any test ⇒ future layout changes are data-only.
/// </summary>
internal static class ModbusRegistryFixture
{
    /// <summary>
    /// 7-family × 2000-register block layout. Ordinal defines the block position:
    /// blockBase = ordinal × 2000; data registers start at blockBase + 4;
    /// heartbeat register sits at blockBase + 2.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> FamilyOrdinals =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["flecs"] = 0,
            ["plc"] = 1,
            ["unipro"] = 2,
            ["compupac"] = 3,
            ["other"] = 4,
            ["lan"] = 5,
            ["hes64"] = 6,
        };

    public static string ResolveVariableRegistryPath()
    {
        // templates\variable_registry.json is content-linked into the test output
        // (mirrors the production project's <Content ... Link> mechanism).
        var path = Path.Combine(AppContext.BaseDirectory, "templates", "variable_registry.json");
        Assert.True(File.Exists(path), $"variable_registry.json not found at {path}");
        return path;
    }

    public static UnifiedVariableRegistry LoadRegistry() =>
        UnifiedVariableRegistry.Load(ResolveVariableRegistryPath());

    /// <summary>family → modbusBase, read directly from the source-of-truth file.</summary>
    public static Dictionary<string, int> LoadExpectedModbusBases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(ResolveVariableRegistryPath()));
        var bases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.GetProperty("families").EnumerateObject())
            bases[prop.Name] = prop.Value.GetProperty("modbusBase").GetInt32();
        return bases;
    }
}
