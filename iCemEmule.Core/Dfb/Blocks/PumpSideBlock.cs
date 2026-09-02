// PumpSideBlock — один насос (A или B): модель CTX_CementUnit.PumpSide.
// Композиция: FirstOrderLagBlock (τ=2s) + KickoutLatchBlock (OPKO) + TotalizerBlock.
// P_raw(t) = PressureBase + Resistance·Rate + lag + noise(σ=0.5%·max(100,|P|)).

namespace Halliburton.iCemEmule.Dfb;

public sealed class PumpSideBlock : DfbBlockBase
{
    // входы (setpoint'ы сети)
    public DfbSignal<double> Rate { get; }
    public DfbSignal<double> OpkoSetting { get; }
    public DfbSignal<bool> Reset { get; }
    // выходы
    public DfbSignal<double> RawPressure { get; }
    public DfbSignal<double> FilteredPressure { get; }   // значение лага БЕЗ шума
    public DfbSignal<double> PressureAtKo { get; }
    public DfbSignal<bool> KickedOut { get; }
    public DfbSignal<double> StageTotal { get; }
    public DfbSignal<double> JobTotal { get; }

    public DfbParameter<double> PressureBase { get; set; } = new(0.0);
    public DfbParameter<double> Resistance { get; set; } = new(400.0);   // psi per bpm
    public DfbParameter<double> LagTauSeconds { get; set; } = new(2.0);
    public DfbParameter<double> NoiseSigma { get; set; } = new(0.005);    // relative

    private readonly FirstOrderLagBlock _lag;
    private readonly KickoutLatchBlock _latch;
    private readonly TotalizerBlock _totals;

    public PumpSideBlock(string name) : base(name)
    {
        Rate = new DfbSignal<double>($"{name}.SetpointRate");
        OpkoSetting = new DfbSignal<double>($"{name}.OpkoSetting");
        Reset = new DfbSignal<bool>($"{name}.Reset");
        RawPressure = new DfbSignal<double>($"{name}.RawPressure");
        FilteredPressure = new DfbSignal<double>($"{name}.FilteredPressure");
        PressureAtKo = new DfbSignal<double>($"{name}.PressureAtKo");
        KickedOut = new DfbSignal<bool>($"{name}.KickedOut");
        StageTotal = new DfbSignal<double>($"{name}.StageTotal");
        JobTotal = new DfbSignal<double>($"{name}.JobTotal");
        Inputs = new DfbSignal[] { Rate, OpkoSetting, Reset };
        Outputs = new DfbSignal[] { RawPressure, FilteredPressure, PressureAtKo, KickedOut, StageTotal, JobTotal };

        _lag = new FirstOrderLagBlock($"{name}.Lag") { TauSeconds = LagTauSeconds };
        _latch = new KickoutLatchBlock($"{name}.Latch");
        _totals = new TotalizerBlock($"{name}.Totals");
    }

    public override void Execute(DfbContext ctx)
    {
        double rate = Rate.Read();
        bool reset = Reset.Read();

        double target = PressureBase.Value + Resistance.Value * rate;
        _lag.Target.Value = target;
        _lag.Execute(ctx);

        FilteredPressure.Value = _lag.Lagged.Value;   // без шума — для фильтрованных SV

        double noise = (ctx.Random.NextDouble() * 2.0 - 1.0)
                     * NoiseSigma.Value * Math.Max(100.0, Math.Abs(_lag.Lagged.Value));
        double raw = Math.Max(0.0, _lag.Lagged.Value + noise);
        RawPressure.Value = raw;

        _latch.RawPressure.Value = raw;
        _latch.Setting.Value = OpkoSetting.Read();
        _latch.Reset.Value = reset;
        _latch.Execute(ctx);
        KickedOut.Value = _latch.Tripped.Value;
        PressureAtKo.Value = _latch.PressureAtKo.Value;

        _totals.Rate.Value = rate;
        _totals.ResetStage.Value = reset;
        _totals.Execute(ctx);
        StageTotal.Value = _totals.StageTotal.Value;
        JobTotal.Value = _totals.JobTotal.Value;
    }
}
