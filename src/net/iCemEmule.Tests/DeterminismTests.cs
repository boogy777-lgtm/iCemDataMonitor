// DeterminismTests — одинаковые elapsed ⇒ одинаковые значения (INT_DfbEngine: Idempotency).

using Halliburton.iCemEmule.Dfb;
using Xunit;

namespace DfbEngine.Tests;

public sealed class DeterminismTests
{
    [Fact]
    public void Two_networks_same_elapsed_sequence_identical_outputs()
    {
        var a = CementUnitNetworkFactory.Create();
        var b = CementUnitNetworkFactory.Create();

        for (int t = 1; t <= 50; t++)
        {
            var ctxA = DfbContext.Create(t, t, seed: 7);
            var ctxB = DfbContext.Create(t, t, seed: 7);
            a.Execute(ctxA);
            b.Execute(ctxB);

            var paths = new[]
            {
                "PumpA.RawPressure", "PumpA.PressureAtKo", "PumpB.RawPressure",
                "Mixer.RecircDensity", "Mixer.MixWaterStageTotal", "Temp.DisplayTemperature"
            };
            foreach (var p in paths)
                Assert.Equal(a.Signal<double>(p).Value, b.Signal<double>(p).Value, 9);
        }
    }

    [Fact]
    public void Same_seed_same_noise_sequence()
    {
        var r1 = new Random(7);
        var r2 = new Random(7);
        for (int i = 0; i < 100; i++)
            Assert.Equal(r1.NextDouble(), r2.NextDouble());
    }
}
