using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Data;

/// <summary>
/// Delegating IVariableCatalog adapter that resolves the correct per-family catalog
/// via IDeviceCatalogRegistry based on the currently active configuration family.
///
/// Active family is resolved per-call from IConfigStore (lazy via IServiceProvider).
/// Falls back to "flecs" when config store is not yet available (startup construction).
/// O(1) per call — no cache, no invalidation state.
/// </summary>
public sealed class FamilyAwareCatalog : IVariableCatalog
{
    private readonly IDeviceCatalogRegistry _registry;
    private readonly IServiceProvider _sp;

    public FamilyAwareCatalog(IDeviceCatalogRegistry registry, IServiceProvider sp)
    {
        _registry = registry;
        _sp = sp;
    }

    private IVariableCatalog Current
    {
        get
        {
            var activeFamily = ResolveActiveFamily();
            return _registry.GetCatalog(activeFamily);
        }
    }

    private string ResolveActiveFamily()
    {
        try
        {
            var config = _sp.GetService(typeof(IConfigStore)) as IConfigStore;
            if (config is null)
                return "flecs";

            var active = config.GetActiveConfig();
            if (active is null)
                return "flecs";

            // Search which family the active config belongs to
            var families = config.GetAllFamilies();
            foreach (var f in families)
            {
                if (f.Configs.Any(c =>
                    string.Equals(c.Name, active.Value.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return f.Name;
                }
            }
        }
        catch
        {
            // Config store not yet available (e.g. during startup construction).
            // Fall through to default.
        }

        return "flecs";
    }

    public IReadOnlyList<VariableDefinition> GetCatalog() => Current.GetCatalog();

    public string GuessUom(string variableName) => Current.GuessUom(variableName);

    public string GetColor(int index) => Current.GetColor(index);

    public (string Display, string Uom, int? ModbusOffset)? Lookup(string name) =>
        Current.Lookup(name);
}
