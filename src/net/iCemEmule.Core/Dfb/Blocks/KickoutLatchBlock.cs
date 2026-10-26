// KickoutLatchBlock — SR-латч OPKO (CTX_CementUnit.PumpSide):
// Running → KickoutTripped ⇔ (RawPressure ≥ Setting) ∧ ¬reset;
// capture(PressureAtKo = RawPressure) в момент триггера; сброс только Reset.

namespace Halliburton.iCemEmule.Dfb;

public sealed class KickoutLatchBlock : DfbBlockBase
{
    public DfbSignal<double> RawPressure { get; }
    public DfbSignal<double> Setting { get; }
    public DfbSignal<bool> Reset { get; }
    public DfbSignal<bool> Tripped { get; }
    public DfbSignal<double> PressureAtKo { get; }

    private bool _tripped;
    private double _captured;

    public KickoutLatchBlock(string name) : base(name)
    {
        RawPressure = new DfbSignal<double>($"{name}.RawPressure");
        Setting = new DfbSignal<double>($"{name}.Setting");
        Reset = new DfbSignal<bool>($"{name}.Reset");
        Tripped = new DfbSignal<bool>($"{name}.Tripped");
        PressureAtKo = new DfbSignal<double>($"{name}.PressureAtKo");
        Inputs = new DfbSignal[] { RawPressure, Setting, Reset };
        Outputs = new DfbSignal[] { Tripped, PressureAtKo };
    }

    public override void Execute(DfbContext ctx)
    {
        if (Reset.Read())
        {
            _tripped = false;
            _captured = 0.0;
        }
        else if (!_tripped && RawPressure.Read() >= Setting.Read())
        {
            _tripped = true;
            _captured = RawPressure.Read();
        }

        Tripped.Value = _tripped;
        PressureAtKo.Value = _captured;
    }
}
