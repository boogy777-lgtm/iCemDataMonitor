// KickoutLatchTests — семантика OPKO-латча (CTX_CementUnit.PumpSide).

using Halliburton.iCemEmule.Dfb;
using Xunit;

namespace DfbEngine.Tests;

public sealed class KickoutLatchTests
{
    private static DfbContext Ctx(double t) => DfbContext.Create(t, (long)t, seed: 7);

    [Fact]
    public void Below_threshold_no_trip()
    {
        var latch = new KickoutLatchBlock("L");
        latch.RawPressure.Value = 1000;
        latch.Setting.Value = 3000;

        for (int t = 1; t <= 10; t++)
            latch.Execute(Ctx(t));

        Assert.False(latch.Tripped.Value);
        Assert.Equal(0.0, latch.PressureAtKo.Value);
    }

    [Fact]
    public void Trip_captures_pressure_at_ko()
    {
        var latch = new KickoutLatchBlock("L");
        latch.Setting.Value = 3000;
        latch.RawPressure.Value = 1000;
        latch.Execute(Ctx(1));

        latch.RawPressure.Value = 3550.5;
        latch.Execute(Ctx(2));

        Assert.True(latch.Tripped.Value);
        Assert.Equal(3550.5, latch.PressureAtKo.Value, 3);
    }

    [Fact]
    public void Latch_holds_after_pressure_drops()
    {
        var latch = new KickoutLatchBlock("L");
        latch.Setting.Value = 3000;
        latch.RawPressure.Value = 3200;
        latch.Execute(Ctx(1));

        latch.RawPressure.Value = 500;   // давление упало — латч держит
        latch.Execute(Ctx(2));

        Assert.True(latch.Tripped.Value);
    }

    [Fact]
    public void Reset_clears_latch_and_capture()
    {
        var latch = new KickoutLatchBlock("L");
        latch.Setting.Value = 3000;
        latch.RawPressure.Value = 3200;
        latch.Execute(Ctx(1));

        latch.Reset.Value = true;
        latch.Execute(Ctx(2));

        Assert.False(latch.Tripped.Value);
        Assert.Equal(0.0, latch.PressureAtKo.Value);
    }
}
