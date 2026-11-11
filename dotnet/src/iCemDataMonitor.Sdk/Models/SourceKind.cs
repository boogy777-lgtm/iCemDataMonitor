namespace iCemDataMonitor.Sdk.Models;

/// <summary>
/// Identifies the data source. Flat enum — no [Flags], mutually exclusive per reader instance.
/// </summary>
public enum SourceKind : byte
{
    /// <summary>Legacy MongoDB poll (JobRealtimeMessages)</summary>
    Mongo = 1,
    /// <summary>ACE LAN multicast sniffer</summary>
    Ace = 2,
    /// <summary>HES_64 SQL Server mirror</summary>
    Hes64 = 3,
    /// <summary>DataTap JSONL named-pipe ingest (iCem.exe producer)</summary>
    Tap = 4,
}
