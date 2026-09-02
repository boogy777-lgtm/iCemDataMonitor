using System.Text.Json;
using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Data;

public class InMemoryConfigStore : IConfigStore
{
    private readonly IVariableCatalog _catalog;
    private readonly IDeviceCatalogRegistry? _registry;
    private readonly string? _statePath;
    private readonly List<DeviceFamily> _families;
    private string _activeFamily = "flecs";
    private string _activeConfigName = "flecs_ver4p53_ppg_bpm_psi";
    private List<VariableDefinition> _activeVariables = new();
    private List<ModbusRegisterEntry> _combinedModbusMap = new();
    private HashSet<string> _liveMongoNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public InMemoryConfigStore(IVariableCatalog catalog, string? statePath = null, IDeviceCatalogRegistry? registry = null)
    {
        _catalog = catalog;
        _registry = registry;
        _statePath = statePath;
        _families = SsfTemplateLoader.AllFamilies().ToList();
        LoadState();
        _combinedModbusMap = BuildCombinedModbusMap();
        _ = RebuildAsync();
    }

    public IReadOnlyList<DeviceFamily> GetAllFamilies() => _families;

    public DeviceConfig? GetActiveConfig()
    {
        var family = _families.FirstOrDefault(f =>
            string.Equals(f.Name, _activeFamily, StringComparison.OrdinalIgnoreCase));
        if (family.Name is null) return null;
        var config = family.Configs.FirstOrDefault(c =>
            string.Equals(c.Name, _activeConfigName, StringComparison.OrdinalIgnoreCase));
        return config;
    }

    public IReadOnlyList<VariableDefinition> GetActiveVariables()
    {
        lock (_lock) { return _activeVariables.ToList(); }
    }

    public async Task ActivateConfigAsync(string family, string configName)
    {
        _activeFamily = family;
        _activeConfigName = configName;
        await RebuildAsync();
        SaveState();
    }

    /// <summary>
    /// Returns a COMBINED register map for ALL families from the variable registry.
    /// Data always flows from all sources simultaneously — this map is independent
    /// of the web UI "active config" (which only controls display filtering).
    /// </summary>
    public IReadOnlyList<ModbusRegisterEntry> GetModbusRegisterMap()
    {
        lock (_lock) { return _combinedModbusMap.ToList(); }
    }

    public async Task RefreshFromLiveAsync(IEnumerable<string> mongoVariableNames)
    {
        _liveMongoNames = new HashSet<string>(mongoVariableNames, StringComparer.OrdinalIgnoreCase);
        await RebuildAsync();
    }

    private Task RebuildAsync()
    {
        var family = _families.FirstOrDefault(f =>
            string.Equals(f.Name, _activeFamily, StringComparison.OrdinalIgnoreCase));
        if (family.Name is null)
            return Task.CompletedTask;

        var config = family.Configs.FirstOrDefault(c =>
            string.Equals(c.Name, _activeConfigName, StringComparison.OrdinalIgnoreCase));
        if (config.Name is null)
            return Task.CompletedTask;

        // F13: exclude both SSF display names AND canonical names.
        // Hardcoded list is the fallback; when registry catalog exposes ExcludeFromTelemetry,
        // it takes precedence. Current IVariableCatalog.Lookup returns null for excluded
        // entries when the parallel UnifiedVariableRegistry path is active.
        var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Time & Date", "Unit Num", "UnixTimestamp", "UnitNum"
        };

        var templateVars = config.Variables
            .Where(v => !v.IsSpare)
            .Where(v => !excludedNames.Contains(v.Name))
            .ToList();

        // G2: resolve per-family catalog when registry is wired; fallback to injected default
        var effectiveCatalog = _registry?.GetCatalog(_activeFamily) ?? _catalog;

        // F3/F4: 2-dictionary bridge — canonical name → catalog metadata + display→canonical reverse
        var canonicalToCatalog = new Dictionary<string, (string Display, string Uom, int? Pos, string Color)>(StringComparer.OrdinalIgnoreCase);
        var displayToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Snapshot live names, filtering out excluded canonical names
        var filteredLiveNames = new HashSet<string>(_liveMongoNames, StringComparer.OrdinalIgnoreCase);
        filteredLiveNames.RemoveWhere(n => excludedNames.Contains(n));

        foreach (var mn in filteredLiveNames)
        {
            var lookup = effectiveCatalog.Lookup(mn);
            if (lookup is not null)
            {
                var (display, uom, pos) = lookup.Value;
                var catalogVars = effectiveCatalog.GetCatalog();
                var catColor = catalogVars.FirstOrDefault(cv =>
                    string.Equals(cv.Name, mn, StringComparison.OrdinalIgnoreCase)).Color;
                if (string.IsNullOrEmpty(catColor)) catColor = "#606060";

                canonicalToCatalog[mn] = (display, uom, pos, catColor);
                if (!string.IsNullOrEmpty(display) && !excludedNames.Contains(display))
                    displayToCanonical[display] = mn;
            }
        }

        // Build template name index for bridge matching
        var templateNameSet = new HashSet<string>(
            templateVars.Select(tv => tv.Name), StringComparer.OrdinalIgnoreCase);

        // Determine which live names match which template vars (via direct or bridge)
        var liveMatchedToTemplate = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // live names already paired
        var templateMatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // template names already paired

