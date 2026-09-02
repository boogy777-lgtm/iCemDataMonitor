// SignalSourceTests — маппинг wireName ↔ сигналы сети (INT_DfbEngine.SignalMap).

using Halliburton.iCemEmule.Dfb;
using Xunit;

namespace DfbEngine.Tests;

public sealed class SignalSourceTests
{
    [Fact]
    public void Known_wire_names_resolve()
    {
        var net = CementUnitNetworkFactory.Create();
        var src = new DfbSignalSource(net);

        Assert.True(src.TryGetValue("Pump A Pressure", out _));
        Assert.True(src.TryGetValue("Side A Kicked Out?", out _));
        Assert.True(src.TryGetValue("OPKO B Setting", out _));
        Assert.True(src.TryGetValue("Recirc Density", out _));
        Assert.True(src.TryGetValue("Control Mode", out _));
        Assert.True(src.TryGetValue("Analog Input 8", out _));
        Assert.True(src.TryGetValue("Cmnt Ops Track IDNum", out _));
    }

    [Fact]
    public void Unknown_wire_name_falls_back()
    {
        var net = CementUnitNetworkFactory.Create();
        var src = new DfbSignalSource(net);

        Assert.False(src.TryGetValue("No Such Signal", out _));
        Assert.Equal(0.0, src.GetValue("No Such Signal"));
    }

    [Fact]
    public void Kicked_out_is_bool_like_value()
    {
        var net = CementUnitNetworkFactory.Create();
        var src = new DfbSignalSource(net);

        net.Execute(DfbContext.Create(1, 1, 7));
        Assert.True(src.TryGetValue("Side A Kicked Out?", out double v));
        Assert.Equal(0.0, v);   // штатный режим — не сработал
    }

    [Fact]
    public void Combined_and_highest_are_composed()
    {
        var net = CementUnitNetworkFactory.Create();
        var src = new DfbSignalSource(net);

        net.Signal<double>("PumpA.SetpointRate").Value = 2.0;
        net.Signal<double>("PumpB.SetpointRate").Value = 3.0;

        Assert.True(src.TryGetValue("Combined Pump Rate", out double combined));
        Assert.Equal(5.0, combined, 6);

        Assert.True(src.TryGetValue("Highest Pump Pressure", out double highest));
        Assert.True(highest >= 0.0);
    }
}
