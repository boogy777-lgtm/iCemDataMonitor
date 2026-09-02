namespace iCemDataMonitor.Transport.Models;

/// <summary>
/// Frozen contract: shape MUST match wwwroot/mock/sniffer-status.json "tee" object.
/// GUI consumes this exact property set — additions ok, removals/renames break GUI.
/// </summary>
public class ComTeeStatus
{
    public string PhysicalPort { get; set; } = "";
    public int Baud { get; set; }
    public string TemplateName { get; set; } = "";
    public bool Up { get; set; }
    public double BytesForwardedPerSec { get; set; }
    public double BytesParsedPerSec { get; set; }
    public int ForwardErrors { get; set; }
    public double LastForwardAgeSec { get; set; }
    public double LastParseAgeSec { get; set; }
    public bool Com0comPairOk { get; set; }
    public bool PhysicalPortBusy { get; set; }

    /// <summary>Convenience field (¬ in mock fixture — additive, non-breaking for GUI)</summary>
    public string State { get; set; } = "STOPPED";
}