        // Map: template var name → canonical live name for metadata enrichment
        var templateCanonicalPair = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tv in templateVars)
        {
            // Direct match: live canonical name equals template display name
            if (filteredLiveNames.Contains(tv.Name))
            {
                liveMatchedToTemplate.Add(tv.Name);
                templateMatched.Add(tv.Name);
                templateCanonicalPair[tv.Name] = tv.Name;
            }
            // Catalog bridge: canonical name → display matches template name
            else if (displayToCanonical.TryGetValue(tv.Name, out var canonical))
            {
                liveMatchedToTemplate.Add(canonical);
                templateMatched.Add(tv.Name);
                templateCanonicalPair[tv.Name] = canonical;
            }
        }

        var palette = new[] { "#CC3300","#339933","#006699","#FF9933","#634B9F",
            "#FFCC05","#D86C27","#009999","#660066","#A1A744" };
        var result = new List<VariableDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ci = 0;

        // Pass 1: template ∩ live (template metadata wins, "template" source).
        // B2 FIX: emit the LIVE canonical name as Name so the dashboard join (by name) works.
        // Description keeps the template/SSF display label. seen tracks the emitted canonical name.
        foreach (var tv in templateVars)
        {
            if (templateMatched.Contains(tv.Name))
            {
                var paletteColor = palette[ci % palette.Length];
                // Enrich with catalog color if available
                if (templateCanonicalPair.TryGetValue(tv.Name, out var can)
                    && canonicalToCatalog.TryGetValue(can, out var catMeta)
                    && !string.IsNullOrEmpty(catMeta.Color))
                    paletteColor = catMeta.Color;

                var liveName = templateCanonicalPair.GetValueOrDefault(tv.Name, tv.Name);
                seen.Add(liveName);
                result.Add(tv with { Name = liveName, Color = paletteColor, Source = "template" });
                ci++;
            }
        }

        // Pass 2: live MongoDB vars NOT matched to any template
        // F5: use catalog metadata when available — ¬ Description=Name
        foreach (var mn in filteredLiveNames)
        {
            if (liveMatchedToTemplate.Contains(mn))
                continue;

            if (seen.Add(mn))
            {
                if (canonicalToCatalog.TryGetValue(mn, out var catMeta))
                {
                    var (display, uom, pos, color) = catMeta;
                    var desc = string.IsNullOrEmpty(display) ? mn : display;
                    var finalUom = string.IsNullOrEmpty(uom) ? VariableUnitGuesser.GuessUom(mn) : uom;
                    var finalColor = string.IsNullOrEmpty(color) ? palette[ci % palette.Length] : color;
                    var finalPos = pos ?? 999;
                    result.Add(new VariableDefinition(mn, desc, finalUom, finalColor, finalPos, false, "mongodb"));
                }
                else
                {
                    var uom = VariableUnitGuesser.GuessUom(mn);
                    result.Add(new VariableDefinition(mn, mn, uom,
                        palette[ci % palette.Length], 999, false, "mongodb"));
                }
                ci++;
            }
        }

        // Pass 3: template vars NOT in live MongoDB.
        // Guard: skip vars already emitted in Pass 1 (templateMatched). Since seen now tracks
        // canonical names, a bridge-matched template name (e.g. "Recirc Density"→"SlurryDensity")
        // would otherwise be re-emitted as template_only because its template name ≠ canonical.
        foreach (var tv in templateVars)
        {
            if (templateMatched.Contains(tv.Name))
                continue;
            if (seen.Add(tv.Name))
            {
                result.Add(tv with { Color = palette[ci % palette.Length], Source = "template_only" });
                ci++;
            }
        }

        lock (_lock)
        {
            _activeVariables = result;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a COMBINED Modbus register map covering ALL families from the variable registry.
    /// Family blocks: flecs=0, plc=1, unipro=2, compupac=3, other=4, lan=5, hes64=6.
    /// Each block = 2000 registers (first 2 reserved for heartbeat, data at base+2).
    /// Register address = family.modbusBase + registryEntry.Index * 2.
    /// Variable names use CANONICAL registry names (e.g., "Lan/RecircDensity", "SlurryDensity").
    /// This map is INDEPENDENT of the web UI active config — data flows from all sources simultaneously.
    /// </summary>
    private List<ModbusRegisterEntry> BuildCombinedModbusMap()
    {
        var map = new List<ModbusRegisterEntry>();

        if (_registry == null)
            return map;

        foreach (var family in _registry.GetFamilies())
        {
            var entries = _registry.GetRegisterEntries(family);
            map.AddRange(entries);
        }

        return map;
    }

    private void LoadState()
    {
        if (_statePath is null || !File.Exists(_statePath)) return;
        try
        {
            var json = File.ReadAllText(_statePath);
            var state = JsonSerializer.Deserialize<ConfigState>(json);
            if (state is not null)
            {
                _activeFamily = state.Family;
                _activeConfigName = state.ConfigName;
            }
        }
        catch { /* ignore corrupt state */ }
    }

    private void SaveState()
    {
        if (_statePath is null) return;
        try
        {
            var state = new ConfigState(_activeFamily, _activeConfigName);
            var dir = Path.GetDirectoryName(_statePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
        }
        catch { /* best-effort */ }
    }

    private record ConfigState(string Family, string ConfigName);
}
