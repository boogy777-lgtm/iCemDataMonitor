using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Transport;

/// <summary>
/// Background service that opens a physical COM port (FLECS/PLC device),
/// forwards raw bytes to a virtual com0com pair port for legacy iCem,
/// parses CSV lines per the active .ssf template, and emits parsed values
/// via an event for downstream consumers (B4 normalization, B5 REST).
///
/// State machine: STARTING → RUNNING | PORT_BUSY | PAIR_MISSING → (watchdog) → STARTING
/// Trade-off: if this service dies, legacy iCem starves (README §12.3.2).
/// </summary>
public sealed class SerialTeeService : BackgroundService
{
    private readonly TeeOptions _options;
    private readonly IConfigStore _configStore;
    private readonly ILogger<SerialTeeService> _logger;
    private readonly Func<string, ISerialPort> _createPhysicalPort;
    private readonly Func<string, ISerialPort> _createVirtualPort;

    // ═══ State ═══
    private readonly object _stateLock = new();
    private string _state = "STOPPED";
    private bool _up;

    // ═══ Pause/resume gate (B5 start/stop control via REST) ═══
    private readonly ManualResetEventSlim _runGate = new(true); // true = running by default
    private bool _physicalPortBusy;
    private bool _com0comPairOk;
    private long _totalBytesForwarded;
    private long _totalLinesParsed;
    private int _forwardErrors;
    private DateTime _lastForwardTime = DateTime.MinValue;
    private DateTime _lastParseTime = DateTime.MinValue;

    // Rate tracking: 1-second sliding window
    private long _bytesFwdWindow;
    private long _linesParsedWindow;
    private DateTime _windowStart = DateTime.MinValue;
    private double _bytesForwardedPerSec;
    private double _bytesParsedPerSec;

    /// <summary>Fired when a CSV line is parsed: (timestamp, named-variable→value map).</summary>
    public event Action<DateTime, IReadOnlyDictionary<string, double>>? LineParsed;

    public SerialTeeService(
        IOptions<TeeOptions> options,
        IConfigStore configStore,
        ILogger<SerialTeeService> logger)
        : this(options, configStore, logger,
              name => new SerialPortAdapter(name,
                  options.Value.BaudRate, options.Value.Parity,
                  options.Value.DataBits, options.Value.StopBits),
              name => new SerialPortAdapter(name,
                  options.Value.BaudRate, options.Value.Parity,
                  options.Value.DataBits, options.Value.StopBits))
    {
    }

    /// <summary>Test constructor: inject serial port factories.</summary>
    internal SerialTeeService(
        IOptions<TeeOptions> options,
        IConfigStore configStore,
        ILogger<SerialTeeService> logger,
        Func<string, ISerialPort> createPhysicalPort,
        Func<string, ISerialPort> createVirtualPort)
    {
        _options = options.Value;
        _configStore = configStore;
        _logger = logger;
        _createPhysicalPort = createPhysicalPort;
        _createVirtualPort = createVirtualPort;
    }

    // ═══ Public API (for B5 REST wiring) ═══

    public ComTeeStatus GetStatus()
    {
        var active = _configStore.GetActiveConfig();
        var templateName = active?.Name ?? "";
        var now = DateTime.UtcNow;

        lock (_stateLock)
        {
            return new ComTeeStatus
            {
                PhysicalPort = _options.PhysicalPort,
                Baud = _options.BaudRate,
                TemplateName = templateName,
                Up = _up,
                BytesForwardedPerSec = _bytesForwardedPerSec,
                BytesParsedPerSec = _bytesParsedPerSec,
                ForwardErrors = _forwardErrors,
                LastForwardAgeSec = _lastForwardTime == DateTime.MinValue
                    ? -1
                    : (now - _lastForwardTime).TotalSeconds,
                LastParseAgeSec = _lastParseTime == DateTime.MinValue
                    ? -1
                    : (now - _lastParseTime).TotalSeconds,
                Com0comPairOk = _com0comPairOk,
                PhysicalPortBusy = _physicalPortBusy,
                State = _state,
            };
        }
    }

    /// <summary>Start/resume the tee service (sets run gate; no-op if already running).</summary>
    public Task StartManuallyAsync(CancellationToken ct = default)
    {
        _runGate.Set();
        if (_state is "PAUSED" or "STOPPED")
            SetState(_up ? "RUNNING" : "STARTING");
        return Task.CompletedTask;
    }

    /// <summary>Stop/pause the tee service (resets run gate; bytes buffered but not forwarded).</summary>
    public Task StopManuallyAsync()
    {
        _runGate.Reset();
        SetState("PAUSED");
        return Task.CompletedTask;
    }

