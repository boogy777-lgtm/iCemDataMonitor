using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using iCemDataMonitor.Data;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Host.Core.Endpoints;

/// <summary>
/// Multi-source REST endpoints for the 3-tab web dashboard.
/// /api/sources — status of all data sources
/// /api/data — timeseries data with optional source filter
/// </summary>
public static class SourceEndpoints
{
    public static void MapSourceEndpoints(this WebApplication app)
    {
        // ── GET /api/sources — status of all 3 sources ──
        app.MapGet("/api/sources", async (IEnumerable<ISourceStore> stores) =>
        {
            var results = new List<SourceStatus>();

            foreach (var store in stores)
            {
                SourceStatus status;
                try
                {
                    // Run GetStatus with a 3-second timeout to prevent hangs
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    status = await Task.Run(() => store.GetStatus(), cts.Token);
                }
                catch (Exception ex)
                {
                    status = new SourceStatus
                    {
                        Kind = store.Kind.ToString().ToLowerInvariant(),
                        Status = "error",
                        Error = ex is OperationCanceledException ? "timeout" : ex.Message
                    };
                }

                results.Add(status);
            }

            return Results.Ok(results);
        });

        // ── GET /api/data — timeseries with optional source filter ──
        app.MapGet("/api/data", async (
            string? names,
            double? hours,
            string[]? source,
            ITimeseriesStore store,
            IEnumerable<ISourceStore> stores) =>
        {
            var nameList = names?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>();
            DateTime? since = hours.HasValue ? DateTime.UtcNow.AddHours(-hours.Value) : null;
            var limit = hours.HasValue ? 10000 : 500;

            // No source filter → legacy behavior: query main ITimeseriesStore.
            if (source is null || source.Length == 0)
            {
                var history = await store.GetHistoryAsync(
                    nameList.Length > 0 ? nameList : null,
                    limit: limit,
                    since: since);

                return Results.Ok(history.Select(s => new
                {
                    source = "legacy",
                    ts = s.Timestamp,
                    values = s.Values
                }));
            }

            // Source filter: query each matching ISourceStore, merge by timestamp.
            var sourceSamples = new List<SourceSample>();
            foreach (var src in source)
            {
                var kind = ParseSourceKind(src);
                if (kind is null)
                    continue;

                var matchingStore = stores.FirstOrDefault(s => s.Kind == kind.Value);
                if (matchingStore is null)
                    continue;

                var history = await matchingStore.GetHistoryAsync(
                    nameList,
                    limit: limit,
                    since: since);

                sourceSamples.AddRange(history);
            }

            var merged = MergeByTimestamp(sourceSamples);
            if (merged.Count > limit)
                merged = merged.GetRange(merged.Count - limit, limit);

            return Results.Ok(merged);
        });
    }

    // ═══ Helpers ═══

    private static SourceKind? ParseSourceKind(string s)
    {
        return s.ToLowerInvariant() switch
        {
            "mongo" => SourceKind.Mongo,
            "ace" => SourceKind.Ace,
            "hes64" => SourceKind.Hes64,
            _ => null
        };
    }

    /// <summary>
    /// Merge samples from multiple sources by timestamp.
    /// Values from different sources at the same timestamp are combined.
    /// Missing values get null (omitted from the dict).
    /// </summary>
    private static List<object> MergeByTimestamp(List<SourceSample> samples)
    {
        // Group by ISO-8601 timestamp string.
        var timeline = new Dictionary<string, (DateTime Ts, Dictionary<string, double?> Vals, string? Source)>();

        foreach (var s in samples)
        {
            var tsKey = s.Timestamp.ToString("O");
            if (!timeline.TryGetValue(tsKey, out var bucket))
            {
                bucket = (s.Timestamp, new Dictionary<string, double?>(), s.Source.ToString().ToLowerInvariant());
                timeline[tsKey] = bucket;
            }

            foreach (var (name, val) in s.Variables)
                bucket.Vals[name] = val;
        }

        var ordered = timeline.Values
            .OrderBy(x => x.Ts)
            .Select(x => new
            {
                ts = x.Ts,
                source = x.Source,
                values = x.Vals
            })
            .ToList();

        return ordered.Cast<object>().ToList();
    }
}
