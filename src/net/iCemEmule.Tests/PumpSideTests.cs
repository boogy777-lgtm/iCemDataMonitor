// PumpSideTests — насос целиком: давление к уставке, kickout при перегрузке,
// сброс, монотонные totals.

using Halliburton.iCemEmule.Dfb;
using Xunit;

namespace DfbEngine.Tests;

public sealed class PumpSideTests
{
    private static DfbContext Ctx(double t) => DfbContext.Create(t, (long)t, seed: 7);

    [Fact]
    public void Steady_rate_pressure_below_opko_no_trip()
    {
        var pump = new PumpSideBlock("P") { Resistance = new DfbParameter<double>(400.0) };
        pump.Rate.Value = 3.0;          // target ≈ 1200 psi
        pump.OpkoSetting.Value = 3000.0;

        for (int t = 1; t <= 30; t++)
            pump.Execute(Ctx(t));

        Assert.False(pump.KickedOut.Value);
        Assert.InRange(pump.RawPressure.Value, 1100, 1300);
        Assert.InRange(pump.StageTotal.Value, 85, 95);   // 3 bpm * 30 s
    }

    [Fact]
    public void Overload_trips_kickout_and_captures_pressure()
    {
        var pump = new PumpSideBlock("P") { Resistance = new DfbParameter<double>(400.0) };
        pump.Rate.Value = 10.0;         // target ≈ 4000 psi > 3000
        pump.OpkoSetting.Value = 3000.0;

        bool tripped = false;
        for (int t = 1; t <= 60 && !tripped; t++)
        {
            pump.Execute(Ctx(t));
            tripped = pump.KickedOut.Value;
        }

        Assert.True(tripped, "pump must trip within 60 s at 4000 psi target");
        Assert.True(pump.PressureAtKo.Value >= 3000.0);
    }

    [Fact]
    public void Reset_after_trip_clears_kickout_and_stage()
    {
        var pump = new PumpSideBlock("P") { Resistance = new DfbParameter<double>(400.0) };
        pump.Rate.Value = 10.0;
        pump.OpkoSetting.Value = 3000.0;

        for (int t = 1; t <= 60; t++)
            pump.Execute(Ctx(t));
        Assert.True(pump.KickedOut.Value);

        pump.Reset.Value = true;
        pump.Execute(Ctx(61));
        Assert.False(pump.KickedOut.Value);
        Assert.Equal(0.0, pump.PressureAtKo.Value);
        Assert.Equal(0.0, pump.StageTotal.Value, 6);
    }
}
