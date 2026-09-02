// TemplateCatalog — loads templates\*.json (excl. variable_registry.json, lantable_registry.json)
// Resolves registry variable references from variable_registry.json.
// Fallback: built-in flecs 4.53 (34 variables) if zero templates loaded.
// Logs warnings to iCemEmule.templates.log next to exe.

using System.Globalization;
using System.Text.Json;

namespace Halliburton.iCemEmule.Core;

public static class TemplateCatalog
{
    private static readonly string TemplatesDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates");

    private static readonly string LogPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "iCemEmule.templates.log");

    private static readonly string[] ExcludedFiles =
        { "variable_registry.json", "lantable_registry.json" };

    // ── internal model types for registry JSON ──────────────────────────
    private sealed record RegistrySimulation(
        double @base,
        double amplitude,
        double frequency,
        string format,
        double togglePeriod
    )
    {
        public RegistrySimulation() : this(0, 0, 0, string.Empty, 0) { }
    }

    private sealed record RegistryFamily(string alias, int index, string? wireName);

    private sealed record RegistryFamilies(
        RegistryFamily? flecs,
        RegistryFamily? plc,
        RegistryFamily? lan
    );

    private sealed record RegistryVariable(
        string name,
        string display,
        string uom,
        RegistrySimulation? simulation,
        bool excludeFromTelemetry,
        RegistryFamilies? families
    )
    {
        public RegistryVariable() : this(string.Empty, string.Empty, string.Empty, null, false, null) { }
    }

    private sealed record RegistryRoot(RegistryVariable[]? variables);

    // ── template JSON shapes ────────────────────────────────────────────
    private sealed record TemplateVarInline(
        int index, string name, string? legacyName, string? uom,
        double @base, double amplitude, double frequency,
        string? format, double togglePeriod, bool excludeFromTelemetry
    );

    private sealed record TemplateVarRef(string registry, int index);

    private sealed record TemplateJson(
        string name, string deviceType, int[]? autoSelectIndices,
        JsonElement[]? variables
    );

    // ── cache ───────────────────────────────────────────────────────────
    private static List<DeviceTemplate>? _templates;
    private static RegistryVariable[]? _registryVars;
    private static readonly object _lock = new();

    public static IReadOnlyList<DeviceTemplate> Load()
    {
        lock (_lock)
        {
            if (_templates is not null) return _templates;
            _registryVars = LoadRegistryVariables();
            var list = new List<DeviceTemplate>();

            if (Directory.Exists(TemplatesDir))
            {
                foreach (var f in Directory.GetFiles(TemplatesDir, "*.json"))
                {
                    var fn = Path.GetFileName(f);
                    if (ExcludedFiles.Contains(fn, StringComparer.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var json = File.ReadAllText(f);
                        var tj = JsonSerializer.Deserialize<TemplateJson>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (tj?.name is null || tj.deviceType is null)
                            continue;

                        var dt = BuildDeviceTemplate(tj);
                        if (dt is not null) list.Add(dt);
                    }
                    catch (Exception ex)
                    {
                        Log($"WARN: failed to load template '{f}': {ex.Message}");
                    }
                }
            }

            if (list.Count == 0)
            {
                Log("WARN: zero template files loaded — using built-in fallback flecs 4.53 (34 variables).");
                list.Add(BuildFallbackFlecs453());
            }

            _templates = list;
            return _templates;
        }
    }

    private static DeviceTemplate? BuildDeviceTemplate(TemplateJson tj)
    {
        var vars = new List<TemplateVariable>();
        if (tj.variables is not null)
        {
            foreach (var elem in tj.variables)
            {
                var v = ResolveVariable(elem, tj.deviceType);
                if (v is not null) vars.Add(v);
            }
        }

        vars.Sort((a, b) => a.Index.CompareTo(b.Index));

        return new DeviceTemplate
        {
            Name = tj.name,
            DeviceType = tj.deviceType,
            AutoSelectIndices = tj.autoSelectIndices ?? Array.Empty<int>(),
            Variables = vars.ToArray()
        };
    }

    private static TemplateVariable? ResolveVariable(JsonElement elem, string deviceType)
    {
        if (elem.TryGetProperty("registry", out var regNameElem))
        {
            // registry reference
            var regName = regNameElem.GetString() ?? string.Empty;
            var index = elem.TryGetProperty("index", out var idxElem) ? idxElem.GetInt32() : 0;
            return ResolveRegistryVar(regName, index, deviceType);
        }

        // inline variable
        var inline = JsonSerializer.Deserialize<TemplateVarInline>(elem,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (inline is null) return null;

        return new TemplateVariable
        {
            Index = inline.index,
            Name = inline.name,
            LegacyName = inline.legacyName ?? inline.name,
            UoM = inline.uom ?? string.Empty,
            BaseValue = inline.@base,
            Amplitude = inline.amplitude,
            Frequency = inline.frequency,
            Format = inline.format ?? string.Empty,
            TogglePeriod = inline.togglePeriod,
            ExcludeFromTelemetry = inline.excludeFromTelemetry
        };
    }

    private static TemplateVariable? ResolveRegistryVar(string regName, int index, string deviceType)
    {
        var vars = _registryVars ?? Array.Empty<RegistryVariable>();
        var match = FindRegistryVariable(vars, regName);
        if (match is null)
        {
            Log($"WARN: registry variable '{regName}' not found in variable_registry.json");
            return null;
        }

        var fam = GetFamilyEntry(match, deviceType);
        var sim = match.simulation ?? new RegistrySimulation();

        return new TemplateVariable
        {
            Index = fam?.index ?? index,
            Name = fam?.alias ?? match.display,
            LegacyName = fam?.alias ?? match.display,
            UoM = match.uom ?? string.Empty,
            BaseValue = sim.@base,
            Amplitude = sim.amplitude,
            Frequency = sim.frequency,
            Format = sim.format ?? string.Empty,
            TogglePeriod = sim.togglePeriod,
            ExcludeFromTelemetry = match.excludeFromTelemetry
        };
    }

    private static RegistryVariable? FindRegistryVariable(RegistryVariable[] vars, string regName)
    {
        foreach (var v in vars)
        {
            if (string.Equals(v.name, regName, StringComparison.OrdinalIgnoreCase))
                return v;
            if (string.Equals(v.display, regName, StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return null;
    }

    private static RegistryFamily? GetFamilyEntry(RegistryVariable v, string deviceType)
    {
        var fams = v.families;
        if (fams is null) return null;
        var dt = deviceType.ToLowerInvariant();
        return dt switch
        {
            "flecs" => fams.flecs,
            "plc" => fams.plc,
            "lan" => fams.lan,
            _ => fams.flecs ?? fams.plc ?? fams.lan
        };
    }

    private static RegistryVariable[] LoadRegistryVariables()
    {
        // 2026-08-31: emulator-override law — variable_registry.emulator.json
        // wins when present (shared install root with the host, see
        // SimFileResolver.cs).
        var path = SimFileResolver.Resolve(TemplatesDir, "variable_registry.json");
        if (!File.Exists(path))
        {
            Log("WARN: variable_registry.json not found — registry references will fail.");
            return Array.Empty<RegistryVariable>();
        }
        try
        {
            var json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<RegistryRoot>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return root?.variables ?? Array.Empty<RegistryVariable>();
        }
        catch (Exception ex)
        {
            Log($"WARN: failed to parse variable_registry.json: {ex.Message}");
            return Array.Empty<RegistryVariable>();
        }
    }

    // ── built-in fallback: flecs 4.53, 34 variables ─────────────────────
    private static DeviceTemplate BuildFallbackFlecs453()
    {
        // TEMPLATE:FlecsTray_v3@fallback — exact copy from FlecsTray_v3.cs:117-154
        // Wire-fix (2026-08-17, merge-restored): Time & Date / Unit Num are NOT equipment
        // variables in iCem's CMT v4.53 parser — they consume no wire tokens (parity with
        // variable_registry.json excludeFromTelemetry for UnixTimestamp/UnitNum).
        var vars = new TemplateVariable[]
        {
            new() { Index=0,  Name="Time & Date",     LegacyName="Time & Date",     UoM="",    BaseValue=0,     Amplitude=0,    Frequency=0,      Format="elapsed", ExcludeFromTelemetry=true },
            new() { Index=1,  Name="Unit Num",        LegacyName="Unit Num",        UoM="",    BaseValue=1,     Amplitude=0,    Frequency=0,      Format="int",      ExcludeFromTelemetry=true },
            new() { Index=2,  Name="SlurryDensity",   LegacyName="SlurryDensity",   UoM="ppg", BaseValue=15.80, Amplitude=0.40, Frequency=0.10 },
            new() { Index=3,  Name="DH_Density",      LegacyName="DH_Density",      UoM="ppg", BaseValue=15.90, Amplitude=0.30, Frequency=0.08 },
            new() { Index=4,  Name="Mix Water Rate",  LegacyName="Mix Water Rate",  UoM="bpm", BaseValue=2.10,  Amplitude=1.00, Frequency=0.15 },
            new() { Index=5,  Name="PumpRate",        LegacyName="PumpRate",        UoM="bpm", BaseValue=4.20,  Amplitude=0.80, Frequency=0.12 },
            new() { Index=6,  Name="PumpPressure",    LegacyName="PumpPressure",    UoM="psi", BaseValue=2500,  Amplitude=200,  Frequency=0.05 },
            new() { Index=7,  Name="DS Pump Press",   LegacyName="DS Pump Press",   UoM="psi", BaseValue=1800,  Amplitude=150,  Frequency=0.06 },
            new() { Index=8,  Name="Water Stg Tot",   LegacyName="Water Stg Tot",   UoM="bbl", BaseValue=120,   Amplitude=2,    Frequency=0.020 },
            new() { Index=9,  Name="Mix Water Total", LegacyName="Mix Water Total", UoM="bbl", BaseValue=80,    Amplitude=1.5,  Frequency=0.025 },
            new() { Index=10, Name="Pump Stg Tot",    LegacyName="Pump Stg Tot",    UoM="bbl", BaseValue=200,   Amplitude=3,    Frequency=0.018 },
            new() { Index=11, Name="CombPumpTotal",   LegacyName="CombPumpTotal",   UoM="bbl", BaseValue=100.0, Amplitude=2.5,  Frequency=0.020 },
            new() { Index=12, Name="CementValvePsn",  LegacyName="CementValvePsn",  UoM="%",   BaseValue=85.0,  Amplitude=5.0,  Frequency=0.05 },
            new() { Index=13, Name="WaterValvePsn",   LegacyName="WaterValvePsn",   UoM="%",   BaseValue=75.0,  Amplitude=5.0,  Frequency=0.04 },
            new() { Index=14, Name="PS Pump Rate",    LegacyName="PS Pump Rate",    UoM="bpm", BaseValue=3.50,  Amplitude=0.50, Frequency=0.14 },
            new() { Index=15, Name="DS Pump Rate",    LegacyName="DS Pump Rate",    UoM="bpm", BaseValue=3.00,  Amplitude=0.40, Frequency=0.13 },
            new() { Index=16, Name="Dig out",         LegacyName="Dig out",         UoM="",    BaseValue=0,     Amplitude=0,    Frequency=0,      Format="int", TogglePeriod=2.5 },
            new() { Index=17, Name="HalWin Event",    LegacyName="HalWin Event",    UoM="",    BaseValue=0,     Amplitude=0,    Frequency=0,      Format="int", TogglePeriod=10 },
            new() { Index=18, Name="Temperature",     LegacyName="Temperature",     UoM="degF",BaseValue=130.0, Amplitude=5,    Frequency=0.02 },
            new() { Index=19, Name="rho12_hat",       LegacyName="rho12_hat",       UoM="ppg", BaseValue=12.80, Amplitude=0.30, Frequency=0.07 },
            new() { Index=20, Name="h2_hat",          LegacyName="h2_hat",          UoM="ft",  BaseValue=5000,  Amplitude=100,  Frequency=0.010 },
            new() { Index=21, Name="vhp_density_HP",  LegacyName="vhp_density_HP",  UoM="ppg", BaseValue=14.50, Amplitude=0.40, Frequency=0.09 },
            new() { Index=22, Name="TubHeight",       LegacyName="TubHeight",       UoM="ft",  BaseValue=6.50,  Amplitude=0.80, Frequency=0.02 },
            new() { Index=23, Name="Extra Press 1",   LegacyName="Extra Press 1",   UoM="psi", BaseValue=1500,  Amplitude=100,  Frequency=0.04 },
            new() { Index=24, Name="Extra Press 2",   LegacyName="Extra Press 2",   UoM="psi", BaseValue=1200,  Amplitude=80,   Frequency=0.045 },
            new() { Index=25, Name="Extra Rate 1",    LegacyName="Extra Rate 1",    UoM="bpm", BaseValue=2.00,  Amplitude=0.30, Frequency=0.11 },
            new() { Index=26, Name="Extra Total 1",   LegacyName="Extra Total 1",   UoM="bbl", BaseValue=50,    Amplitude=1.0,  Frequency=0.020 },
            new() { Index=27, Name="Extra Rate 2",    LegacyName="Extra Rate 2",    UoM="bpm", BaseValue=1.50,  Amplitude=0.25, Frequency=0.10 },
            new() { Index=28, Name="Extra Total 2",   LegacyName="Extra Total 2",   UoM="bbl", BaseValue=40,    Amplitude=0.8,  Frequency=0.019 },
            new() { Index=29, Name="Wtr/slry Ratio",  LegacyName="Wtr/slry Ratio",  UoM="",    BaseValue=0.85,  Amplitude=0.05, Frequency=0.06 },
            new() { Index=30, Name="MudCup Density",  LegacyName="MudCup Density",  UoM="ppg", BaseValue=13.00, Amplitude=0.40, Frequency=0.08 },
            new() { Index=31, Name="PS Pmp Stg Tot",  LegacyName="PS Pmp Stg Tot",  UoM="bbl", BaseValue=100,   Amplitude=2,    Frequency=0.017 },
            new() { Index=32, Name="DS Pmp Stg Tot",  LegacyName="DS Pmp Stg Tot",  UoM="bbl", BaseValue=90,    Amplitude=1.8,  Frequency=0.016 },
            new() { Index=33, Name="VolumetricMeter", LegacyName="VolumetricMeter", UoM="bpm", BaseValue=4.20,  Amplitude=0.50, Frequency=0.08 },
        };

        return new DeviceTemplate
        {
            Name = "flecs 4.53",
            DeviceType = "FLECS",
            AutoSelectIndices = new[] { 6, 5, 2, 3, 18, 11, 13, 22, 12, 33 },
            Variables = vars
        };
    }

    private static void Log(string msg)
    {
        try
        {
            var line = $"{DateTime.UtcNow:O} {msg}{Environment.NewLine}";
            File.AppendAllText(LogPath, line);
        }
        catch { /* best-effort logging */ }
    }
}
