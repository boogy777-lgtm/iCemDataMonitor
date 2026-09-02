namespace iCemDataMonitor.Transport;

/// <summary>
/// Seam for unit-testing serial port logic without real COM hardware.
/// Implementations: SerialPortAdapter (real HW), InMemorySerialPort (tests).
/// </summary>
public interface ISerialPort : IDisposable
{
    void Open();
    void Close();
    bool IsOpen { get; }

    /// <summary>Raw read into buffer. Returns bytes read, 0 on EOF/timeout.</summary>
    int Read(byte[] buffer, int offset, int count);

    /// <summary>Raw write.</summary>
    void Write(byte[] buffer, int offset, int count);

    /// <summary>Flush output buffer.</summary>
    void Flush();

    /// <summary>Optional: port name for logging/status.</summary>
    string PortName { get; }
}