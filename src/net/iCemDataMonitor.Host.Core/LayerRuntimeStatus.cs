using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Host.Core;

/// <summary>Immutable observation: Web and OPC DA are plans, not in-process health.</summary>
public sealed record LayerRuntimeStatus(
    bool ModbusBound, bool SnifferRunning, bool SnifferAvailable,
    bool RequestedModbus, bool RequestedSniffer, bool Web, bool OpcDa,
    IniOptions Ini, bool Busy, string? LastError)
{
    public bool SetAsDefault => Ini.UiSetAsDefault == 1;
}
