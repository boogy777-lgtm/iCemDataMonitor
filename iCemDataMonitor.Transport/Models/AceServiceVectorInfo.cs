namespace iCemDataMonitor.Transport.Models;

/// <summary>
/// Per-service-vector info — matches mock/sniffer-service-vectors.json items.
/// </summary>
public sealed class AceServiceVectorInfo
{
    public int VectorId { get; set; }
    public string Name { get; set; } = "";
    public bool IsUnknown { get; set; }
    public double FrameRate { get; set; }
    public double LastSeenSec { get; set; }
    public int DecodedSignalCount { get; set; }
}