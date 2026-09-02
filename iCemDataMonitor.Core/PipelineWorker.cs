using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using iCemDataMonitor.Core.Models;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Core;

/// <summary>
/// FSM_C: Watching state — orchestrates the pipeline start-to-drain lifecycle.
/// Wires IDataSource → Channel → ITimeseriesStore. Never inspects, transforms,
/// or routes payload — pure pass-through orchestration.
///
/// Startup order (§15.3): Subscribe → IDataSource.StartAsync → activate config
/// → ConfigureAsync(publishers) → ITimeseriesStore.InsertAsync (on data arrival).
/// </summary>
public sealed class PipelineWorker : BackgroundService
{
    private readonly IDataSource _dataSource;
    private readonly IConfigStore _configStore;
    private readonly ITimeseriesStore _timeseriesStore;
    private readonly IModbusPublisher _modbusPublisher;
    private readonly Channel<TimeSeriesSample> _dataChannel;
    private readonly CoreOptions _options;
    private readonly ILogger<PipelineWorker> _logger;

    // Live variable name collection — fed from data flow (¬ from MongoDB directly).
    // TaskCompletionSource signals when enough unique names have been collected.
    private readonly ConcurrentDictionary<string, byte> _liveNames = new();
    private readonly TaskCompletionSource _liveNamesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _batchesSeen;

    // F15: Continuous harvest — growth of the live-name set after the startup
    // window (incl. a timed-out window with 0 names) triggers a throttled
    // reharvest. Debounce prevents rebuild storms on bulk ingestion.
    private long _lastReharvestTicks;
    private long _reharvestDebounceTicks;
    private int _lastHarvestedCount;
    private volatile bool _startupWindowDone;

    // Multi-source: list of ISourceReader instances (null when absent — backward compat).
    private readonly IReadOnlyList<ISourceReader>? _sourceReaders;
    // Monotonic sequence counter for SourceSample.Sequence.
    private long _seq;

    public PipelineWorker(
        IDataSource dataSource,
        IConfigStore configStore,
        ITimeseriesStore timeseriesStore,
        IModbusPublisher modbusPublisher,
        Channel<TimeSeriesSample> dataChannel,
        CoreOptions options,
        ILogger<PipelineWorker> logger,
        IEnumerable<ISourceReader>? sourceReaders = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _timeseriesStore = timeseriesStore ?? throw new ArgumentNullException(nameof(timeseriesStore));
        _modbusPublisher = modbusPublisher ?? throw new ArgumentNullException(nameof(modbusPublisher));
        _dataChannel = dataChannel ?? throw new ArgumentNullException(nameof(dataChannel));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sourceReaders = sourceReaders?.ToList(); // snapshot enumerable eagerly
        _reharvestDebounceTicks = Math.Max(100, _options.MongoReharvestDebounceMs) * TimeSpan.TicksPerMillisecond;
    }

    /// <summary>
    /// Startup sequence replicated from Python main.py:
    ///   1. Subscribe to IDataSource (before start — zero data loss window)
    ///   2. IDataSource.StartAsync
    ///   3. ActivateConfigAsync default config
    ///   4. ConfigureAsync publishers from IConfigStore register map
    ///   5. Start publishers
    ///   6. Drain Channel → ITimeseriesStore.InsertAsync (blocks until cancellation)
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Step 1: Subscribe BEFORE start (Python: ds.on_data at module level) ──
        _dataSource.Subscribe(OnDataArrived);

        // ── Step 2: Start data source ──
        _logger.LogInformation("[pipeline] Starting data source");
        await _dataSource.StartAsync(stoppingToken);
        _logger.LogInformation("[pipeline] Data source started");

        // ── Step 2a: Start multi-source readers (additive — never replaces legacy) ──
        if (_sourceReaders != null)
        {
            foreach (var reader in _sourceReaders)
            {
                reader.OnSample += OnSourceSample;
                await reader.StartAsync(stoppingToken);
                _logger.LogInformation("[pipeline] SourceReader started: {Kind}", reader.Kind);
            }
        }

