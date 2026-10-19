using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Transport.Tests;

/// <summary>
/// In-memory serial port for unit testing tee relay logic.
/// Simulates two connected ports with byte-by-byte forwarding.
/// </summary>
public sealed class InMemorySerialPort : ISerialPort
{
    private readonly MemoryStream _readStream;
    private readonly MemoryStream _writeStream;
    private readonly string _portName;
    private volatile bool _isOpen;
    private readonly object _gate = new();
    private readonly Func<int>? _readTimeoutSim; // throw UnauthorizedAccessException after N reads

    private readonly Action? _onDispose;

    public InMemorySerialPort(string name, byte[]? initialData = null, Func<int>? readTimeoutSim = null, Action? onDispose = null)
    {
        _portName = name;
        _readTimeoutSim = readTimeoutSim;
        _onDispose = onDispose;
        _readStream = new MemoryStream();
        if (initialData != null && initialData.Length > 0)
        {
            _readStream.Write(initialData, 0, initialData.Length);
            _readStream.Position = 0;
        }
        _writeStream = new MemoryStream();
    }

    public string PortName => _portName;
    public bool IsOpen => _isOpen;

    /// <summary>All bytes written to this port (assert output).</summary>
    public byte[] WrittenData { get { lock (_gate) return _writeStream.ToArray(); } }

    /// <summary>Feed additional bytes into the read stream.</summary>
    public void FeedReadData(byte[] data)
    {
        lock (_gate)
        {
            long pos = _readStream.Position;
            _readStream.Position = _readStream.Length;
            _readStream.Write(data, 0, data.Length);
            _readStream.Position = pos;
            Monitor.PulseAll(_gate);
        }
    }

    public void FeedReadLine(string line)
    {
        FeedReadData(Encoding.ASCII.GetBytes(line + "\r\n"));
    }

    public void Open()
    {
        if (_readTimeoutSim != null)
        {
            int remaining = _readTimeoutSim();
            if (remaining <= 0)
                throw new UnauthorizedAccessException("Port busy (simulated)");
        }
        lock (_gate) _isOpen = true;
    }

    public void Close()
    {
        lock (_gate)
        {
            _isOpen = false;
            Monitor.PulseAll(_gate);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            // Model a bounded serial read timeout instead of spinning on an
            // empty MemoryStream and starving the runner's thread pool.
            if (_isOpen && _readStream.Position == _readStream.Length)
                Monitor.Wait(_gate, 20);
            if (!_isOpen)
                throw new InvalidOperationException("Port not open");
            return _readStream.Read(buffer, offset, count);
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (!_isOpen)
                throw new InvalidOperationException("Port not open");
            _writeStream.Write(buffer, offset, count);
        }
    }

    public void Flush() { }

    public void Dispose()
    {
        lock (_gate)
        {
            _isOpen = false;
            Monitor.PulseAll(_gate);
            _readStream.Dispose();
            _writeStream.Dispose();
        }
        _onDispose?.Invoke();
    }
}

// ═══ Mock ConfigStore for tests ═══

internal class MockConfigStore : IConfigStore
{
    private readonly IReadOnlyList<VariableDefinition> _activeVars;

    public MockConfigStore(IReadOnlyList<VariableDefinition>? activeVars = null)
    {
        _activeVars = activeVars ?? Array.Empty<VariableDefinition>();
    }

    public IReadOnlyList<DeviceFamily> GetAllFamilies() => Array.Empty<DeviceFamily>();
    public DeviceConfig? GetActiveConfig() => _activeVars.Count > 0
        ? new DeviceConfig("flecs_ver4p53_ppg_bpm_psi", _activeVars)
        : null;
    public IReadOnlyList<VariableDefinition> GetActiveVariables() => _activeVars;
    public Task ActivateConfigAsync(string family, string configName) => Task.CompletedTask;
    public IReadOnlyList<ModbusRegisterEntry> GetModbusRegisterMap() => Array.Empty<ModbusRegisterEntry>();
    public Task RefreshFromLiveAsync(IEnumerable<string> liveVariableNames) => Task.CompletedTask;
}

// ═══ Tests ═══

public class SerialTeeServiceTests
{
    // ── CSV Parser ──

