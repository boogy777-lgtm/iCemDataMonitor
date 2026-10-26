using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Sdk;

public interface ITimeseriesStore
{
    [Obsolete("Use InsertSampleAsync(SourceSample) instead for source provenance.")]
    Task InsertAsync(DateTime ts, IReadOnlyDictionary<string, double> variables);

    /// <summary>Store a source-tagged sample. Preferred over InsertAsync.</summary>
    Task InsertSampleAsync(SourceSample sample, CancellationToken ct = default);

    Task<IReadOnlyList<TimeSeriesSample>> GetHistoryAsync(IReadOnlyList<string>? names = null, int limit = 500, DateTime? since = null);

    /// <summary>Get history filtered by source.</summary>
    Task<IReadOnlyList<SourceSample>> GetHistoryAsync(
        SourceKind source,
        IReadOnlyList<string>? names = null,
        int limit = 500,
        DateTime? since = null,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, double>> GetCurrentAsync(IReadOnlyList<string>? names = null);

    /// <summary>Get current values filtered by source.</summary>
    Task<IReadOnlyDictionary<string, double>> GetCurrentAsync(
        SourceKind source,
        IReadOnlyList<string>? names = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetVariableNamesAsync();
}
