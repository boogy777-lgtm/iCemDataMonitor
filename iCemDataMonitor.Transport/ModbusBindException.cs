using System.Net.Sockets;

namespace iCemDataMonitor.Transport;

/// <summary>A socket failure from binding the configured Modbus listener.</summary>
public sealed class ModbusBindException(int port, SocketException cause)
    : Exception($"Failed to bind Modbus port {port}: {cause.Message}", cause)
{
    public int Port { get; } = port;
    public SocketError SocketErrorCode { get; } = cause.SocketErrorCode;
}
