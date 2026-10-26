namespace iCemDataMonitor.Data;

/// <summary>
/// Flat status record for a data source. No polymorphism — single record
/// with all possible fields, null for those that don't apply.
/// </summary>
public sealed record SourceStatus
{
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "unknown"; // connected | running | stopped | error
    public DateTime? LastTimestamp { get; init; }
    public long RowCount { get; init; }
    public string DbPath { get; init; } = "";
    public long DbSizeBytes { get; init; }
    public string? Error { get; init; }

    // Source-specific fields
    public string? AdapterName { get; init; }  // ACE only
    public string? MacAddress { get; init; }   // ACE only
    public long FramesCaptured { get; init; }  // ACE only
    public string? LastSvSeen { get; init; }   // ACE only
    public string? UnitNumber { get; init; }   // HES_64 only
    public long DecodeErrors { get; init; }    // HES_64 only

    public long DedupeDropped { get; init; }   // DataTap only: transport duplicates dropped
}
