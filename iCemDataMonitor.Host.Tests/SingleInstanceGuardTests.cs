using iCemDataMonitor.Host;

namespace iCemDataMonitor.Host.Tests;

// D.3' (3): single-instance mutex — second instance must fail fast.
public class SingleInstanceGuardTests
{
    [Fact]
    public void ProductionMutexName_Is_Global_Host_v1()
    {
        Assert.Equal(@"Global\iCemDataMonitorHost_v1", SingleInstanceGuard.DefaultMutexName);
    }

    [Fact]
    public void SecondInstance_Fails_Fast()
    {
        var name = $@"Global\iCemDataMonitorHost_test_{Guid.NewGuid():N}";
        using var first = new SingleInstanceGuard(name);
        Assert.True(first.Acquired);

        using var second = new SingleInstanceGuard(name);
        Assert.False(second.Acquired); // caller maps this to stderr + exit 1
    }

    [Fact]
    public void ReleasedMutex_CanBeAcquiredAgain()
    {
        var name = $@"Global\iCemDataMonitorHost_test_{Guid.NewGuid():N}";
        using (var first = new SingleInstanceGuard(name))
        {
            Assert.True(first.Acquired);
        }
        using var again = new SingleInstanceGuard(name);
        Assert.True(again.Acquired);
    }
}
