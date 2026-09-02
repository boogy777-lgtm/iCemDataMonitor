// FirstOrderLagBlock — инерционное звено 1-го порядка: y += (target - y) * (dt / tau).

namespace Halliburton.iCemEmule.Dfb;

public sealed class FirstOrderLagBlock : DfbBlockBase
{
    public DfbSignal<double> Target { get; }
    public DfbSignal<double> Lagged { get; }
    public DfbParameter<double> TauSeconds { get; set; } = new(1.0);

    private double _lagged;

    public FirstOrderLagBlock(string name) : base(name)
    {
        Target = new DfbSignal<double>($"{name}.Target");
        Lagged = new DfbSignal<double>($"{name}.Lagged");
        Inputs = new DfbSignal[] { Target };
        Outputs = new DfbSignal[] { Lagged };
    }

    public override void Execute(DfbContext ctx)
    {
        double tau = Math.Max(ctx.Dt, TauSeconds.Value);
        _lagged += (Target.Read() - _lagged) * (ctx.Dt / tau);
        Lagged.Value = _lagged;
    }
}
