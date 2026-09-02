using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Sdk.Diagnostics;

namespace iCemDataMonitor.Host;

/// <summary>
/// D.3' B.1.6 / A.2.4: startup.log + the one-line machine-readable READY
/// marker («READY modbus=bound web=:8090 bus=open»). The marker is the anchor
/// for scripts and the live-gate — it is written ONLY after every configured
/// layer has finished starting (no premature READY).
///
/// W22 (owner decree, ProgramData logging): the file now lives at
/// %ProgramData%\Halliburton\iCem\DataMonitoring\Logs\iCemDataMonitor.Host\startup.log
/// (was: next to the exe) and every line goes through the StationFileLog
/// policy — 25 MB cap with in-place newest-half truncation, lazy folder
/// creation, never throws.
/// </summary>
public static class StartupLog
{
    public const string FileName = "startup.log";

    /// <summary>Create an append-style logger writing to the ProgramData host
    /// log directory by default. Console echo keeps interactive runs
    /// informative.</summary>
    public static Action<string> Create(string? directory = null)
    {
        var dir = directory ?? StationLogPaths.HostDir;
        var path = Path.Combine(dir, FileName);
        return message =>
        {
            var line = $"{DateTime.UtcNow:O} {message}";
            StationFileLog.Write(path, line); // best effort + 25 MB cap by contract
            try { Console.WriteLine(line); }
            catch
            {
                // W21.4: when the UI births the host it captures stdout; if the
                // UI later exits, the pipe closes and Console.WriteLine throws.
                // The FILE log above survives — the console echo is best-effort.
            }
        };
    }

    /// <summary>Compose the A.2.4 READY marker from REAL states (truth probes,
    /// not ini flags): modbus bound? actual web port? bus open?</summary>
    public static string ReadyMarker(bool modbusConfigured, bool modbusBound, int webPort, bool busOpen) =>
        $"READY modbus={(modbusConfigured ? (modbusBound ? "bound" : "failed") : "off")} web=:{webPort} bus={(busOpen ? "open" : "closed")}";
}
