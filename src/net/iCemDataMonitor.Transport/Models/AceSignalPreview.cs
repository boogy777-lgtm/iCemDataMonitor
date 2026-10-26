namespace iCemDataMonitor.Transport.Models;

/// <summary>
/// Per-signal decoded value preview — matches mock/sniffer-signal-preview.json items.
/// Sentinel channels have value=-9999 and sentinel=true.
/// </summary>
public sealed class AceSignalPreview
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public string Uom { get; set; } = "";
    public bool Sentinel { get; set; }
}