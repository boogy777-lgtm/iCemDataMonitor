// TotalizerTests — интегратор: монотонность, сброс стадии, кумулятивность работы.

using Halliburton.iCemEmule.Dfb;
using Xunit;

namespace DfbEngine.Tests;

public sealed class TotalizerTests
{
    private static DfbContext Ctx(double t) => DfbContext.Create(t, (long)t, seed: 7);

    [Fact]
    public void Stage_total_monotonic_and_resets()
    {
        var tot = new TotalizerBlock("T");
        tot.Rate.Value = 2.0;

        for (int t = 1; t <= 10; t++)
            tot.Execute(Ctx(t));
        Assert.Equal(20.0, tot.StageTotal.Value, 6);
        Assert.Equal(20.0, tot.JobTotal.Value, 6);

        tot.ResetStage.Value = true;
        tot.Execute(Ctx(11));
        Assert.Equal(0.0, tot.StageTotal.Value, 6);
        Assert.Equal(22.0, tot.JobTotal.Value, 6);   // job кумулятивен
    }

    [Fact]
    public void Zero_rate_no_growth()
    {
        var tot = new TotalizerBlock("T");
        tot.Rate.Value = 0.0;
        for (int t = 1; t <= 10; t++)
            tot.Execute(Ctx(t));
        Assert.Equal(0.0, tot.StageTotal.Value);
    }
}
