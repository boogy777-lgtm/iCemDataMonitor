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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                  or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
