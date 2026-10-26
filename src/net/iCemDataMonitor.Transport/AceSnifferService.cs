using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Transport;

/// <summary>
/// Background service that passively sniffs ACE multicast Ethernet frames
/// via SharpPcap (Npcap), decodes signals per LantableRegistry, emits decoded
/// values for B4 normalization, and supports capture-to-file in pcapng format.
///
/// State machine: STOPPED → STARTING → ADAPTER_MISSING → RUNNING → STOPPED
/// Adapter selection: MANUAL only (config AceSniffer:Adapter). Auto-probe removed.
/// BPF filter: "ether multicast", promiscuous, 64 KB buffer.
///
/// Seam: IAceFrameSource abstracts live adapter from replay tests.
/// </summary>
public sealed class AceSnifferService : BackgroundService
{
    private readonly AceSnifferOptions _options;
    private readonly ILogger<AceSnifferService> _logger;
    private readonly LantableRegistry _lantable;
    private readonly Func<string, IAceFrameSource> _createSource;
    // True when frame source factory was injected (test seam). Manual adapter
    // validation (match against enumerated adapters) applies ONLY to the
    // production live-source factory — injected factories resolve names themselves.
    private readonly bool _hasCustomSourceFactory;

    // ═══ State ═══
    private readonly object _stateLock = new();
    private string _state = "STOPPED";
    private bool _up;

    // ═══ Pause/resume gate (B5 start/stop control via REST) ═══
    private readonly ManualResetEventSlim _runGate = new(true); // true = running by default
    private string _activeAdapter = "";
    private string _activeMac = "";

    // ═══ Available adapters cache (for manual selection) ═══
    private List<AdapterInfo> _availableAdapters = new();

    // Frame tracking
    private long _totalFrames;
    private long _aceFrames;          // frames passing IsAceFrame (152 B, LLC 030308/030309)
    private long _decodeErrors;
    private long _unknownVectors;
    private int _prefix030308;
    private int _prefix030309;
    private DateTime _lastFrameTime = DateTime.MinValue;
    // Distinct source MACs among ACE frames (witness surface, 2026-08-30:
    // sniffer-witness gap — frames were counted but never logged)
    private readonly ConcurrentDictionary<string, byte> _distinctMacs = new();

    // Rate tracking: 1-second sliding window
    private long _fpsWindow;
    private DateTime _fpsWindowStart = DateTime.MinValue;
    private double _framesPerSec;

    // Per-service-vector tracking
    private readonly ConcurrentDictionary<uint, SvTracker> _svTrackers = new();

    // Per-equipment tracking
    private readonly ConcurrentDictionary<long, EqTracker> _eqTrackers = new();

    // Mapping: sv number → local vectorId (stable incrementing counter)
    private readonly ConcurrentDictionary<uint, int> _svToVectorId = new();
    private int _nextVectorId;

    /// <summary>
    /// Last decoded value cache for late-joiner (XPUB pattern) and signal preview.
    /// Key = signal canonical name, Value = (value, uom, isSentinel, timestamp).
    /// </summary>
    private readonly ConcurrentDictionary<string, AceSignalSnapshot> _lastSignals = new();

    // ═══ Witness cadence (per-minute diagnostic log line) ═══
    private DateTime _lastWitnessUtc = DateTime.MinValue;
    private long _witnessLastAceFrames;

    // ═══ Capture state ═══
    private readonly object _captureLock = new();
    private bool _capturing;
    private DateTime _captureStartedAt;
    private string? _captureTargetPath;
    private readonly ConcurrentQueue<byte[]> _captureRing = new();
    private int _captureFrameCount;
    private const int MaxRingFrames = 200_000; // ~200s of 1 kHz worst case

    // ═══ Events ═══

    /// <summary>
    /// Fired for every decoded ACE frame. B4 normalizer subscribes.
    /// Signature: (timestampUtc, equipmentNumber, serviceVectorNumber, decodedSignals).
    /// Canonical names: "Lan/..." from LanVariableCatalog/UnifiedVariableRegistry where known;
    /// falls back to LantableRegistry signal name.
    /// </summary>
    public event Action<DateTime, uint, uint, IReadOnlyList<DecodedLanValue>>? AceFrameDecoded;