    // ═══ BackgroundService ═══

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // CRITICAL: yield BEFORE any synchronous blocking work (SerialPort.Read
        // loop). BackgroundService.StartAsync runs ExecuteAsync on the caller
        // thread until the first incomplete await — a sync blocking loop would
        // stall host startup (Kestrel never binds) forever.
        await Task.Yield();
        SetState("STARTING");
        _logger.LogInformation("[Tee] SerialTeeService starting. Physical={PhysicalPort}, Virtual={VirtualOutPort}, Bidir={Bidirectional}",
            _options.PhysicalPort, _options.VirtualOutPort, _options.Bidirectional);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunTeeLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tee] Unexpected error in tee loop — watchdog restart in 5s");
                SetState("STARTING");
                await SafeDelay(5000, ct);
            }
        }

        SetState("STOPPED");
        _logger.LogInformation("[Tee] SerialTeeService stopped");
    }

    private async Task RunTeeLoopAsync(CancellationToken ct)
    {
        ISerialPort? physicalPort = null;
        ISerialPort? virtualPort = null;
        Task? reverseBridge = null;
        var reverseCts = new CancellationTokenSource();

        try
        {
            // ── 1. Open physical port ──
            SetState("STARTING");
            _physicalPortBusy = false;

            try
            {
                physicalPort = _createPhysicalPort(_options.PhysicalPort);
                physicalPort.Open();
                _logger.LogInformation("[Tee] Physical port {Port} opened", _options.PhysicalPort);
            }
            catch (UnauthorizedAccessException)
            {
                _physicalPortBusy = true;
                SetState("PORT_BUSY");
                _logger.LogWarning("[Tee] Physical port {Port} is busy — retry in 5s", _options.PhysicalPort);
                await SafeDelay(5000, ct);
                return; // retry loop
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tee] Failed to open physical port {Port}", _options.PhysicalPort);
                SetState("STARTING");
                await SafeDelay(5000, ct);
                return;
            }

            // ── 2. Open virtual output port ──
            _com0comPairOk = false;
            try
            {
                virtualPort = _createVirtualPort(_options.VirtualOutPort);
                virtualPort.Open();
                _com0comPairOk = true;
                _logger.LogInformation("[Tee] Virtual port {Port} opened", _options.VirtualOutPort);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Tee] Virtual port {Port} missing — retry in 5s", _options.VirtualOutPort);
                SetState("PAIR_MISSING");
                physicalPort?.Dispose();
                await SafeDelay(5000, ct);
                return;
            }

            // ── 3. Both ports open — RUNNING ──
            _up = true;
            SetState("RUNNING");
            _logger.LogInformation("[Tee] Tee running: {Physical} → {Virtual}",
                _options.PhysicalPort, _options.VirtualOutPort);

            // ── 4. Bidirectional reverse bridge (COM1 → COM2) ──
            if (_options.Bidirectional)
            {
                reverseBridge = Task.Run(() => ReverseBridgeAsync(virtualPort, physicalPort, reverseCts.Token), ct);
            }

            // ── 5. Main read-parse-forward loop ──
            var lineBuf = new StringBuilder(1024);
            var readBuf = new byte[256];
            int fieldCount = GetActiveFieldCount();

            while (!ct.IsCancellationRequested)
            {
                // ═══ B5 pause/resume gate — blocks forwarding when stopped ═══
                _runGate.Wait(ct);

                int bytesRead;
                try
                {
                    bytesRead = physicalPort.Read(readBuf, 0, readBuf.Length);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "[Tee] Physical port read error — reconnecting");
                    _forwardErrors++;
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "[Tee] Physical port closed — reconnecting");
                    break;
                }

                if (bytesRead <= 0)
                    continue; // timeout, no data

                // Forward raw bytes to virtual output
                try
                {
                    virtualPort.Write(readBuf, 0, bytesRead);
                    virtualPort.Flush();
                    RecordForward(bytesRead);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Tee] Virtual port write error");
                    _forwardErrors++;
                    break;
                }

                // Accumulate lines and parse
                for (int i = 0; i < bytesRead; i++)
                {
                    char c = (char)readBuf[i];
                    if (c == '\n')
                    {
                        string line = lineBuf.ToString().TrimEnd('\r', '\n');
                        lineBuf.Clear();
                        if (line.Length > 0)
                            ParseAndEmitLine(line, fieldCount);
                    }
                    else
                    {
                        lineBuf.Append(c);
                    }
                }
            }
        }
        finally
        {
            _up = false;

            // Stop reverse bridge
            reverseCts.Cancel();
            if (reverseBridge != null)
            {
                try { await reverseBridge.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { }
            }
            reverseCts.Dispose();

            // Close ports
            virtualPort?.Dispose();
            physicalPort?.Dispose();

            if (!ct.IsCancellationRequested)
            {
                SetState("STARTING");
                _logger.LogInformation("[Tee] Tee loop ended — watchdog restart in 3s");
                await SafeDelay(3000, ct);
            }
        }
    }

    // ═══ Reverse bridge: COM1 (iCem commands) → COM2 (physical device) ═══

    private Task ReverseBridgeAsync(ISerialPort virtualPort, ISerialPort physicalPort, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var buf = new byte[256];
            _logger.LogInformation("[Tee] Reverse bridge active: {Virtual} → {Physical}",
                _options.VirtualOutPort, _options.PhysicalPort);

            while (!ct.IsCancellationRequested)
            {
                // ═══ B5 pause/resume gate — blocks reverse bridge when stopped ═══
                _runGate.Wait(ct);

                try
                {
                    int n = virtualPort.Read(buf, 0, buf.Length);
                    if (n <= 0)
                        continue;
                    physicalPort.Write(buf, 0, n);
                    physicalPort.Flush();
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        _logger.LogWarning(ex, "[Tee] Reverse bridge read error");
                    break;
                }
            }
            _logger.LogInformation("[Tee] Reverse bridge stopped");
        }, ct);
    }

    // ═══ CSV Parser (ported from FlecsTray_v3.cs FlecsParser) ═══

    private int GetActiveFieldCount()
    {
        var vars = _configStore.GetActiveVariables();
        // The .ssf template defines all fields; count gives us the max column count
        return vars.Count > 0 ? vars.Count : 34; // fallback: standard FLECS 4.53
    }

    /// <summary>
    /// Parse a comma-separated telemetry line.
    /// Returns null if line is invalid or too short.
    /// </summary>
    public static double[]? Parse(string line, int fieldCount)
    {
        if (string.IsNullOrEmpty(line) || fieldCount <= 0)
            return null;
        string[] parts = line.Split(',');
        if (parts.Length < fieldCount)
            return null;
        double[] values = new double[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            double val;
            if (!double.TryParse(parts[i].Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out val))
                val = 0.0;
            values[i] = val;
        }
        return values;
    }

    private void ParseAndEmitLine(string line, int fieldCount)
    {
        var values = Parse(line, fieldCount);
        if (values == null)
        {
            _forwardErrors++;
            return;
        }

        RecordParse();

        // Build named-variable map from active config
        var activeVars = _configStore.GetActiveVariables();
        if (activeVars.Count > 0)
        {
            var dict = new Dictionary<string, double>(activeVars.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var v in activeVars)
            {
                if (v.IsSpare)
                    continue;
                int idx = v.Position - 1; // Position is 1-based in .ssf
                if (idx >= 0 && idx < values.Length)
                    dict[v.Name] = values[idx];
            }
            LineParsed?.Invoke(DateTime.UtcNow, dict);
        }
        else
        {
            // No active config — emit position-keyed map
            var dict = new Dictionary<string, double>(values.Length);
            for (int i = 0; i < values.Length; i++)
                dict[$"col_{i}"] = values[i];
            LineParsed?.Invoke(DateTime.UtcNow, dict);
        }
    }

    // ═══ Metrics ═══

    private void RecordForward(int byteCount)
    {
        lock (_stateLock)
        {
            _totalBytesForwarded += byteCount;
            _bytesFwdWindow += byteCount;
            _lastForwardTime = DateTime.UtcNow;
            RotateWindow();
        }
    }

    private void RecordParse()
    {
        lock (_stateLock)
        {
            _totalLinesParsed++;
            _linesParsedWindow++;
            _lastParseTime = DateTime.UtcNow;
            RotateWindow();
        }
    }

    private void RotateWindow()
    {
        var now = DateTime.UtcNow;
        if (_windowStart == DateTime.MinValue)
        {
            _windowStart = now;
            return;
        }
        double elapsed = (now - _windowStart).TotalSeconds;
        if (elapsed >= 1.0)
        {
            _bytesForwardedPerSec = _bytesFwdWindow / elapsed;
            _bytesParsedPerSec = _linesParsedWindow / elapsed;
            _bytesFwdWindow = 0;
            _linesParsedWindow = 0;
            _windowStart = now;
        }
    }

    private void SetState(string state)
    {
        lock (_stateLock) { _state = state; }
    }

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }
}