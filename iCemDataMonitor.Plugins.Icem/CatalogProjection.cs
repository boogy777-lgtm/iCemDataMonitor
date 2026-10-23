using System.Collections.Frozen;
using iCemDataMonitor.Sdk.Models;
using iCemDataMonitor.Sdk.Collections;

namespace iCemDataMonitor.Plugins.Icem;

/// <summary>Shared metadata projection; family data and offset/palette policies stay in the wrappers.</summary>
internal sealed class CatalogProjection
{
    private readonly IReadOnlyList<VariableDefinition> _catalog;
    private readonly FrozenDictionary<string, (string Display, string Uom, int? ModbusOffset)> _lookup;
    private readonly string[] _colors;

    private CatalogProjection(IEnumerable<VariableDefinition> entries, IReadOnlyList<string> colors)
    {
        _catalog = new OwnedReadOnlyList<VariableDefinition>(entries.ToArray());
        var lookup = new Dictionary<string, (string Display, string Uom, int? ModbusOffset)>(StringComparer.OrdinalIgnoreCase);
        // Preserve legacy last-write-wins for entries accepted by the strict registry parser.
        foreach (var entry in _catalog)
            lookup[entry.Name] = (entry.Description, entry.Uom, entry.Position);
        _lookup = lookup.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _colors = colors.ToArray();
    }

    internal static CatalogProjection Create(UnifiedVariableRegistry? registry, string family,
        int offset, Func<IReadOnlyList<VariableDefinition>> fallback, IReadOnlyList<string> colors) =>
        new(registry is null ? fallback() : registry.GetFamilyVariables(family)
            .Select(r => new VariableDefinition(r.Name, r.Alias, r.Uom, r.Color, offset + r.Index, false, "registry")), colors);

    internal IReadOnlyList<VariableDefinition> GetCatalog() => _catalog;
    internal string GuessUom(string variableName) => VariableUnitGuesser.GuessUom(variableName);
    internal string GetColor(int index) => _colors[index % _colors.Length];
    internal (string Display, string Uom, int? ModbusOffset)? Lookup(string name) =>
        _lookup.TryGetValue(name, out var value) ? value : null;
}
