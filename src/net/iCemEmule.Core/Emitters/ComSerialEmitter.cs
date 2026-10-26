using System;
using System.IO.Ports;

namespace Halliburton.iCemEmule.Emitters
{
    /// <summary>
    /// Contract: FLECS serial line emitter (9600 8N1, com_wire from CONTRACT_iCemEmule.yaml).
    /// </summary>
    public interface IComEmitter : IDisposable
    {
        bool IsOpen { get; }
        string? PortName { get; }
        long LinesSent { get; }
        /// <summary>Open the named COM port. Returns "" on success, error message otherwise.</summary>
        string Open(string portName);
        /// <summary>Send one line (CRLF appended automatically). Thread-safe.</summary>
        void SendLine(string line);
        /// <summary>Close the port. Idempotent.</summary>
        void Close();
        /// <summary>Fired on open, close, and error transitions.</summary>
        event Action<string>? StatusChanged;
    }

    /// <summary>
    /// Implements FLECS serial line output via System.IO.Ports.SerialPort.
    /// Baud 9600, 8 data bits, no parity, one stop bit, no handshake.
    /// Idempotent Close/Dispose. Error-tolerant SendLine.
    /// </summary>
    public sealed class ComSerialEmitter : IComEmitter
    {
        private readonly object _lock = new();
        private SerialPort? _serialPort;
        private long _linesSent;

        /// <summary>W11.7: enumerate available serial ports for the UI dropdown.</summary>
        public static IReadOnlyList<string> ListPorts()
        {
            try { return SerialPort.GetPortNames(); }
            catch { return Array.Empty<string>(); }
        }

        public bool IsOpen
        {
            get
            {
                lock (_lock)
                    return _serialPort?.IsOpen ?? false;
            }
        }

        public string? PortName
        {
            get
            {
                lock (_lock)
                    return _serialPort?.PortName;
            }
        }

        public long LinesSent => Interlocked.Read(ref _linesSent);

        public event Action<string>? StatusChanged;

        public string Open(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
                return "Port name must not be empty.";

            lock (_lock)
            {
                // Idempotent: close any existing connection first
                CloseInternal();

                try
                {
                    var sp = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
                    {
                        Handshake = Handshake.None,
                        ReadTimeout = 500,
                        WriteTimeout = 500
                    };
                    sp.Open();
                    _serialPort = sp;
                    var msg = $"COM opened: {portName}";
                    StatusChanged?.Invoke(msg);
                    return "";
                }
                catch (UnauthorizedAccessException)
                {
                    var msg = $"Access denied to {portName}. Port may be in use by another application.";
                    StatusChanged?.Invoke(msg);
                    return msg;
                }
                catch (ArgumentException)
                {
                    var msg = $"Invalid port name: {portName}.";
                    StatusChanged?.Invoke(msg);
                    return msg;
                }
                catch (System.IO.IOException ex)
                {
                    var msg = $"COM error on {portName}: {ex.Message}";
                    StatusChanged?.Invoke(msg);
                    return msg;
                }
                catch (Exception ex)
                {
                    var msg = $"COM error on {portName}: {ex.Message}";
                    StatusChanged?.Invoke(msg);
                    return msg;
                }
                finally
                {
                    if (_serialPort == null || !_serialPort.IsOpen)
                    {
                        _serialPort?.Dispose();
                        _serialPort = null;
                    }
                }
            }
        }

        public void SendLine(string line)
        {
            lock (_lock)
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                    return;

                try
                {
                    _serialPort.Write(line + "\r\n");
                    Interlocked.Increment(ref _linesSent);
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"SendLine error: {ex.Message}");
                }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                CloseInternal();
            }
        }

        private void CloseInternal()
        {
            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen)
                        _serialPort.Close();
                }
                catch { /* best effort on close */ }
                try { _serialPort.Dispose(); } catch { }
                _serialPort = null;
                StatusChanged?.Invoke("COM closed");
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
