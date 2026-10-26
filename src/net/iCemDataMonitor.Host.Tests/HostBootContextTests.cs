using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Host.Tests;

[Collection(HostKestrelSerialCollection.Name)]
public sealed class HostBootContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hostboot_{Guid.NewGuid():N}");
    private string IniPath => Path.Combine(_dir, "config.ini");

    public HostBootContextTests() => Directory.CreateDirectory(_dir);

    private HostBuildSpec Spec(int? port = null) =>
        new([], IniPath: IniPath, PortOverride: port, ContentRootOverride: _dir);

    private void WriteConfig(int webOn = 0, int remoteOn = 0) =>
        File.WriteAllText(IniPath, IniConfig.DefaultContent
            .Replace("modbus_on=1", "modbus_on=0")
            .Replace("lansniffer_on=1", "lansniffer_on=0")
            .Replace("web_on=1", $"web_on={webOn}")
            .Replace("web_remote_on=0", $"web_remote_on={remoteOn}"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_LoadedContext_DoesNotReloadChangedOrInvalidIni(bool invalid)
    {
        WriteConfig();
        var context = HostAppFactory.LoadBootContext(Spec(0));
        if (invalid)
            File.WriteAllText(IniPath, "[station]\nweb_port=invalid");
        else
            WriteConfig(webOn: 1, remoteOn: 1);

        await using var app = HostAppFactory.Build(context);
        Assert.Same(context.Ini, app.Services.GetRequiredService<IniOptions>());
        var controller = app.Services.GetRequiredService<LayerRuntimeController>();
        Assert.Same(context.Ini, controller.Ini);
        Assert.False(controller.Layers.Web);
        Assert.Null(app.Services.GetService<RingHistory>());
        Assert.Equal(IPAddress.Loopback, context.BindPlan.Address);

    }

    [Fact]
    public async Task Build_AfterAnotherLoad_PersistsToCapturedPath()
    {
        WriteConfig();
        var context = HostAppFactory.LoadBootContext(Spec(0));
        var otherPath = Path.Combine(_dir, "other.ini");
        File.WriteAllText(otherPath, IniConfig.DefaultContent);
        var otherText = File.ReadAllText(otherPath);
        _ = IniConfig.Load(otherPath);
        Assert.Equal(otherPath, IniConfig.ActivePath);

        await using var app = HostAppFactory.Build(context);
        var controller = app.Services.GetRequiredService<LayerRuntimeController>();
        Assert.Equal(context.IniPath, controller.IniPath);
        Assert.True(controller.SetAsDefault);
        controller.Toggle(TrayToggleRules.LayerKind.Web);

        Assert.Null(controller.LastError);
        Assert.Equal(1, IniConfig.Parse(File.ReadAllText(IniPath)).WebOn);
        Assert.Equal(context.Ini.WebPort, IniConfig.Parse(File.ReadAllText(IniPath)).WebPort);
        Assert.Equal(otherText, File.ReadAllText(otherPath));
    }

    [Theory]
    [InlineData(0, null, "127.0.0.1")]
    [InlineData(1, null, "0.0.0.0")]
    [InlineData(0, 9123, "127.0.0.1")]
    [InlineData(1, 9123, "0.0.0.0")]
    public void LoadBootContext_SelectsBindPlan_WithoutChangingPersistedPort(
        int remoteOn, int? portOverride, string expectedAddress)
    {
        WriteConfig(remoteOn: remoteOn);
        var context = HostAppFactory.LoadBootContext(Spec(portOverride));

        Assert.Equal(portOverride ?? context.Ini.WebPort, context.BindPlan.Port);
        Assert.Equal(new IniOptions().WebPort, context.Ini.WebPort);
        Assert.Equal(IPAddress.Parse(expectedAddress), context.BindPlan.Address);
        Assert.Equal(HttpProtocols.Http1AndHttp2, context.BindPlan.Protocols);
        Assert.Equal(Path.GetFullPath(IniPath), context.IniPath);
    }

    [Fact]
    public void LoadBootContext_RejectsInvalidIni_BeforeBuild()
    {
        File.WriteAllText(IniPath, "[station]\nweb_port=invalid");
        Assert.Throws<IniConfigException>(() => HostAppFactory.LoadBootContext(Spec()));
    }

    [Fact]
    public void LoadBootContext_MissingIni_CreatesExistingDefaults()
    {
        var context = HostAppFactory.LoadBootContext(Spec());
        Assert.Equal(new IniOptions(), context.Ini);
        Assert.Equal(context.Ini, IniConfig.Parse(File.ReadAllText(IniPath)));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
