// TemperatureBlock — Display/Controller температуры: медленный lag (τ=60s) к Target + дрейф.

namespace Halliburton.iCemEmule.Dfb;

public sealed class TemperatureBlock : DfbBlockBase
{
    public DfbSignal<double> DisplayTemperature { get; }
    public DfbSignal<double> ControllerTemperature { get; }
    public DfbParameter<double> Target { get; set; } = new(120.0);
    public DfbParameter<double> TauSeconds { get; set; } = new(60.0);

    private double _display;
    private double _controller;

    public TemperatureBlock(string name) : base(name)
    {
        DisplayTemperature = new DfbSignal<double>($"{name}.DisplayTemperature");
        ControllerTemperature = new DfbSignal<double>($"{name}.ControllerTemperature");
        Outputs = new DfbSignal[] { DisplayTemperature, ControllerTemperature };
    }

    public override void Execute(DfbContext ctx)
    {
        double tau = Math.Max(ctx.Dt, TauSeconds.Value);
        double target = Target.Value + (ctx.Random.NextDouble() - 0.5) * 0.2;
        _display += (target - _display) * (ctx.Dt / tau);
        _controller += (target - _controller) * (ctx.Dt / (tau * 0.8));
        DisplayTemperature.Value = _display;
        ControllerTemperature.Value = _controller;
    }
}
