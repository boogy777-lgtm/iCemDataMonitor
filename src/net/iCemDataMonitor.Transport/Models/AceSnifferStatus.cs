namespace iCemDataMonitor.Transport.Models;

/// <summary>
/// Frozen contract: shape MUST match wwwroot/mock/sniffer-status.json "ace" object.
/// GUI consumes this exact property set — additions ok, removals/renames break GUI.
/// </summary>
public sealed class AceSnifferStatus
{
    public string Adapter { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public bool Up { get; set; }
    public double FramesPerSec { get; set; }
    public double LastFrameAgeSec { get; set; }
    public long TotalFrames { get; set; }

    /// <summary>Frames passing IsAceFrame (152 B + LLC 030308/030309). Additive witness field, non-breaking for GUI.</summary>
    public long AceFrames { get; set; }

    /// <summary>Count of distinct source MACs among ACE frames. Additive witness field, non-breaking for GUI.</summary>
    public int DistinctMacs { get; set; }

    public int DecodeErrors { get; set; }
    public Dictionary<string, int> PrefixCounts { get; set; } = new();
    public List<AceEquipmentStatus> Equipment { get; set; } = new();

    /// <summary>Convenience fields (¬ in mock — additive, non-breaking for GUI)</summary>
    public string State { get; set; } = "STOPPED";
    public int UnknownVectors { get; set; }

    /// <summary>Available adapters on this machine (name + description). Additive field for manual adapter selection GUI.</summary>
    public List<AdapterInfo> AvailableAdapters { get; set; } = new();
}

/// <summary>Minimal adapter descriptor for operator selection.</summary>
public sealed class AdapterInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}