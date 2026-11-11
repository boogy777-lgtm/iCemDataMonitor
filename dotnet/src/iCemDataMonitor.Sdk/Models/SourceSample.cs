namespace iCemDataMonitor.Sdk.Models;

/// <summary>
/// Flat sample with source provenance. Replaces TimeSeriesSample (which lacked source/sequence).
/// </summary>
public readonly record struct SourceSample(
    DateTime Timestamp,
    IReadOnlyDictionary<string, double> Variables,
    SourceKind Source,
    long Sequence,
    uint? Equipment = null,
    uint? SvNumber = null
);
