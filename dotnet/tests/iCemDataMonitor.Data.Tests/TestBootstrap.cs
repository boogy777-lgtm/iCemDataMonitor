using iCemDataMonitor.Data;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// Assembly bootstrap: unit runs must NEVER write the real ProgramData
/// evidence log (owner log policy applies to the live station, not test runs).
/// The DataBusLog kill switch is flipped off once per test assembly; the
/// DataBusLogTests re-enable it locally with a temp LogDirOverride.
/// </summary>
internal static class TestBootstrap
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        DataBusLog.Enabled = false;
        DataBusLog.LogDirOverride = null;
    }
}
