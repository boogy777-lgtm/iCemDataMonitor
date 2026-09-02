using iCemDataMonitor.Sdk;
using iCemDataMonitor.Transport;

namespace iCemDataMonitor.Host.Core;

/// <summary>
/// D.3a: default layer actuator used by both the WinUI shell and the headless
/// Windows service host. Keeps the shell/Host controller logic identical and
/// prevents actuator drift between the two launchers.
/// </summary>
public sealed class DefaultLayerActuator : LayerRuntimeController.ILayerActuator
{
    private readonly IModbusPublisher? _publisher;
    private readonly AceSnifferService? _sniffer;

    public DefaultLayerActuator(IModbusPublisher? publisher, AceSnifferService? sniffer)
    {
        _publisher = publisher;
        _sniffer = sniffer;
    }

    public void StartModbus()
    {
        if (_publisher is null)
            throw new InvalidOperationException("Modbus publisher not registered");
        _publisher.StartAsync().GetAwaiter().GetResult();
    }

    public void StopModbus()
    {
        if (_publisher is null)
            throw new InvalidOperationException("Modbus publisher not registered");
        _publisher.StopAsync().GetAwaiter().GetResult();
    }

    public void StartSniffer()
    {
        if (_sniffer is null)
            throw new InvalidOperationException("Sniffer service not registered");
        _sniffer.StartSnifferAsync().GetAwaiter().GetResult();
    }

    public void StopSniffer()
    {
        if (_sniffer is null)
            throw new InvalidOperationException("Sniffer service not registered");
        _sniffer.StopSnifferAsync().GetAwaiter().GetResult();
    }
}
