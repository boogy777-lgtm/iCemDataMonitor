using System.IO.Ports;

namespace iCemDataMonitor.Transport;

/// <summary>
/// Production adapter: wraps System.IO.Ports.SerialPort.
/// </summary>
public sealed class SerialPortAdapter : ISerialPort
{
    private readonly SerialPort _port;
    public string PortName { get; }

    public SerialPortAdapter(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
    {
        PortName = portName;
        _port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
        };
    }

    public bool IsOpen => _port.IsOpen;

    public void Open()
    {
        if (!_port.IsOpen)
            _port.Open();
    }

    public void Close()
    {
        try
        {
            if (_port.IsOpen)
                _port.Close();
        }
        catch { }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _port.Read(buffer, offset, count);
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        _port.Write(buffer, offset, count);
    }

    public void Flush()
    {
        // SerialPort doesn't have Flush; BaseStream Flush
        _port.BaseStream.Flush();
    }

    public void Dispose()
    {
        Close();
        _port.Dispose();
    }
}