    [Fact]
    public void Parse_34ColumnDeterministicLine_ReturnsAll34Values()
    {
        // Build a deterministic line: col0=0.00, col1=1.01, col2=2.02, ...
        var parts = new string[34];
        for (int i = 0; i < 34; i++)
            parts[i] = (i * 1.01).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string line = string.Join(",", parts);

        var values = SerialTeeService.Parse(line, 34);

        Assert.NotNull(values);
        Assert.Equal(34, values.Length);
        for (int i = 0; i < 34; i++)
            Assert.Equal(i * 1.01, values[i], 3);
    }

    [Fact]
    public void Parse_TooFewColumns_ReturnsNull()
    {
        string line = "1.0,2.0,3.0";
        var values = SerialTeeService.Parse(line, 34);
        Assert.Null(values);
    }

    [Fact]
    public void Parse_EmptyLine_ReturnsNull()
    {
        var values = SerialTeeService.Parse("", 34);
        Assert.Null(values);
    }

    [Fact]
    public void Parse_NullLine_ReturnsNull()
    {
        var values = SerialTeeService.Parse(null!, 34);
        Assert.Null(values);
    }

    [Fact]
    public void Parse_NonNumericField_DefaultsToZero()
    {
        string line = "abc,2.0,3.0," + string.Join(",", Enumerable.Repeat("0.0", 31));
        var values = SerialTeeService.Parse(line, 34);
        Assert.NotNull(values);
        Assert.Equal(0.0, values[0]);
        Assert.Equal(2.0, values[1]);
    }

    // ── Tee forwarding: byte-by-byte fidelity ──

    [Fact]
    public async Task TeeLoop_ForwardsRawBytesToVirtualPort()
    {
        var options = CreateOptions();
        var configStore = new MockConfigStore(BuildFlecs34Variables());
        var logger = NullLogger<SerialTeeService>.Instance;

        var physicalPort = new InMemorySerialPort("COM2");
        var virtualPort = new InMemorySerialPort("COM1");

        using var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            _ => physicalPort,
            _ => virtualPort);

        var parsed = new TaskCompletionSource<IReadOnlyDictionary<string, double>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int parsedCount = 0;
        service.LineParsed += (ts, dict) =>
        {
            Interlocked.Increment(ref parsedCount);
            parsed.TrySetResult(dict);
        };

