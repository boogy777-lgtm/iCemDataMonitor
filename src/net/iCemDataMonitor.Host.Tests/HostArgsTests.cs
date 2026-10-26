using iCemDataMonitor.Host;

namespace iCemDataMonitor.Host.Tests;

// D.3' (1): command-line contract — fail-fast on unknown args, no silent ignore.
public class HostArgsTests
{
    [Fact]
    public void Parse_Empty_AllNull_Valid()
    {
        var a = HostArgs.Parse([]);
        Assert.True(a.IsValid);
        Assert.Null(a.Port);
        Assert.Null(a.IniPath);
        Assert.False(a.ShowHelp);
    }

    [Fact]
    public void Parse_Port_And_Ini()
    {
        var a = HostArgs.Parse(["--port", "8091", "--ini", @"C:\x\station.ini"]);
        Assert.True(a.IsValid);
        Assert.Equal(8091, a.Port);
        Assert.Equal(@"C:\x\station.ini", a.IniPath);
    }

    [Fact]
    public void Parse_PortZero_Allowed_ForTests()
    {
        var a = HostArgs.Parse(["--port", "0"]);
        Assert.True(a.IsValid);
        Assert.Equal(0, a.Port);
    }

    [Fact]
    public void Parse_PortOutOfRange_Invalid()
    {
        Assert.False(HostArgs.Parse(["--port", "70000"]).IsValid);
        Assert.False(HostArgs.Parse(["--port", "-1"]).IsValid);
    }

    [Fact]
    public void Parse_MissingPortValue_Invalid()
    {
        Assert.False(HostArgs.Parse(["--port"]).IsValid);
        Assert.False(HostArgs.Parse(["--ini"]).IsValid);
    }

    [Fact]
    public void Parse_UnknownArgument_FailFast_NotSilent()
    {
        var a = HostArgs.Parse(["--service"]);
        Assert.False(a.IsValid);
        Assert.Contains(a.Errors, e => e.Contains("--service"));
    }

    [Fact]
    public void Parse_Help_StopsValidation()
    {
        var a = HostArgs.Parse(["--help"]);
        Assert.True(a.ShowHelp);
        Assert.False(a.IsValid); // help short-circuits the run
    }
}
