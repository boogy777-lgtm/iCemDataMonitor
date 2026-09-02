// TotalizerBlock — интегратор расхода: StageTotal = ∫rate·dt (сброс по ResetStage),
// JobTotal = кумулятивен.

namespace Halliburton.iCemEmule.Dfb;

public sealed class TotalizerBlock : DfbBlockBase
{
    public DfbSignal<double> Rate { get; }
    public DfbSignal<bool> ResetStage { get; }
    public DfbSignal<double> StageTotal { get; }
    public DfbSignal<double> JobTotal { get; }

    private double _stage;
    private double _job;

    public TotalizerBlock(string name) : base(name)
    {
        Rate = new DfbSignal<double>($"{name}.Rate");
        ResetStage = new DfbSignal<bool>($"{name}.ResetStage");
        StageTotal = new DfbSignal<double>($"{name}.StageTotal");
        JobTotal = new DfbSignal<double>($"{name}.JobTotal");
        Inputs = new DfbSignal[] { Rate, ResetStage };
        Outputs = new DfbSignal[] { StageTotal, JobTotal };
    }

    public override void Execute(DfbContext ctx)
    {
        double rate = Rate.Read();
        _stage += rate * ctx.Dt;
        _job += rate * ctx.Dt;
        if (ResetStage.Read()) _stage = 0.0;
        StageTotal.Value = _stage;
        JobTotal.Value = _job;
    }
}