        // ── Step 2.5: Collect live variable names from data flow ──
        // Names are harvested from OnDataArrived (Subscribe callback),
        // NOT from a direct MongoDB query. First N batches provide the name set.
        // This keeps the web tier isolated from the data source — it sees
        // only what flows through the pipeline into SQLite.
        _logger.LogInformation("[pipeline] Collecting live variable names from data flow");
        var timeout = Task.Delay(_options.MongoNameCollectionTimeoutMs, stoppingToken);
        var completed = await Task.WhenAny(_liveNamesReady.Task, timeout);
        // Mark the window done BEFORE the initial refresh — otherwise names arriving
        // during the refresh call fall into the gap (no continuous harvest yet).
        _startupWindowDone = true;
        if (completed == _liveNamesReady.Task)
        {
            var liveNames = _liveNames.Keys.ToList();
            _logger.LogInformation("[pipeline] Collected {Count} live names from {Batches} data batches",
                liveNames.Count, _batchesSeen);
            if (liveNames.Count > 0)
            {
                await _configStore.RefreshFromLiveAsync(liveNames);
                _logger.LogInformation("[pipeline] Config store refreshed with live names");
            }
        }
        else
        {
            var partial = _liveNames.Keys.ToList();
            _logger.LogWarning(
                "[pipeline] Timed out waiting for live names — got {Count} partial from {Batches} batches",
                partial.Count, _batchesSeen);
            if (partial.Count > 0)
            {
                await _configStore.RefreshFromLiveAsync(partial);
            }
            else
            {
                // FIX: fallback to DiscoverAsync when no live data flows during
                // the startup window. Tail-first poll may have zero new documents
                // while historical data exists — DiscoverAsync reads the collection.
                _logger.LogInformation("[pipeline] No live data — falling back to DiscoverAsync");
                try
                {
                    var discovered = await _dataSource.DiscoverAsync();
                    if (discovered.Count > 0)
                    {
                        _logger.LogInformation("[pipeline] DiscoverAsync returned {Count} variable definitions", discovered.Count);
                        await _configStore.RefreshFromLiveAsync(discovered.Select(v => v.Name));
                    }
                    else
                    {
                        _logger.LogWarning("[pipeline] DiscoverAsync also returned 0 variables — config will be template-only");
                        await _configStore.RefreshFromLiveAsync(Array.Empty<string>());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[pipeline] DiscoverAsync fallback failed");
                    await _configStore.RefreshFromLiveAsync(Array.Empty<string>());
                }
            }
        }
        Volatile.Write(ref _lastHarvestedCount, _liveNames.Count);

        // ── Step 2.6: Harvest discovered variables from multi-source readers ──
        if (_sourceReaders != null)
        {
            foreach (var reader in _sourceReaders)
            {
                foreach (var v in reader.GetDiscoveredVariables())
                    _liveNames.TryAdd(v.Name, 0);
            }
        }

        // ── Step 3: Activate default config (RebuildAsync now sees _liveMongoNames) ──
        _logger.LogInformation(
            "[pipeline] Activating default config: {Family}/{Config}",
            _options.DefaultFamily, _options.DefaultConfig);
        await _configStore.ActivateConfigAsync(_options.DefaultFamily, _options.DefaultConfig);

        var activeConfig = _configStore.GetActiveConfig();
        var activeVars = _configStore.GetActiveVariables();
        _logger.LogInformation(
            "[pipeline] Active config: {Config} ({Count} variables)",
            activeConfig?.Name ?? "(none)", activeVars.Count);

        // ── Step 4: Configure publishers from register map ──
        var registerMap = _configStore.GetModbusRegisterMap();
        _logger.LogInformation("[pipeline] Configuring Modbus publisher ({Count} registers)", registerMap.Count);
        await _modbusPublisher.ConfigureAsync(registerMap);

        // ── Step 5: Start publishers ──
        _logger.LogInformation("[pipeline] Starting Modbus publisher");
        await _modbusPublisher.StartAsync(stoppingToken);

        _logger.LogInformation("[pipeline] Pipeline fully operational — draining data channel");

        // ── Step 6: Drain channel → timeseries store ──
        // TODO (MultiSource): migrate Channel<TimeSeriesSample> → Channel<SourceSample>
        // once all upstream producers emit SourceSample natively. For now, legacy
        // path always tags SourceKind.Mongo.
        var n = 0;
        await foreach (var sample in _dataChannel.Reader.ReadAllAsync(stoppingToken))
        {
            n++;
            var srcSample = new SourceSample(
                sample.Timestamp, sample.Values, SourceKind.Mongo,
                Interlocked.Increment(ref _seq));
            await _timeseriesStore.InsertSampleAsync(srcSample, stoppingToken);
            if (n % 500 == 1)
                _logger.LogDebug("[pipe] stored #{Count}: ts={Ts:HH:mm:ss}, vars={VarCount}",
                    n, sample.Timestamp, sample.Values.Count);
        }
    }

