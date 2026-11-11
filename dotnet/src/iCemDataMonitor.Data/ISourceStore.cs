using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Data;

/// <summary>
/// Flat store interface for a sovereign data source. Each source owns its
/// SQLite database. NOT in iCemDataMonitor.Sdk — this is a data-layer concern.
/// </summary>
public interface ISourceStore
{
    SourceKind Kind { get; }

    /// <summary>Store one sample. Idempotent — duplicates suppressed by SQLite UNIQUE or upsert.</summary>
    Task InsertAsync(SourceSample sample, CancellationToken ct = default);

    /// <summary>Latest value for each requested name (or all if names is empty).</summary>
    Task<IReadOnlyDictionary<string, double>> GetCurrentAsync(IReadOnlyList<string> names, CancellationToken ct = default);

    /// <summary>Historical samples, ordered by timestamp ascending, newest last.</summary>
    Task<IReadOnlyList<SourceSample>> GetHistoryAsync(IReadOnlyList<string> names, int limit = 500, DateTime? since = null, CancellationToken ct = default);

    /// <summary>
    /// Diagnostic status for the dashboard /api/sources endpoint.
    /// Default returns a stub — override in real stores.
    /// </summary>
    SourceStatus GetStatus()
    {
        var kindName = Kind switch
        {
            SourceKind.Mongo => "mongo",
            SourceKind.Ace => "ace",
            SourceKind.Hes64 => "hes64",
            _ => "unknown"
        };
        return new SourceStatus { Kind = kindName, Status = "not_implemented" };
    }
}
