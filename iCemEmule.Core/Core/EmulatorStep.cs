using Halliburton.iCemEmule.Dfb;

namespace Halliburton.iCemEmule.Core;

/// <summary>Logical 1 Hz advancement. The caller serializes commands/steps and
/// supplies transport readiness; this owner never opens, schedules or emits.</summary>
internal sealed class EmulatorStep
{
    internal SimulationEngine ComEngine { get; }
    internal AcePayloadProvider BaseProvider { get; }
    internal FrozenPayloadProvider FrozenProvider { get; }
    internal ManualOverridePayloadProvider OverrideProvider { get; }
    internal UnitConvertingPayloadProvider LanUnitProvider { get; }
    private readonly DfbNetwork? _dfbNetwork;
    private readonly IAcePayloadProvider _output;
    internal double LanElapsed => _lanElapsed;
    internal DfbNetwork? DfbNetwork => _dfbNetwork;
    private double _lanElapsed;
    private bool _wasLanActive;
    internal bool ComEmulating { get; set; }
    private bool _lanEmulating;
    internal bool LanEmulating
    {
        get => _lanEmulating;
        set { _lanEmulating = value; FrozenProvider.Frozen = !value; }
    }
    private UnitSettings _units = new();
    internal UnitSettings Units
    {
        get => _units;
        set { _units = value; LanUnitProvider.Settings = value; }
    }

    internal EmulatorStep(UnitPreset preset)
        : this(new SimulationEngine(), new AcePayloadProvider(), DfbEngineBridge.TryCreateNetwork(), preset) { }

    // Concrete definitions/providers may be prepared by tests without hardware.
    internal EmulatorStep(SimulationEngine com, AcePayloadProvider baseProvider, DfbNetwork? network,
        UnitPreset preset, IAcePayloadProvider? output = null)
    {
        ComEngine = com;
        BaseProvider = baseProvider;
        _dfbNetwork = network;
        var decorated = network is not null ? DfbEngineBridge.Decorate(baseProvider, network) : baseProvider;
        FrozenProvider = new FrozenPayloadProvider(decorated) { Frozen = true };
        OverrideProvider = new ManualOverridePayloadProvider(FrozenProvider);
        LanUnitProvider = new UnitConvertingPayloadProvider(OverrideProvider, preset);
        Units = new UnitSettings { Preset = preset };
        _output = output ?? LanUnitProvider;
    }

    // Keep preparation staged: runtime sends COM before starting DFB/LAN work.
    internal string? PrepareCom()
    {
        var values = ComEmulating ? ComEngine.Tick() : FrozenComValues();
        var template = ComEngine.Current;
        if (template is null || values.Length == 0) return null;
        for (int i = 0; i < values.Length && i < template.Variables.Length; i++)
        {
            var v = template.Variables[i];
            if (UnitConversion.IsConvertible(v.UoM))
                values[i] = UnitConversion.ToTarget(values[i], v.UoM, Units);
        }
        return SignalWireCodec.FormatLine(values, template);
    }

    /// <summary>Frozen COM values: last tick snapshot + current manual overrides.</summary>
    private double[] FrozenComValues()
    {
        var template = ComEngine.Current;
        var last = ComEngine.LastValues;
        if (template is null || last.Length == 0)
            return ComEngine.Tick();   // seed once, then freeze
        var ov = ComEngine.SnapshotOverrides();
        for (int i = 0; i < last.Length && i < template.Variables.Length; i++)
            if (ov.TryGetValue(template.Variables[i].Name, out double o))
                last[i] = o;
        return last;
    }

    internal PreparedLanStep? PrepareLan(bool active, bool sending, uint equipment)
    {
        if (active)
        {
            if (!_wasLanActive) _lanElapsed = 0.0;
            if (!sending)
            {
                var elapsed = _lanElapsed;
                if (_dfbNetwork is not null && LanEmulating)
                    _dfbNetwork.Execute(DfbContext.Create(elapsed, (long)elapsed, DfbEngineBridge.Seed));
                IReadOnlyList<AceVectorData> vectors;
                try { vectors = _output.GetSuperframe(equipment, elapsed); }
                catch
                {
                    _lanElapsed += 1.0;
                    _wasLanActive = active;
                    return null;
                }
                _lanElapsed += 1.0;
                _wasLanActive = active;
                return new PreparedLanStep(vectors, equipment);
            }
        }
        _wasLanActive = active;
        return null;
    }
}

internal sealed record PreparedLanStep(IReadOnlyList<AceVectorData> Vectors, uint Equipment);
