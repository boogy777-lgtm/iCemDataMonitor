using iCemDataMonitor.Plugins.Icem;

namespace iCemDataMonitor.Plugins.Icem.Tests;

public class VariableUnitGuesserTests
{
    [Fact]
    public void SelfCheck_AllPass()
    {
        var (pass, fail, failures) = VariableUnitGuesser.SelfCheck();
        if (fail > 0)
            Assert.Fail(string.Join("\n", failures));
        Assert.True(pass > 0);
        Assert.Equal(0, fail);
    }

    // F6 regression cases
    [Theory]
    [InlineData("VolumetricMeter", "bpm")]
    [InlineData("Mix Water Rate", "bpm")]
    [InlineData("Water Stg Tot", "bbl")]
    [InlineData("Mix Water Total", "bbl")]
    [InlineData("Comb Pump Total", "bbl")]
    [InlineData("Pump Stg Tot", "bbl")]
    public void F6_CriticalNames_ReturnCorrectUom(string name, string expected)
    {
        Assert.Equal(expected, VariableUnitGuesser.GuessUom(name));
    }

    // flecs canonical keys (sample)
    [Theory]
    [InlineData("SlurryDensity", "ppg")]
    [InlineData("DH_Density", "ppg")]
    [InlineData("PumpRate", "bpm")]
    [InlineData("PumpPressure", "psi")]
    [InlineData("CementValvePsn", "%")]
    [InlineData("WaterValvePsn", "%")]
    [InlineData("Temperature", "degF")]
    [InlineData("TubHeight", "ft")]
    [InlineData("UnixTimestamp", "")]
    [InlineData("HalWinEvent", "")]
    [InlineData("MixWaterRate", "bpm")]
    [InlineData("CombPumpTotal", "bbl")]
    public void FlecsCanonicalKeys_ReturnCorrectUom(string name, string expected)
    {
        Assert.Equal(expected, VariableUnitGuesser.GuessUom(name));
    }

    // plc_basic.json emulator names
    [Theory]
    [InlineData("PLC Timestamp", "")]
    [InlineData("PLC Status", "")]
    [InlineData("Suction Pressure", "psi")]
    [InlineData("Discharge Pressure", "psi")]
    [InlineData("Pump Rate", "bpm")]
    [InlineData("Flow Rate", "gpm")]
    [InlineData("Tank Level", "ft")]
    [InlineData("Tank Temperature", "degF")]
    [InlineData("Motor Speed", "rpm")]
    [InlineData("Valve Position", "%")]
    public void PlcEmulatorNames_ReturnCorrectUom(string name, string expected)
    {
        Assert.Equal(expected, VariableUnitGuesser.GuessUom(name));
    }

    // Substring heuristic: names NOT in exact table
    [Theory]
    [InlineData("SomeDensity", "ppg")]
    [InlineData("SomeRate", "bpm")]
    [InlineData("SomeFlow", "bpm")]
    [InlineData("SomePressure", "psi")]
    [InlineData("SomeTemp", "degF")]
    [InlineData("SomeTotal", "bbl")]
    [InlineData("SomeVolume", "bbl")]
    [InlineData("SomeLevel", "bbl")]
    [InlineData("SomeValve", "%")]
        [InlineData("SomePsn", "%")]
    [InlineData("SomeHeight", "ft")]
    [InlineData("SomeDepth", "ft")]
    [InlineData("SomeStretch", "ft")]
    [InlineData("SomeTub", "ft")]
    [InlineData("SomeWeight", "klb")]
    [InlineData("SomeHookload", "klb")]
    [InlineData("UnknownThing", "")]
    public void SubstringHeuristics_ReturnCorrectUom(string name, string expected)
    {
        Assert.Equal(expected, VariableUnitGuesser.GuessUom(name));
    }

    // Edge: "rate" must win over "vol" substring to prevent VolumetricMeter → bbl
    [Fact]
    public void RateWinsOverVol()
    {
        // "VolumetricMeter" contains "vol" but ALSO "rate" — rate rule must trigger first
        Assert.Equal("bpm", VariableUnitGuesser.GuessUom("VolumetricMeter"));
        // "VolumeMeter" contains "volume" but NOT "rate" → bbl
        Assert.Equal("bbl", VariableUnitGuesser.GuessUom("VolumeMeter"));
        // "VolMeter" contains "vol" but not "rate" → bbl (substring "vol")
        Assert.Equal("bbl", VariableUnitGuesser.GuessUom("VolMeter"));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.Equal("bpm", VariableUnitGuesser.GuessUom("volumetricmeter"));
        Assert.Equal("bpm", VariableUnitGuesser.GuessUom("VOLUMETRICMETER"));
        Assert.Equal("ppg", VariableUnitGuesser.GuessUom("slurrydensity"));
        Assert.Equal("psi", VariableUnitGuesser.GuessUom("PUMPPRESSURE"));
    }
}
