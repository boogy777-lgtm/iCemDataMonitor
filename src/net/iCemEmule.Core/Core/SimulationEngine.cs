// SimulationEngine — ISimulationEngine implementation.
// Thread-safe: SetTemplate / Tick / ResetClock all under lock.
// Waveform: elapsed→elapsed; toggle→(int)(elapsed/TogglePeriod)%2;
//           amp>0&&freq>0 → base+amp*sin(2π·f·t); else base.
// NO deterministic mode. NO MongoDB.

namespace Halliburton.iCemEmule.Core;

public sealed class SimulationEngine : ISimulationEngine
{
    private readonly object _lock = new();
    private DeviceTemplate? _current;
    private double _elapsed;   // seconds, accumulated on Tick after ResetClock

    // Runtime overrides (control channel `set`/`clear`): name → value, applied
    // in Tick AFTER the waveform. Empty ⇒ behavior identical to pre-control builds.
    private readonly Dictionary<string, double> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private double[] _lastValues = Array.Empty<double>();

    public IReadOnlyList<DeviceTemplate> Templates { get; }

    public DeviceTemplate? Current
    {
        get { lock (_lock) return _current; }
    }

    public event Action? TemplateSwapped;

    public SimulationEngine()
    {
        Templates = TemplateCatalog.Load();
        // default: first template (or fallback)
        if (Templates.Count > 0)
            _current = Templates[0];
    }

    public void SetTemplate(string deviceType, string templateName)
    {
        lock (_lock)
        {
            var match = Templates.FirstOrDefault(t =>
                string.Equals(t.DeviceType, deviceType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Name, templateName, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                // find any template matching deviceType
                match = Templates.FirstOrDefault(t =>
                    string.Equals(t.DeviceType, deviceType, StringComparison.OrdinalIgnoreCase));
            }

            if (match is null && Templates.Count > 0)
                match = Templates[0];

            if (match is not null && !ReferenceEquals(match, _current))
            {
                _current = match;
                // R5/R6: on-the-fly swap — next Tick uses new format
            }
        }
        TemplateSwapped?.Invoke();
    }

    public double[] Tick()
    {
        DeviceTemplate? tmpl;
        double elapsed;
        Dictionary<string, double>? overrides;
        lock (_lock)
        {
            tmpl = _current;
            _elapsed += 1.0;
            elapsed = _elapsed;
            overrides = _overrides.Count > 0 ? new Dictionary<string, double>(_overrides, StringComparer.OrdinalIgnoreCase) : null;
        }

        if (tmpl is null || tmpl.Variables.Length == 0)
            return Array.Empty<double>();

        var values = new double[tmpl.Variables.Length];
        for (int i = 0; i < tmpl.Variables.Length; i++)
        {
            var v = tmpl.Variables[i];
            values[i] = overrides is not null && overrides.TryGetValue(v.Name, out double ov)
                ? ov
                : ComputeValue(v, elapsed);
        }
        lock (_lock) { _lastValues = values; }
        return values;
    }

    public void ResetClock()
    {
        lock (_lock) { _elapsed = 0.0; }
    }

    // ── control-channel surface (set/get/clear) ─────────────────────────

    /// <summary>Find a variable of the CURRENT template by name (case-insensitive,
    /// Name or LegacyName). Index = position in Variables (and in LastValues).</summary>
    public bool TryFindVariable(string name, out TemplateVariable? variable, out int index)
    {
        lock (_lock)
        {
            var vars = _current?.Variables;
            if (vars is not null)
            {
                for (int i = 0; i < vars.Length; i++)
                {
                    if (string.Equals(vars[i].Name, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(vars[i].LegacyName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        variable = vars[i];
                        index = i;
                        return true;
                    }
                }
            }
        }
        variable = null;
        index = -1;
        return false;
    }

    public void SetOverride(string variableName, double value)
    {
        lock (_lock) { _overrides[variableName] = value; }
    }

    public bool ClearOverride(string variableName)
    {
        lock (_lock) { return _overrides.Remove(variableName); }
    }

    public IReadOnlyDictionary<string, double> SnapshotOverrides()
    {
        lock (_lock) { return new Dictionary<string, double>(_overrides); }
    }

    /// <summary>Last Tick result (native units, overrides applied). Empty before the first tick.</summary>
    public double[] LastValues
    {
        get { lock (_lock) return (double[])_lastValues.Clone(); }
    }

    public static double ComputeValue(TemplateVariable v, double elapsed)
    {
        // format "elapsed" ⇒ value = elapsed
        if (string.Equals(v.Format, "elapsed", StringComparison.OrdinalIgnoreCase))
            return elapsed;

        // digital toggle: TogglePeriod > 0
        if (v.TogglePeriod > 0)
        {
            var cyc = (int)(elapsed / v.TogglePeriod);
            return (cyc % 2) != 0 ? 1.0 : 0.0;
        }

        // sine: amplitude > 0 && frequency > 0
        if (v.Amplitude > 0 && v.Frequency > 0)
        {
            return v.BaseValue + v.Amplitude * Math.Sin(2.0 * Math.PI * v.Frequency * elapsed);
        }

        return v.BaseValue;
    }
}
