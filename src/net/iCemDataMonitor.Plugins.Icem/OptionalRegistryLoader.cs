using System.Text.Json;

namespace iCemDataMonitor.Plugins.Icem;

/// <summary>Optional deployment data. Strict UnifiedVariableRegistry.Load remains unchanged.</summary>
internal static class OptionalRegistryLoader
{
    internal static UnifiedVariableRegistry? LoadDefault() => Load(() =>
        UnifiedVariableRegistry.Load(Path.Combine(AppContext.BaseDirectory, "templates", "variable_registry.json")));

    internal static UnifiedVariableRegistry? Load(Func<UnifiedVariableRegistry> load)
    {
        try { return load(); }
        // Strict Load also dereferences required DTO nodes: JSON null there is malformed
        // optional deployment data. Keep that compatibility handling at this boundary.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                  or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            return null;
        }
    }
}
