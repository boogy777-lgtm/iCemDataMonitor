namespace iCemDataMonitor.Transport.Models;

/// <summary>
/// Per-equipment stats entry — matches mock/sniffer-status.json equipment[] shape.
/// </summary>
public sealed class AceEquipmentStatus
{
    public int EquipmentNumber { get; set; }
    public string MacAddress { get; set; } = "";
    public int VectorCount { get; set; }
    public double TotalFrameRate { get; set; }

    /// <summary>Cumulative ACE frame count for this equipment. Additive witness field, non-breaking for GUI.</summary>
    public long TotalFrames { get; set; }
}