    /// <summary>
    /// Graceful shutdown: stop data source → stop publishers → complete channel.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // ── Stop multi-source readers first (unsubscribe + stop) ──
        if (_sourceReaders != null)
        {
            foreach (var reader in _sourceReaders)
            {
                reader.OnSample -= OnSourceSample;
                try { await reader.StopAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "[pipeline] Error stopping SourceReader {Kind}", reader.Kind); }
            }
        }

        _logger.LogInformation("[pipeline] Shutting down — stopping data source");
        try { await _dataSource.StopAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[pipeline] Error stopping data source"); }

        _logger.LogInformation("[pipeline] Stopping Modbus publisher");
        try { await _modbusPublisher.StopAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[pipeline] Error stopping Modbus publisher"); }

        _logger.LogInformation("[pipeline] Completing data channel");
        try { _dataChannel.Writer.Complete(); }
        catch (ChannelClosedException) { /* already completed — caller may have closed first */ }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("[pipeline] Shutdown complete");
    }

    /// <summary>
    /// Callback from IDataSource.Subscribe — writes data to Channel for decoupled consumption.
    /// Also harvests live variable names for the 3-pass merge (Pass 1 matching).
    /// Core performs zero transformation on the payload.
    ///
    /// F15: Continuous harvest — after the startup window completes, unseen variable
    /// names still trigger a throttled reharvest (RefreshFromLiveAsync). This ensures
    /// device-switch at runtime (e.g. flecs→plc) populates the live-name set without
    /// a process restart. The debounce (MongoReharvestDebounceMs) prevents rebuild
    /// storms during bulk ingestion.
    /// </summary>
    private int _rxCount;
    private const int BatchesForNameCollection = 5; // signal after N batches (fallback)

    private void OnDataArrived(DateTime timestamp, IReadOnlyDictionary<string, double> variables)
    {
        var sample = new TimeSeriesSample(timestamp, variables);
        _dataChannel.Writer.TryWrite(sample);

        // Harvest variable names — during startup window AND continuously.
        foreach (var key in variables.Keys)
            _liveNames.TryAdd(key, 0);

        // Startup window: signal ready after enough batches
        if (!_liveNamesReady.Task.IsCompleted)
        {
            var batch = Interlocked.Increment(ref _batchesSeen);
            var threshold = _options.MongoNameCollectionBatches > 0
                ? _options.MongoNameCollectionBatches
                : BatchesForNameCollection;
            if (batch >= threshold)
                _liveNamesReady.TrySetResult();
        }

        // F15 continuous harvest: ANY growth of the live-name set after the startup
        // window (even one that timed out with 0 names) triggers a throttled refresh.
        // Covers: (a) emulator starting after the window expired; (b) runtime device
        // switch (flecs→plc). Growth-based, ¬ newNameDetected-after-completion — the
        // latter misses names arriving between window expiry and batch threshold.
        if (_startupWindowDone)
        {
            var currentCount = _liveNames.Count;
            if (currentCount > Volatile.Read(ref _lastHarvestedCount))
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                var lastTicks = Interlocked.Read(ref _lastReharvestTicks);
                if ((nowTicks - lastTicks) >= _reharvestDebounceTicks
                    && Interlocked.CompareExchange(ref _lastReharvestTicks, nowTicks, lastTicks) == lastTicks)
                {
                    Volatile.Write(ref _lastHarvestedCount, currentCount);
                    var liveNames = _liveNames.Keys.ToList();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _configStore.RefreshFromLiveAsync(liveNames);
                            _logger.LogInformation(
                                "[pipeline] Reharvested {Count} live names after detecting growth",
                                liveNames.Count);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[pipeline] Reharvest failed");
                        }
                    });
                }
            }
        }

        var c = Interlocked.Increment(ref _rxCount);
        if (c <= 3)
            _logger.LogDebug("[pipe] rx #{Count}: {VarCount} vars @ {Ts:HH:mm:ss}",
                c, variables.Count, timestamp);
    }

    /// <summary>
    /// Callback from ISourceReader.OnSample — converts SourceSample to TimeSeriesSample
    /// and writes to the shared channel. Variable names are harvested into _liveNames
    /// for the same 3-pass merge as the legacy path.
    /// </summary>
    private void OnSourceSample(SourceSample sample)
    {
        var tsSample = new TimeSeriesSample(sample.Timestamp, sample.Variables);
        _dataChannel.Writer.TryWrite(tsSample);

        // Harvest variable names — same pattern as OnDataArrived.
        foreach (var key in sample.Variables.Keys)
            _liveNames.TryAdd(key, 0);
    }
}
