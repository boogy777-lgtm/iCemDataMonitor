using System.Globalization;

namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// E.2.3: pure formatter for the host tray tooltip, refreshed at 1 Hz from
/// StationHealthState. Unit-tested as a table — the WinForms NotifyIcon itself
/// is NOT unit-testable (message loop).
/// Format: «iCemDataMonitor Host — bus:{open|closed} HB {N}s · web:{port}».
/// 2026-08-27: the «engine:{pid}» segment was REMOVED with engine supervision
/// (the emulator is a separate independent app now — its pid is its own UI's
/// business, not the host tray's).
/// </summary>
public static class TrayTooltipFormatter
{
    /// <summary>NotifyIcon.Text hard limit — Windows throws beyond 63 chars.</summary>
    public const int MaxLength = 63;

    public static string Format(bool busOpen, short heartbeat, int webPort)
    {
        var text =
            $"iCemDataMonitor Host — bus:{(busOpen ? "open" : "closed")} HB {heartbeat.ToString(CultureInfo.InvariantCulture)}s" +
            $" · web:{webPort.ToString(CultureInfo.InvariantCulture)}";
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    /// <summary>Defensive clamp for any dynamic tooltip source (NotifyIcon throws beyond 63).</summary>
    public static string Clamp(string text) =>
        text.Length <= MaxLength ? text : text[..MaxLength];
}
