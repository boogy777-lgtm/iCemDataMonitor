using System.Text.Json;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Host.Grpc;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Bus;
using Microsoft.Extensions.DependencyInjection;

namespace iCemDataMonitor.Host.Tests;

public class LayerRuntimeStatusTests
{
    private sealed class Publisher : IModbusPublisher
    {
        public bool IsBound => false;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    [Fact]
    public void Factory_ReportsObservedFailure_SeparateFromRequestedAndPersistedPlan()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IModbusPublisher>(new Publisher());
        var ini = new IniOptions();
        HostComposition.RegisterLayerController(services, ini, LayerFlags.From(ini), "unused.ini");
        using var provider = services.BuildServiceProvider();
        var status = provider.GetRequiredService<LayerRuntimeController>().CaptureStatus();
        Assert.False(status.ModbusBound);
        Assert.True(status.RequestedModbus);
        Assert.Equal(1, status.Ini.ModbusOn);
        Assert.False(status.SnifferRunning);
        Assert.False(status.SnifferAvailable);
    }

    [Fact]
    public void Capture_ReadsEachProbeOnce_AndRestProtoAgree()
    {
        var services = new ServiceCollection();
        var ini = new IniOptions();
        HostComposition.RegisterLayerController(services, ini, LayerFlags.From(ini), "unused.ini");
        using var provider = services.BuildServiceProvider();
        var controller = provider.GetRequiredService<LayerRuntimeController>();
        int modbusReads = 0, snifferReads = 0;
        controller.ModbusBoundProbe = () => ++modbusReads == 1;
        controller.SnifferRunningProbe = () => ++snifferReads == 1;
        var status = controller.CaptureStatus();
        var proto = TelemetryServiceImpl.ProjectLayerState(status);
        var rest = JsonSerializer.SerializeToElement(HostComposition.GetLayerStatusShape(status, ini, LayerFlags.From(ini)));
        Assert.Equal(1, modbusReads);
        Assert.Equal(1, snifferReads);
        Assert.Equal(proto.Modbus, proto.ModbusBound);
        Assert.Equal(proto.Modbus, rest.GetProperty("modbus").GetBoolean());
        Assert.Equal(proto.Sniffer, rest.GetProperty("sniffer").GetBoolean());
        Assert.Equal(proto.ModbusPort, rest.GetProperty("modbusPort").GetInt32());
        Assert.Equal(proto.OpcDa, rest.GetProperty("opcDa").GetBoolean());
        Assert.Equal(proto.SetAsDefault, rest.GetProperty("setAsDefault").GetBoolean());
        var stats = JsonSerializer.SerializeToElement(HostComposition.GetSnifferStatsShape(new()));
        Assert.Equal(new[] { "aceFrames", "distinctMacs", "framesPerSec", "lastFrameAgeSec", "state", "totalFrames", "up" },
            stats.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
    }
}
