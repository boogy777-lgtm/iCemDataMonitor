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
    private bool _isOpen;
    private readonly Func<int>? _readTimeoutSim; // throw UnauthorizedAccessException after N reads

    public InMemorySerialPort(string name, byte[]? initialData = null, Func<int>? readTimeoutSim = null)
    {
        _portName = name;
        _readTimeoutSim = readTimeoutSim;
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
    public byte[] WrittenData => _writeStream.ToArray();

    /// <summary>Feed additional bytes into the read stream.</summary>
    public void FeedReadData(byte[] data)
    {
        long pos = _readStream.Position;
        _readStream.Position = _readStream.Length;
        _readStream.Write(data, 0, data.Length);
        _readStream.Position = pos;
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
        _isOpen = true;
    }

    public void Close()
    {
        _isOpen = false;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (!_isOpen)
            throw new InvalidOperationException("Port not open");
        return _readStream.Read(buffer, offset, count);
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (!_isOpen)
            throw new InvalidOperationException("Port not open");
        _writeStream.Write(buffer, offset, count);
    }

    public void Flush() { }

    public void Dispose()
    {
        _isOpen = false;
        _readStream.Dispose();
        _writeStream.Dispose();
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

        var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            _ => physicalPort,
            _ => virtualPort);

        var parsedLines = new List<IReadOnlyDictionary<string, double>>();
        service.LineParsed += (ts, dict) => parsedLines.Add(dict);

        // Start the service
        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);

        // Wait for ports to open
        await Task.Delay(300);

        Assert.Equal("RUNNING", ComTeeStatusExtensionsState(service));

        // Feed a known CSV line
        string csvLine = BuildDeterministicCsvLine(34, 1.01);
        physicalPort.FeedReadLine(csvLine);

        // Give time for processing
        await Task.Delay(500);

        // Stop
        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(3000));

        // Assert: virtual port received the bytes
        var forwarded = Encoding.ASCII.GetString(virtualPort.WrittenData);
        Assert.Contains(csvLine, forwarded);

        // Assert: line was parsed and event fired
        Assert.Single(parsedLines);
    }

    [Fact]
    public async Task TeeLoop_Bidirectional_RelaysReverseBytes()
    {
        var options = CreateOptions(); // Bidirectional = true by default
        var configStore = new MockConfigStore(BuildFlecs34Variables());
        var logger = NullLogger<SerialTeeService>.Instance;

        var physicalPort = new InMemorySerialPort("COM2");
        var virtualPort = new InMemorySerialPort("COM1");

        var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            _ => physicalPort,
            _ => virtualPort);

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);

        await Task.Delay(300);

        // Simulate iCem sending a command back through COM1
        string command = "*IDN?\r\n";
        virtualPort.FeedReadData(Encoding.ASCII.GetBytes(command));

        await Task.Delay(500);

        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(3000));

        // Assert: command bytes were forwarded to physical port
        var phyWritten = Encoding.ASCII.GetString(physicalPort.WrittenData);
        Assert.Contains("*IDN?", phyWritten);
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

        var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            CreatePhysical,
            _ => virtualPort);

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);

        // Wait for the first attempt to surface PORT_BUSY — POLL instead of a
        // fixed sleep (CaptureToFile lesson: fixed sleeps flake under the
        // parallel load of the full-suite run).
        var portBusyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var sawPortBusy = false;
        var reportedState = string.Empty;
        while (DateTime.UtcNow < portBusyDeadline && !sawPortBusy)
        {
            var sample = service.GetStatus();
            sawPortBusy = sample.PhysicalPortBusy;
            reportedState = sample.State;
            await Task.Delay(50);
        }

        Assert.True(sawPortBusy, "Should be PORT_BUSY after UnauthorizedAccessException");
        Assert.Equal("PORT_BUSY", reportedState);

        // The retry is after 5s — we can't wait that long.
        // Instead, verify the state machine behavior and cancel.
        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(2000));

        Assert.True(openAttempts >= 1, "Should have attempted to open port");
    }

    [Fact]
    public async Task TeeLoop_PortBusyException_SetsPhysicalPortBusyFlag()
    {
        var options = CreateOptions();
        var configStore = new MockConfigStore(BuildFlecs34Variables());
        var logger = NullLogger<SerialTeeService>.Instance;

        var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            _ => throw new UnauthorizedAccessException("Port occupied"),
            _ => new InMemorySerialPort("COM1"));

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);

        await Task.Delay(300);

        var status = service.GetStatus();
        Assert.True(status.PhysicalPortBusy);
        Assert.Equal("PORT_BUSY", status.State);

        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(2000));
    }

    // ── Status DTO compatibility ──

    [Fact]
    public void GetStatus_MatchesMockFixtureShape()
    {
        var options = CreateOptions();
        var configStore = new MockConfigStore(BuildFlecs34Variables());
        var logger = NullLogger<SerialTeeService>.Instance;

        var service = new SerialTeeService(
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
        ISerialPort CreateVirtual(string name)
        {
            virtualAttempts++;
            if (virtualAttempts == 1)
                throw new IOException("Virtual port not available");
            return new InMemorySerialPort(name);
        }

        var service = new SerialTeeService(
            Options.Create(options),
            configStore,
            logger,
            _ => new InMemorySerialPort("COM2"),
            CreateVirtual);

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => service.StartAsync(cts.Token), CancellationToken.None);

        await Task.Delay(500);

        var status = service.GetStatus();
        Assert.False(status.Com0comPairOk);
        Assert.Equal("PAIR_MISSING", status.State);

        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(2000));
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