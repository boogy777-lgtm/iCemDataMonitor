using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Sdk;

/// <summary>
/// Unified data source reader. Replaces both IDataSource (poll) and the
/// push-sink pattern (the old ISnifferDataSink was REMOVED 2026-08-27 —
/// producer-only decree: sources push, consumers read, nobody redirects).
/// Flat — no base class, no generic type params, no decorator pattern.
/// </summary>
public interface ISourceReader
{
    /// <summary>Immutable source identity</summary>
    SourceKind Kind { get; }

    /// <summary>Fires per sample. Polling sources fire on internal timer; push sources fire per decoded frame.</summary>
    event Action<SourceSample>? OnSample;

    /// <summary>Start the reader. Idempotent — second call is no-op.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop the reader. Idempotent — drains in-flight, second call is no-op.</summary>
    Task StopAsync();

    /// <summary>Synchronous snapshot of variables this source knows about. No async I/O.</summary>
    IReadOnlyList<VariableDefinition> GetDiscoveredVariables();
}