    public AceSnifferService(
        IOptions<AceSnifferOptions> options,
        ILogger<AceSnifferService> logger)
        : this(options, logger, LoadLantable(), CreateLiveSource)
    {
    }

    /// <summary>Test constructor: inject frame source factory.</summary>
    internal AceSnifferService(
        IOptions<AceSnifferOptions> options,
        ILogger<AceSnifferService> logger,
        LantableRegistry lantable,
        Func<string, IAceFrameSource> createSource)
    {
        _options = options.Value;
        _logger = logger;
        _lantable = lantable;
        _createSource = createSource;
        _hasCustomSourceFactory = true;
    }

    private static LantableRegistry LoadLantable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "templates", "lantable_registry.json");
        return LantableRegistry.Load(path);
    }

    private static IAceFrameSource CreateLiveSource(string adapterName)
    {
        return new SharpPcapAceSource(adapterName);
    }

    // ═══ Public API (for B5 REST wiring) ═══

    /// <summary>Get aggregated sniffer status matching sniffer-status.json "ace" shape.</summary>
    public AceSnifferStatus GetStatus()
    {
        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            return new AceSnifferStatus
            {
                Adapter = _activeAdapter,
                MacAddress = _activeMac,
                Up = _up,
                FramesPerSec = _framesPerSec,
                LastFrameAgeSec = _lastFrameTime == DateTime.MinValue
                    ? -1
                    : (now - _lastFrameTime).TotalSeconds,
                TotalFrames = _totalFrames,
                AceFrames = Interlocked.Read(ref _aceFrames),
                DistinctMacs = _distinctMacs.Count,
                DecodeErrors = (int)_decodeErrors,
                PrefixCounts = new Dictionary<string, int>
                {
                    ["030308"] = _prefix030308,
                    ["030309"] = _prefix030309,
                },
                Equipment = _eqTrackers.Values.Select(e => new AceEquipmentStatus
                {
                    EquipmentNumber = e.EquipmentNumber,
                    MacAddress = e.MacAddress,
                    VectorCount = e.VectorsSeen.Count,
                    TotalFrameRate = e.FrameRate,
                    TotalFrames = e.TotalFrames,
                }).ToList(),
                State = _state,
                UnknownVectors = (int)_unknownVectors,
                AvailableAdapters = _availableAdapters.ToList(),
            };
        }
    }

    /// <summary>Get per-service-vector info matching sniffer-service-vectors.json shape.</summary>
    public IReadOnlyList<AceServiceVectorInfo> GetServiceVectors()
    {
        var now = DateTime.UtcNow;
        return _svTrackers.Values
            .OrderBy(sv => sv.VectorId)
            .Select(sv => new AceServiceVectorInfo
            {
                VectorId = sv.VectorId,
                Name = sv.Name,
                IsUnknown = sv.IsUnknown,
                FrameRate = sv.FrameRate,
                LastSeenSec = sv.LastFrameTime == DateTime.MinValue
                    ? -1
                    : (now - sv.LastFrameTime).TotalSeconds,
                DecodedSignalCount = sv.SignalCount,
            })
            .ToList();
    }

    /// <summary>
    /// Get decoded signal preview for a given local vectorId.
    /// Returns last-known values from the cached late-joiner snapshot.
    /// Matches sniffer-signal-preview.json shape.
    /// </summary>
    public IReadOnlyList<AceSignalPreview> GetSignalPreview(int vectorId)
    {
        // Find the sv number for this vectorId
        var svPair = _svToVectorId.FirstOrDefault(kv => kv.Value == vectorId);
        if (svPair.Key == 0) return Array.Empty<AceSignalPreview>();

        uint svNumber = svPair.Key;
        var results = new List<AceSignalPreview>();
        foreach (var kvp in _lastSignals)
        {
            // Key format: "svNumber::signalName"
            if (kvp.Key.StartsWith($"{svNumber}::"))
            {
                var snap = kvp.Value;
                results.Add(new AceSignalPreview
                {
                    Name = snap.SignalName,
                    Value = snap.IsSentinel ? -9999.0 : snap.Value,
                    Uom = snap.Uom,
                    Sentinel = snap.IsSentinel,
                });
            }
        }
        return results;
    }

    /// <summary>Start/resume the sniffer (sets run gate; no-op if already running).</summary>
    public Task StartSnifferAsync()
    {
        _runGate.Set();
        if (_state is "PAUSED" or "STOPPED")
            SetState(_up ? "RUNNING" : "STARTING");
        return Task.CompletedTask;
    }

    /// <summary>W10.1: truth signal for lamps — RUNNING/STARTING means the run gate is set.</summary>
    public bool IsRunning { get { lock (_stateLock) { return _state is "RUNNING" or "STARTING"; } } }

    /// <summary>Stop/pause the sniffer (resets run gate; frames buffered but not processed).</summary>
    public Task StopSnifferAsync()
    {
        _runGate.Reset();
        SetState("PAUSED");
        return Task.CompletedTask;
    }

    /// <summary>Start pcapng capture to file. Optional auto-stop after durationSeconds.</summary>
    public Task StartCaptureAsync(int? durationSeconds = null)
    {
        lock (_captureLock)
        {
            if (_capturing) return Task.CompletedTask;

            _capturing = true;
            _captureStartedAt = DateTime.UtcNow;
            _captureFrameCount = 0;

            var dir = _options.CaptureDir;
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(AppContext.BaseDirectory, dir);
            Directory.CreateDirectory(dir);

            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            _captureTargetPath = Path.Combine(dir, $"ace_capture_{ts}.pcapng");

            _logger.LogInformation("[Ace] Capture started → {Path}", _captureTargetPath);

            if (durationSeconds.HasValue && durationSeconds.Value > 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(durationSeconds.Value));
                    await StopCaptureAsync();
                });
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Stop capture and flush pcapng to disk.</summary>
    public Task StopCaptureAsync()
    {
        string? path;
        List<byte[]> frames;
        int frameCount;
        lock (_captureLock)
        {
            if (!_capturing) return Task.CompletedTask;
            _capturing = false;
            path = _captureTargetPath;

            // Drain the ring UNDER the lock: the flush below must see exactly
            // the frames captured in THIS window, and a concurrent
            // StartCaptureAsync must begin with an empty ring (Stop↔Start race
            // closed — previously the shared ring was written outside the lock).
            frames = new List<byte[]>(_captureRing.Count);
            while (_captureRing.TryDequeue(out var f))
                frames.Add(f);
            frameCount = _captureFrameCount;
        }

        if (path != null)
        {
            try
            {
                WritePcapng(path, frames);
                _logger.LogInformation("[Ace] Capture saved: {Path} ({Frames} frames)", path, frameCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Ace] Failed to write capture file {Path}", path);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Get current capture status.</summary>
    public AceCaptureStatus GetCaptureStatus()
    {
        lock (_captureLock)
        {
            return new AceCaptureStatus
            {
                Capturing = _capturing,
                FramesCaptured = _captureFrameCount,
                ElapsedSec = _capturing
                    ? (DateTime.UtcNow - _captureStartedAt).TotalSeconds
                    : 0,
                TargetPath = _captureTargetPath,
                StartedAt = _captureStartedAt.ToString("O"),
            };
        }
    }

    /// <summary>Path to the last completed capture file, or null.</summary>
    public string? GetCaptureFilePath()
    {
        lock (_captureLock)
        {
            return !_capturing ? _captureTargetPath : null;
        }
    }

    // ═══ BackgroundService ═══

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // CRITICAL: yield BEFORE any potentially blocking sync work (adapter
        // enumeration, source open). BackgroundService.StartAsync runs
        // ExecuteAsync on the caller thread until the first incomplete await.
        await Task.Yield();
        SetState("STARTING");
        _logger.LogInformation("[Ace] AceSnifferService starting. Adapter={Adapter}, CaptureDir={CaptureDir}",
            _options.Adapter ?? "(manual — not configured)", _options.CaptureDir);

        if (!_options.Enabled)
        {
            _logger.LogInformation("[Ace] Sniffer disabled via config — staying in STOPPED");
            SetState("STOPPED");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSnifferLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Ace] Unexpected error in sniffer loop — watchdog restart in 5s");
                SetState("STARTING");
                _up = false;
                await SafeDelay(5000, ct);
            }
        }

        // Finalize any active capture
        await StopCaptureAsync();
        SetState("STOPPED");
        _up = false;
        _logger.LogInformation("[Ace] AceSnifferService stopped");
    }

    private async Task RunSnifferLoopAsync(CancellationToken ct)
    {
        SetState("STARTING");

        // ── 1. Resolve adapter (MANUAL or AUTO-SELECT) ──
        // Refresh available adapters list for operator visibility
        RefreshAvailableAdapters();

        // Log ALL available adapters on startup for diagnostics
        if (_availableAdapters.Count > 0)
        {
            _logger.LogInformation("[Ace] Available network adapters ({Count}):", _availableAdapters.Count);
            foreach (var a in _availableAdapters)
                _logger.LogInformation("[Ace]   → {Name} ({Description})", a.Name, a.Description);
        }
        else
        {
            _logger.LogWarning("[Ace] No network adapters enumerated (Npcap driver may not be installed)");
        }

        string? adapterName = null;
        if (!string.IsNullOrWhiteSpace(_options.Adapter))
        {
            if (_hasCustomSourceFactory)
            {
                // Injected factory (test seam) — it resolves the name itself;
                // manual validation against enumerated adapters is prod-only.
                adapterName = _options.Adapter;
            }
            else
            {
                var match = _availableAdapters.FirstOrDefault(a =>
                    (a.Name?.Contains(_options.Adapter, StringComparison.OrdinalIgnoreCase) == true) ||
                    (a.Description?.Contains(_options.Adapter, StringComparison.OrdinalIgnoreCase) == true) ||
                    (a.Description?.Replace("(R)", "").Replace("(TM)", "").Contains(_options.Adapter, StringComparison.OrdinalIgnoreCase) == true));
                if (match != null)
                {
                    adapterName = match.Name;
                    _logger.LogInformation("[Ace] Using configured adapter: {Adapter} ({Desc})",
                        adapterName, match.Description);
                }
                else if (_availableAdapters.Count == 0)
                {
                    // No real adapters enumerated (Npcap not installed yet / driver down).
                    // Accept the configured name verbatim — the live source will surface
                    // a real open error if the name is wrong.
                    adapterName = _options.Adapter;
                    _logger.LogInformation("[Ace] No adapters enumerated; using configured name verbatim: {Adapter}", adapterName);
                }
                else
                {
                    _logger.LogError("[Ace] Configured adapter '{Adapter}' not found. Available adapters:",
                        _options.Adapter);
                    foreach (var a in _availableAdapters)
                        _logger.LogError("[Ace]   → {Name} ({Description})", a.Name, a.Description);

                    SetState("ADAPTER_MISSING");
                    await SafeDelay((int)TimeSpan.FromSeconds(_options.RetryIntervalSeconds).TotalMilliseconds, ct);
                    return;
                }
            }
        }
        else
        {
            // Auto-select: first Ethernet adapter (filter by common NIC descriptions).
            // Excludes loopback, WAN miniport, and virtual adapters.
            var ethernetAdapters = _availableAdapters
                .Where(a => a.Description != null && (
                    a.Description.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                    a.Description.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                    a.Description.Contains("Gigabit", StringComparison.OrdinalIgnoreCase) ||
                    a.Description.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
                    a.Description.Contains("Broadcom", StringComparison.OrdinalIgnoreCase)))
                .Where(a => !a.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                .Where(a => !a.Description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (ethernetAdapters.Count > 0)
            {
                adapterName = ethernetAdapters[0].Name;
                _logger.LogInformation("[Ace] Auto-selected Ethernet adapter: {Adapter} ({Desc}) — configure AceSniffer:Adapter to override",
                    adapterName, ethernetAdapters[0].Description);
            }
            else
            {
                _logger.LogWarning("[Ace] No adapter configured and no Ethernet adapter found for auto-select. Available adapters:");
                foreach (var a in _availableAdapters)
                    _logger.LogWarning("[Ace]   → {Name} ({Description})", a.Name, a.Description);

                SetState("ADAPTER_MISSING");
                await SafeDelay((int)TimeSpan.FromSeconds(_options.RetryIntervalSeconds).TotalMilliseconds, ct);
                return;
            }
        }

        _activeAdapter = adapterName;
        _logger.LogInformation("[Ace] Using adapter: {Adapter}", adapterName);

        // ── 2. Open frame source (subscribe BEFORE start to avoid race) ──
        IAceFrameSource? source = null;
        var frameChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        try
        {
            source = _createSource(adapterName);
            _activeMac = source.MacAddress;

            // Subscribe BEFORE Start — replay sources may fire synchronously
            source.FrameReceived += frame =>
            {
                frameChannel.Writer.TryWrite(frame);
            };

            source.Start();
            _logger.LogInformation("[Ace] Frame source started on {Adapter} (MAC {Mac})",
                adapterName, _activeMac);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ace] Failed to open adapter {Adapter}", adapterName);
            frameChannel.Writer.Complete();
            await SafeDelay(5000, ct);
            return;
        }

        // ── 3. Spin up stats timer ──
        using var statsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var statsTask = RunStatsLoopAsync(statsCts.Token);

        _up = true;
        SetState("RUNNING");
        _logger.LogInformation("[Ace] Sniffer running on {Adapter}", adapterName);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame;
                try
                {
                    frame = await frameChannel.Reader.ReadAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                // ═══ B5 pause/resume gate — blocks frame processing when stopped ═══
                _runGate.Wait(ct);

                if (frame.Length > 0)
                {
                    ProcessFrame(frame);
                }
            }
        }
        finally
        {
            _up = false;
            statsCts.Cancel();
            await SafeIgnore(statsTask);

            source.Stop();
            source.Dispose();
            _logger.LogInformation("[Ace] Frame source closed");
        }
    }

    // ── Available adapter enumeration (for manual selection) ──

    private void RefreshAvailableAdapters()
    {
        try
        {
            var devices = SharpPcap.LibPcap.LibPcapLiveDeviceList.Instance;
            _availableAdapters = devices
                .Select(d => new AdapterInfo { Name = d.Name ?? "", Description = d.Description ?? "" })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ace] Failed to enumerate adapters (Npcap installed?)");
            _availableAdapters = new List<AdapterInfo>();
        }
    }

    // ── Frame processing ──

    internal void ProcessFrame(byte[] frame)
    {
        Interlocked.Increment(ref _totalFrames);
        _lastFrameTime = DateTime.UtcNow;
        RecordFps(1);

        // Track prefix
        if (frame.Length >= 17)
        {
            if (frame[14] == 0x03 && frame[15] == 0x03 && frame[16] == 0x08)
                Interlocked.Increment(ref _prefix030308);
            else if (frame[14] == 0x03 && frame[15] == 0x03 && frame[16] == 0x09)
                Interlocked.Increment(ref _prefix030309);
        }

        // Validate ACE frame
        if (!IsAceFrame(frame))
            return;

        // Witness counters (2026-08-30 sniffer-witness gap)
        Interlocked.Increment(ref _aceFrames);
        _distinctMacs.TryAdd(ExtractSrcMac(frame), 0);

        // Feed capture ring
        lock (_captureLock)
        {
            if (_capturing)
            {
                _captureRing.Enqueue(frame);
                _captureFrameCount++;
                // Trim ring
                while (_captureRing.Count > MaxRingFrames)
                    _captureRing.TryDequeue(out _);
            }
        }

        // Extract equipment and service vector
        uint eq = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(20, 4));
        uint sv = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(24, 4));

        // Track equipment
        var eqTracker = _eqTrackers.GetOrAdd(eq,
            _ => new EqTracker { EquipmentNumber = (int)eq, MacAddress = ExtractSrcMac(frame) });
        eqTracker.Touch();
        eqTracker.VectorsSeen.TryAdd(sv, 0);

        // Resolve service vector definition
        if (!_lantable.TryGet(sv, out var svDef))
        {
            Interlocked.Increment(ref _unknownVectors);
            // Still track it as unknown
            GetOrCreateSvTracker(sv, $"0x{sv:X8}", isUnknown: true).Touch();
            return;
        }

        var svTracker = GetOrCreateSvTracker(sv, svDef.Name, isUnknown: false);
        svTracker.SignalCount = svDef.Signals.Count;
        svTracker.Touch();

        // Decode signals
        IReadOnlyList<DecodedLanValue> decoded;
        try
        {
            decoded = LantableRegistry.DecodeSignals(frame, svDef);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _decodeErrors);
            _logger.LogWarning(ex, "[Ace] Decode error for sv 0x{Sv:X8}", sv);
            return;
        }

        // Cache last values (late-joiner)
        foreach (var sig in decoded)
        {
            var key = $"{sv}::{sig.SignalName}";
            _lastSignals[key] = new AceSignalSnapshot
            {
                SignalName = sig.SignalName,
                Value = sig.Value ?? 0,
                Uom = sig.Uom,
                IsSentinel = sig.IsSentinel,
            };
        }

        // Emit for B4 normalization
        AceFrameDecoded?.Invoke(DateTime.UtcNow, eq, sv, decoded);
    }

    private static bool IsAceFrame(byte[] frame) =>
        frame.Length == 152
        && frame[14] == 0x03
        && frame[15] == 0x03
        && (frame[16] == 0x08 || frame[16] == 0x09);

    private static string ExtractSrcMac(byte[] frame)
    {
        if (frame.Length < 12) return "";
        return $"{frame[6]:X2}:{frame[7]:X2}:{frame[8]:X2}:{frame[9]:X2}:{frame[10]:X2}:{frame[11]:X2}";
    }

    private SvTracker GetOrCreateSvTracker(uint svNumber, string name, bool isUnknown)
    {
        return _svTrackers.GetOrAdd(svNumber, _ =>
        {
            var id = Interlocked.Increment(ref _nextVectorId);
            _svToVectorId[svNumber] = id;
            return new SvTracker { SvNumber = svNumber, VectorId = id, Name = name, IsUnknown = isUnknown };
        });
    }

    // ── Statistics at 1 Hz ──

    private async Task RunStatsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SafeDelay(1000, ct);
            if (ct.IsCancellationRequested) break;

            RotateFpsWindow();
            RotateSvRates();
            RotateEqRates();
            LogWitnessIfNeeded();
        }
    }

    // ── Witness log (per-minute, 2026-08-30) ──
    // The sniffer counted frames but logged nothing while running — the
    // sniffer-witness gap. One line per minute (cadence constant) into the
    // host log with the counters an operator needs to answer "is the wire
    // actually delivering ACE frames, and for which equipment?".

    internal const int WitnessIntervalSeconds = 60;

    private void LogWitnessIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (_lastWitnessUtc != DateTime.MinValue &&
            (now - _lastWitnessUtc).TotalSeconds < WitnessIntervalSeconds)
            return;
        _lastWitnessUtc = now;
        _logger.LogInformation("[Ace] {Witness}", BuildWitnessLine(now));
    }

    internal string BuildWitnessLine(DateTime now)
    {
        long aceFrames = Interlocked.Read(ref _aceFrames);
        long aceDelta = aceFrames - _witnessLastAceFrames;
        _witnessLastAceFrames = aceFrames;
        double lastAgo = _lastFrameTime == DateTime.MinValue ? -1 : (now - _lastFrameTime).TotalSeconds;
        var equipment = _eqTrackers.Values
            .OrderByDescending(e => e.TotalFrames)
            .Select(e => $"0x{e.EquipmentNumber:X8}:{e.TotalFrames}f");
        return $"witness: ace={aceFrames} (+{aceDelta}/interval) total={_totalFrames} " +
               $"lastFrameAgo={lastAgo:F1}s distinctMacs={_distinctMacs.Count} " +
               $"equipment=[{string.Join(" ", equipment)}]";
    }

    private void RecordFps(int count)
    {
        lock (_stateLock)
        {
            _fpsWindow += count;
        }
    }

    private void RotateFpsWindow()
    {
        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            if (_fpsWindowStart == DateTime.MinValue)
            {
                _fpsWindowStart = now;
                _fpsWindow = 0;
                return;
            }
            double elapsed = (now - _fpsWindowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                _framesPerSec = _fpsWindow / elapsed;
                _fpsWindow = 0;
                _fpsWindowStart = now;
            }
        }
    }

    private void RotateSvRates()
    {
        var now = DateTime.UtcNow;
        foreach (var sv in _svTrackers.Values)
        {
            sv.RotateRate(now);
        }
    }

    private void RotateEqRates()
    {
        var now = DateTime.UtcNow;
        foreach (var eq in _eqTrackers.Values)
        {
            eq.RotateRate(now);
        }
    }

    // ── Pcapng Writer ──

    private static void WritePcapng(string path, IReadOnlyCollection<byte[]> frames)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // Section Header Block
        WritePcapngBlock(bw, 0x0A0D0D0A, body =>
        {
            body.Write(0x1A2B3C4Du);  // BOM (little-endian)
            body.Write((ushort)1);    // Major version
            body.Write((ushort)0);    // Minor version
            body.Write(0xFFFFFFFFFFFFFFFFuL); // Section length (-1 = unknown)
        });

        // Interface Description Block (linktype 1 = Ethernet)
        WritePcapngBlock(bw, 0x00000001u, body =>
        {
            body.Write((ushort)1);     // LinkType = Ethernet
            body.Write((ushort)0);     // Reserved
            body.Write(65535u);        // SnapLen
        });

        // Enhanced Packet Blocks
        long tsBase = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        int frameIdx = 0;
        foreach (var frame in frames)
        {
            long tsNs = tsBase + (frameIdx * 1_000_000_000L / 6); // simulate 6 fps spacing
            frameIdx++;

            uint tsHigh = (uint)(tsNs >> 32);
            uint tsLow = (uint)(tsNs & 0xFFFFFFFF);

            WritePcapngBlock(bw, 0x00000006u, body =>
            {
                body.Write(0);          // Interface ID
                body.Write(tsHigh);
                body.Write(tsLow);
                body.Write(frame.Length); // Captured length
                body.Write(frame.Length); // Original length
                body.Write(frame);
            });
        }
    }

    private static void WritePcapngBlock(BinaryWriter bw, uint blockType, Action<BinaryWriter> writeBody)
    {
        using var ms = new MemoryStream();
        using var bodyWriter = new BinaryWriter(ms);
        writeBody(bodyWriter);
        bodyWriter.Flush();
        byte[] body = ms.ToArray();

        int totalLen = 12 + body.Length;
        // Pad to 4-byte boundary
        int padLen = (4 - (totalLen % 4)) % 4;
        totalLen += padLen;

        bw.Write(blockType);
        bw.Write(totalLen);
        bw.Write(body);
        for (int i = 0; i < padLen; i++)
            bw.Write((byte)0);
        bw.Write(totalLen); // trailing block length
    }

    // ═══ Helpers ═══

    private void SetState(string state)
    {
        lock (_stateLock) { _state = state; }
    }

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
    }

    private static async Task SafeIgnore(Task t)
    {
        try { await t; } catch { }
    }

    // ═══ Tracker types ═══

    private sealed class SvTracker
    {
        public uint SvNumber;
        public int VectorId;
        public string Name = "";
        public bool IsUnknown;
        public int SignalCount;

        private long _windowFrames;
        private DateTime _windowStart = DateTime.MinValue;
        public double FrameRate;
        public DateTime LastFrameTime = DateTime.MinValue;

        public void Touch()
        {
            LastFrameTime = DateTime.UtcNow;
            Interlocked.Increment(ref _windowFrames);
        }

        public void RotateRate(DateTime now)
        {
            if (_windowStart == DateTime.MinValue)
            {
                _windowStart = now;
                _windowFrames = 0;
                return;
            }
            double elapsed = (now - _windowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                FrameRate = Interlocked.Read(ref _windowFrames) / elapsed;
                Interlocked.Exchange(ref _windowFrames, 0);
                _windowStart = now;
            }
        }
    }

    private sealed class EqTracker
    {
        public int EquipmentNumber;
        public string MacAddress = "";
        public readonly ConcurrentDictionary<uint, byte> VectorsSeen = new();

        private long _windowFrames;
        private long _totalFrames;
        private DateTime _windowStart = DateTime.MinValue;
        public double FrameRate;

        public long TotalFrames => Interlocked.Read(ref _totalFrames);

        public void Touch()
        {
            Interlocked.Increment(ref _totalFrames);
            Interlocked.Increment(ref _windowFrames);
        }

        public void RotateRate(DateTime now)
        {
            if (_windowStart == DateTime.MinValue)
            {
                _windowStart = now;
                _windowFrames = 0;
                return;
            }
            double elapsed = (now - _windowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                FrameRate = Interlocked.Read(ref _windowFrames) / elapsed;
                Interlocked.Exchange(ref _windowFrames, 0);
                _windowStart = now;
            }
        }
    }

    internal sealed class AceSignalSnapshot
    {
        public string SignalName = "";
        public double Value;
        public string Uom = "";
        public bool IsSentinel;
    }
}