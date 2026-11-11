namespace iCemDataMonitor.Transport.Models;

public class AceSnifferOptions
{
    public const string Section = "AceSniffer";

    /// <summary>Adapter name (e.g. from SharpPcap device list). Null/empty = manual selection required; operator picks from available list.</summary>
    public string? Adapter { get; set; }

    /// <summary>Directory for capture-to-file output.</summary>
    public string CaptureDir { get; set; } = "captures";

    /// <summary>Whether the sniffer should start on host startup.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Interval (seconds) between retry scans when adapter is missing or not configured.</summary>
    public int RetryIntervalSeconds { get; set; } = 30;
}