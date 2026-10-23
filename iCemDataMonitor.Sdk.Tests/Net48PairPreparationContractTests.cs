namespace iCemDataMonitor.Sdk.Tests;

public sealed class Net48PairPreparationContractTests
{
    [Fact]
    public void ExactProductionPreparation_PortableGoldenVectors() => PairPreparationGolden.RunVectors();

    [WindowsLoggingFact]
    public void ExactProductionPreparation_CompilesCSharp73_AndRunsOnClr4()
    {
        var result = Net48ContractRunner.CompileAndRun("PairPreparationGolden", new[] {
            "PairPreparationGolden.cs", "PollPairPreparation.cs", "PollResolution.cs",
            "DataMonitorAddressMap.cs", "NameNormalizer.cs", "UnixTimestamp.cs", "BusLayout.cs" });
        Assert.Contains("net48 CLR 4.", result);
        Assert.Contains("net48 pair preparation golden vectors passed", result);
    }
}