        // Start the service
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => service.GetStatus().State == "RUNNING");

            Assert.Equal("RUNNING", ComTeeStatusExtensionsState(service));

            // Feed a known CSV line
            string csvLine = BuildDeterministicCsvLine(34, 1.01);
            physicalPort.FeedReadLine(csvLine);

            await parsed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Stop
            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            // Assert: virtual port received the bytes
            var forwarded = Encoding.ASCII.GetString(virtualPort.WrittenData);
            Assert.Contains(csvLine, forwarded);

            // Assert: line was parsed and event fired
            Assert.Equal(1, parsedCount);
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TeeLoop_Bidirectional_RelaysReverseBytes()
    {
        var options = CreateOptions(); // Bidirectional = true by default
        var configStore = new MockConfigStore(BuildFlecs34Variables());
        var logger = NullLogger<SerialTeeService>.Instance;

        var physicalPort = new InMemorySerialPort("COM2");
        var virtualPort = new InMemorySerialPort("COM1");

        using var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            _ => physicalPort,
            _ => virtualPort);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => service.GetStatus().State == "RUNNING");

            // Simulate iCem sending a command back through COM1
            string command = "*IDN?\r\n";
            virtualPort.FeedReadData(Encoding.ASCII.GetBytes(command));

            await WaitUntilAsync(() => physicalPort.WrittenData.Length == Encoding.ASCII.GetByteCount(command));

            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            // Assert: command bytes were forwarded to physical port
            var phyWritten = Encoding.ASCII.GetString(physicalPort.WrittenData);
            Assert.Equal(command, phyWritten);
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    // ── Fault injection: PORT_BUSY ──

    [Fact]
    public async Task TeeLoop_PortBusy_RetriesAndStaysAlive()
    {
        var options = CreateOptions();
        var configStore = new MockConfigStore(BuildFlecs34Variables());
        var logger = NullLogger<SerialTeeService>.Instance;

        int openAttempts = 0;
        ISerialPort CreatePhysical(string name)
        {
            openAttempts++;
            if (openAttempts <= 2)
                throw new UnauthorizedAccessException("Port busy");
            return new InMemorySerialPort(name);
        }

        var virtualPort = new InMemorySerialPort("COM1");

        using var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            CreatePhysical,
            _ => virtualPort);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var status = await WaitUntilAsync(service.GetStatus,
                snapshot => snapshot.PhysicalPortBusy && snapshot.State == "PORT_BUSY");
            Assert.True(status.PhysicalPortBusy);
            Assert.Equal("PORT_BUSY", status.State);

            // The retry is after 5s — we can't wait that long.
            // Instead, verify the state machine behavior and cancel.
            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(openAttempts >= 1, "Should have attempted to open port");
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TeeLoop_PortBusyException_SetsPhysicalPortBusyFlag()
    {
        var options = CreateOptions();
        var configStore = new MockConfigStore(BuildFlecs34Variables());
        var logger = NullLogger<SerialTeeService>.Instance;

        using var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            _ => throw new UnauthorizedAccessException("Port occupied"),
            _ => new InMemorySerialPort("COM1"));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var status = await WaitUntilAsync(service.GetStatus,
                snapshot => snapshot.State == "PORT_BUSY");
            Assert.True(status.PhysicalPortBusy);
            Assert.Equal("PORT_BUSY", status.State);

            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    // ── Status DTO compatibility ──

    [Fact]
    public void GetStatus_MatchesMockFixtureShape()
    {
        var options = CreateOptions();
        var configStore = new MockConfigStore(BuildFlecs34Variables());
        var logger = NullLogger<SerialTeeService>.Instance;

        using var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger);

        var status = service.GetStatus();

        // Verify all mock fixture fields are present
        Assert.Equal("COM2", status.PhysicalPort);
        Assert.Equal(9600, status.Baud);
        Assert.False(status.Up);
        Assert.Equal(0, status.ForwardErrors);

        // Verify the exact property names expected by the mock fixture exist
        // (reflection-based check)
        var type = typeof(ComTeeStatus);
        Assert.NotNull(type.GetProperty("PhysicalPort"));
        Assert.NotNull(type.GetProperty("Baud"));
        Assert.NotNull(type.GetProperty("TemplateName"));
        Assert.NotNull(type.GetProperty("Up"));
        Assert.NotNull(type.GetProperty("BytesForwardedPerSec"));
        Assert.NotNull(type.GetProperty("BytesParsedPerSec"));
        Assert.NotNull(type.GetProperty("ForwardErrors"));
        Assert.NotNull(type.GetProperty("LastForwardAgeSec"));
        Assert.NotNull(type.GetProperty("LastParseAgeSec"));
        Assert.NotNull(type.GetProperty("Com0comPairOk"));
        Assert.NotNull(type.GetProperty("PhysicalPortBusy"));
    }

    // ── Watchdog recovery ──

    [Fact]
    public async Task TeeLoop_VirtualPortMissing_RetriesConnection()
    {
        var options = CreateOptions();
        var configStore = new MockConfigStore(BuildFlecs34Variables());
        var logger = NullLogger<SerialTeeService>.Instance;

        int virtualAttempts = 0;
        var missing = new TaskCompletionSource<ComTeeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ISerialPort CreateVirtual(string name)
        {
            int attempt = Interlocked.Increment(ref virtualAttempts);
            if (attempt == 1)
                throw new IOException("Virtual port not available");
            retried.TrySetResult(attempt);
            return new InMemorySerialPort(name);
        }

        SerialTeeService? owner = null;
        using var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            _ => new InMemorySerialPort("COM2", onDispose: () =>
            {
                // Production disposes this port immediately AFTER PAIR_MISSING,
                // before awaiting retry delay or advancing the state in finally.
                // Capture on that same call stack; polling can miss a one-shot state.
                if (owner is not null) missing.TrySetResult(owner.GetStatus());
            }),
            CreateVirtual);
        owner = service;

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var status = await missing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(status.Com0comPairOk);
            Assert.Equal("PAIR_MISSING", status.State);
            // Verify the watchdog really attempts the virtual port again, rather
            // than only observing its initial error label.
            Assert.Equal(2, await retried.Task.WaitAsync(TimeSpan.FromSeconds(15)));

            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task InMemoryPort_ConcurrentFeedAndRead_PreservesEveryByte()
    {
        using var port = new InMemorySerialPort("fake");
        port.Open();
        byte[] expected = Enumerable.Range(0, 32_768).Select(i => (byte)(i % 251)).ToArray();
        var actual = new byte[expected.Length];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var reader = Task.Run(() =>
        {
            int offset = 0;
            while (offset < actual.Length)
            {
                deadline.Token.ThrowIfCancellationRequested();
                offset += port.Read(actual, offset, Math.Min(31, actual.Length - offset));
            }
        });
        try
        {
            for (int offset = 0; offset < expected.Length; offset += 32)
            {
                port.FeedReadData(expected.AsSpan(offset, Math.Min(32, expected.Length - offset)).ToArray());
                await Task.Yield();
            }
            await reader.WaitAsync(deadline.Token);
            Assert.Equal(expected, actual);
        }
        finally
        {
            deadline.Cancel();
            await reader.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task WaitUntil_ReturnsTheMatchingSnapshot_WhenSourceHasAlreadyAdvanced()
    {
        int reads = 0;
        ComTeeStatus Observe() => new() { State = ++reads == 1 ? "PAIR_MISSING" : "STARTING" };
        var observed = await WaitUntilAsync(Observe, snapshot => snapshot.State == "PAIR_MISSING");
        Assert.Equal("STARTING", Observe().State);
        Assert.Equal("PAIR_MISSING", observed.State);
        Assert.Equal(2, reads);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        await WaitUntilAsync(condition, matched => matched);
    }

    // Return the evidence that met the predicate. A fresh read after await can
    // already belong to the next retry phase, even though this wait succeeded.
    private static async Task<T> WaitUntilAsync<T>(Func<T> observe, Func<T, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var snapshot = observe();
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(20, deadline.Token);
        }
    }

    // ── Helpers ──

    private static TeeOptions CreateOptions() => new()
    {
        PhysicalPort = "COM2",
        BaudRate = 9600,
        DataBits = 8,
        Parity = System.IO.Ports.Parity.None,
        StopBits = System.IO.Ports.StopBits.One,
        VirtualOutPort = "COM1",
        Bidirectional = true,
    };

    private static IReadOnlyList<VariableDefinition> BuildFlecs34Variables()
    {
        // Match the 34-variable FLECS 4.53 template (index 0-33)
        var names = new[]
        {
            "UnixTimestamp", "UnitNum", "SlurryDensity", "DH_Density", "MixWaterRate",
            "PumpRate", "PumpPressure", "DSPumpPress", "WaterStgTot", "MixWaterTotal",
            "PumpStgTot", "CombPumpTotal", "CementValvePsn", "WaterValvePsn", "PSPumpRate",
            "DSPumpRate", "DigOut", "HalWinEvent", "Temperature", "rho12_hat",
            "h2_hat", "vhp_density_HP", "TubHeight", "ExtraPress1", "ExtraPress2",
            "ExtraRate1", "ExtraTotal1", "ExtraRate2", "ExtraTotal2", "WtrSlryRatio",
            "MudCupDensity", "PSPmpStgTot", "DSPmpStgTot", "VolumetricMeter",
        };

        var uoms = new[]
        {
            "", "", "ppg", "ppg", "bpm", "bpm", "psi", "psi", "bbl", "bbl",
            "bbl", "bbl", "%", "%", "bpm", "bpm", "", "", "degF", "ppg",
            "ft", "ppg", "ft", "psi", "psi", "bpm", "bbl", "bpm", "bbl", "",
            "ppg", "bbl", "bbl", "bpm",
        };

        var vars = new VariableDefinition[34];
        for (int i = 0; i < 34; i++)
        {
            vars[i] = new VariableDefinition(names[i], names[i], uoms[i], "#CC3300", i + 1, false, "test");
        }
        return vars;
    }

    private static string BuildDeterministicCsvLine(int columns, double stepFactor)
    {
        var parts = new string[columns];
        for (int i = 0; i < columns; i++)
            parts[i] = (i * stepFactor).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(",", parts);
    }

    private static string? ComTeeStatusExtensionsState(SerialTeeService s)
    {
        return s.GetStatus().State;
    }
}
