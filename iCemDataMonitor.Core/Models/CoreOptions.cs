namespace iCemDataMonitor.Core.Models;

/// <summary>
/// Configuration bind point for Core orchestrator.
/// All values are operator-level station config (§15.7 seam_kinds: station-config-value).
/// </summary>
public sealed class CoreOptions
{
    public const string Section = "Core";

    public string DbPath { get; set; } = "icem_poc.db";
    public string MongoUrl { get; set; } = "mongodb://localhost:33036";
    public string DefaultFamily { get; set; } = "flecs";
    public string DefaultConfig { get; set; } = "flecs_ver4p53_ppg_bpm_psi";
    public int HealthCheckIntervalSeconds { get; set; } = 5;

    // MongoDataSource options (F14: configurable discovery cap + harvest window)
    public int MongoDiscoverLimit { get; set; } = 2000;
    public int MongoNameCollectionTimeoutMs { get; set; } = 8000;
    public int MongoNameCollectionBatches { get; set; } = 5;

    // F15: tail-first poll — start from latest _id to avoid draining backlog on startup.
    // Set true only if full backlog replay is required (e.g. reprocessing historical data).
    public bool MongoDrainBacklog { get; set; } = false;

    // F15: debounce interval for reharvesting live names after startup window.
    // A burst of new names within this window triggers a single RefreshFromLiveAsync call.
    public int MongoReharvestDebounceMs { get; set; } = 3000;
}
