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

    public async Task StartModbusAsync()
    {
        if (_publisher is null)
            throw new InvalidOperationException("Modbus publisher not registered");
        await _publisher.StartAsync().ConfigureAwait(false);
    }

    public async Task StopModbusAsync()
    {
        if (_publisher is null)
            throw new InvalidOperationException("Modbus publisher not registered");
        await _publisher.StopAsync().ConfigureAwait(false);
    }

    public async Task StartSnifferAsync()
    {
        if (_sniffer is null)
            throw new InvalidOperationException("Sniffer service not registered");
        await _sniffer.StartSnifferAsync().ConfigureAwait(false);
    }

    public async Task StopSnifferAsync()
    {
        if (_sniffer is null)
            throw new InvalidOperationException("Sniffer service not registered");
        await _sniffer.StopSnifferAsync().ConfigureAwait(false);
    }
}
