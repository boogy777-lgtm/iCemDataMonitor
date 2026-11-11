using System.IO;
using System.IO.MemoryMappedFiles;
using Grpc.Core;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Data.Telemetry;
using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Host.Core.Health;
using iCemDataMonitor.Host.Grpc;
using iCemDataMonitor.Host.Tray;
using iCemDataMonitor.Sdk.Bus;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using HZone = iCemDataMonitor.Host.Core.Health.ZoneHealth;

namespace iCemDataMonitor.Host.Tests;

// ===========================================================================
// D.2 TelemetryServiceImpl unit tests (NO Kestrel): GetStatus mapping,
// SubscribeMetrics smoke over a session-local fake bus (precedent
// MetricsSamplerTests � ������������ � ����� Global\ bus), SetLayer guard
// branches, SetAutostart + autostart_enabled truth (2026-08-27). Engine
// supervision was REMOVED with the emulator independence decree � no
// EngineSupervisor, no EmuleState, no ControlEngine.
// ===========================================================================

/// <summary>Minimal ServerCallContext for direct service-invocation tests.</summary>
internal sealed class TestCallContext : ServerCallContext
{
    private readonly CancellationToken _ct;
    public TestCallContext(CancellationToken ct = default) => _ct = ct;
    protected override string MethodCore => "/icemdatamonitor.v1.TelemetryService/Test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "ipv4:127.0.0.1:12345";
    protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
    protected override Metadata RequestHeadersCore { get; } = new();
    protected override Metadata ResponseTrailersCore { get; } = new();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; } = new();
    protected override AuthContext AuthContextCore { get; } = new("",
        new Dictionary<string, List<AuthProperty>>());
    protected override CancellationToken CancellationTokenCore => _ct;
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotSupportedException("not used in tests");
}

/// <summary>Collecting server-stream writer.</summary>
internal sealed class CollectingStreamWriter<T> : IServerStreamWriter<T> where T : class
{
    public List<T> Received { get; } = [];
    public WriteOptions? WriteOptions { get; set; }
    public Task WriteAsync(T message)
    {
        lock (Received) Received.Add(message);
        return Task.CompletedTask;
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TelemetryServiceImplTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tsvc_{Guid.NewGuid():N}");
    private readonly string _mapName;
    private readonly string _mutexName;
    private readonly BusReader _reader = null!;
    private BusAcquisitionService? _acquisition;
    private readonly MemoryMappedFile _section = null!;

    public TelemetryServiceImplTests()
    {
        Directory.CreateDirectory(_dir);
        var mmfPath = Path.Combine(_dir, "bus.dat");
        _mapName = $"TsvcMap_{Guid.NewGuid():N}";
        _mutexName = $"TsvcMutex_{Guid.NewGuid():N}";

        // INV-39 producer-first order: BusZoneWriter was DELETED from
        // iCemDataMonitor.Data (producer-owned bus � the host is read-only).
        // The test creates the backing file with a valid v5 identity header and
        // opens the named section READ-WRITE FIRST � a section first created by
        // a READER is born read-only (BusMapping.OpenReadOnly) and later writes
        // would fail. The section is the test-side write seam (�15.7(a)).
        CreateBusFile(mmfPath);
        _section = MemoryMappedFile.CreateFromFile(
            new FileStream(mmfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite),
            _mapName, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);

        _reader = new BusReader(mmfPath, _mapName, _mutexName);
        Assert.True(_reader.TryOpen(out var rerr), rerr);
    }

    public void Dispose()
    {
        _acquisition?.Dispose();
        _reader.Dispose();
        _section.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Creates the bus backing file with a valid v5 identity header:
    /// magic 'STNB' {0x53,0x54,0x4E,0x42}, version 1, layoutVer 5, regCount 10001
    /// (all little-endian at the BusLayout header offsets � the header endianness
    /// law) and length 64 + 2?10001 = 20066 with a zero body. The reader's shape
    /// validation (BusLayout.IsValidShape) must pass BEFORE the section opens.</summary>
    private static void CreateBusFile(string mmfPath)
    {
        using var fs = new FileStream(mmfPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        Span<byte> header = stackalloc byte[BusLayout.HeaderBytes];
        BusLayout.MagicBytes.CopyTo(header); // 0..3 'STNB'
        BitConverter.TryWriteBytes(header.Slice(BusLayout.HeaderVersionOffset, sizeof(ushort)), BusLayout.Version);
        BitConverter.TryWriteBytes(header.Slice(BusLayout.HeaderLayoutVersionOffset, sizeof(ushort)), BusLayout.LayoutVersion);
        BitConverter.TryWriteBytes(header.Slice(BusLayout.HeaderRegCountOffset, sizeof(ushort)), BusLayout.RegCount);
        fs.Write(header);
        fs.SetLength(BusLayout.TotalFileBytes); // extends with zeros (body)
    }

    /// <summary>Writes raw u16 words at absolute body addresses under the bus
    /// mutex (�15.7(a) data seam, precedent MetricsSamplerTests.WriteRawWords:
    /// header state regs 0..3 are AddIn-owned and are written ONLY here � the
    /// deleted BusZoneWriter likewise refused addr &lt; 4 by design).</summary>
    private void WriteRawWords(params (int Addr, ushort Value)[] writes)
    {
        using var mmf = MemoryMappedFile.OpenExisting(
            _mapName, MemoryMappedFileRights.ReadWrite);
        using var mutex = new Mutex(false, _mutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }
        try
        {
            long len = new FileInfo(Path.Combine(_dir, "bus.dat")).Length;
            using var acc = mmf.CreateViewAccessor(0, len, MemoryMappedFileAccess.ReadWrite);
            foreach (var (addr, value) in writes)
            {
                int off = iCemDataMonitor.Sdk.Bus.BusLayout.BodyOffset + addr * 2;
                acc.Write(off, (byte)(value >> 8));     // body words are BIG-endian
                acc.Write(off + 1, (byte)(value & 0xFF));
            }
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
        _acquisition?.CaptureOnce();
    }

    /// <summary>Test-side replacement for the DELETED BusZoneWriter.WriteFloat
    /// (INV-39 producer-owned bus): writes one float32 as its CDAB pair � low
    /// word at reg, high word at reg+1, bytes big-endian inside each word (the
    /// body word law) � through the READ-WRITE section under the bus mutex,
    /// reusing WriteRawWords for the mutex discipline.</summary>
    private void WriteFloat(int reg, float value)
    {
        var (low, high) = CdabCodec.Split(value);
        WriteRawWords((reg, low), (reg + 1, high));
    }

    /// <summary>Build the service + its dependencies. The AutostartManager uses
    /// a THROWAWAY HKCU subkey and value name � tests NEVER touch the real
    /// HKCU Run key (2026-08-27 autostart truth seam). The console-visibility
    /// service is built on the �15.7(a) native-boundary seam: the recorded
    /// probe/executor guarantee the unit tests NEVER AllocConsole / SW_HIDE
    /// real windows in the shared testhost (2026-08-28 one-service law).
    /// 2026-08-31 (theme decree): the UiPrefsStore is wired to a THROWAWAY
    /// temp-file path � tests NEVER read/write the real ProgramData ui-theme
    /// (the owner's live shell listens on the real poke event there); the
    /// theme-specific tests below build their own store through the same
    /// ctor seam with an explicit path + recorded poke.</summary>
    private (TelemetryServiceImpl Svc, StationHealthState Health, AutostartManager Autostart, ServiceProvider Provider, ConsoleVisibilityService Console, UiPrefsStore Prefs) Build(
        BusReader reader,
        List<ConsoleWindowAction>? executedActions = null)
    {
        var health = new StationHealthState();
        _acquisition = new BusAcquisitionService(reader, TimeSpan.FromMilliseconds(200));
        _acquisition.CaptureOnce();
        var sampler = new MetricsSampler(_acquisition);
        var autostart = new AutostartManager(
            runKeyPath: $@"Software\iCemDataMonitor.Host.Tests\run_{Guid.NewGuid():N}",
            valueName: $"autostart_{Guid.NewGuid():N}",
            exePath: @"C:\deploy\iCemDataMonitor.Host.exe");

        var console = new ConsoleVisibilityService(
            log: null,
            probe: () => ConsolePresence.None,
            execute: (action, _) => executedActions?.Add(action));

        var services = new ServiceCollection().BuildServiceProvider();
        var uiPrefs = new UiPrefsStore(
            filePath: Path.Combine(Path.GetTempPath(), $"tsvc_theme_{Guid.NewGuid():N}"));
        // E.2.7: the service no longer takes IHostApplicationLifetime � it has
        // NO way to stop the host process (shutdown = host tray only).
        var svc = new TelemetryServiceImpl(
            health, _acquisition, sampler, autostart, console, uiPrefs,
            services,
            NullLogger<TelemetryServiceImpl>.Instance);
        return (svc, health, autostart, services, console, uiPrefs);
    }

    [Fact]
    public async Task GetStatus_Maps_HealthZonesLayers()
    {
        var (svc, health, _, provider, _, _) = Build(_reader);
        try
        {
            health.Update(new StationHealth(
                BusOpen: true, AddInAlive: true, Heartbeat: 42, ComPort: 3, CaseLanState: 7, CaseState: 0,
                Com: new HZone(Active: true, AgeMs: 100),
                Lan: new HZone(Active: false, AgeMs: 99_999),
                Raw: new HZone(Active: true, AgeMs: 5),
                UpdatedAtUtc: new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc)));

            var status = await svc.GetStatus(new HostStatusRequest(), new TestCallContext());

            Assert.True(status.BusOpen);
            Assert.True(status.AddinAlive);
            Assert.Equal(42, status.Heartbeat);
            Assert.Equal(3, status.ComPort);
            // 2026-08-28 dishonest-surface fix: case_lan_state flows from
            // StationHealth.CaseLanState (header reg 2) � the former
            // StationHealth.LanStatus carried header reg 8 (a com_stream
            // float-pair low word = garbage as LAN state).
            Assert.Equal(7, status.CaseLanState);
            Assert.Equal(3, status.Zones.Count);
            Assert.Equal(BusZoneKind.BusZoneCom, status.Zones[0].Kind);
            Assert.True(status.Zones[0].Active);
            Assert.False(status.Zones[1].Active); // lan stale > truth age ? 5 s
            // 2026-08-27: autostart truth comes from the (throwaway, absent)
            // registry key > honest false, never a cached value or exception.
            Assert.False(status.AutostartEnabled);
            Assert.NotNull(status.UpdatedAtUtc);
        }
        finally { provider.Dispose(); }
    }

    [Fact]
    public async Task SubscribeMetrics_Smoke_FakeBus_ReceivesSamplesWithDeltas()
    {
        var (svc, _, _, provider, _, _) = Build(_reader);
        try
        {
            WriteFloat(200, 1.5f);

            var stream = new CollectingStreamWriter<MetricsSample>();            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var call = svc.SubscribeMetrics(new SubscribeRequest { IntervalMs = 200 }, stream, new TestCallContext(cts.Token));

            // Wait until at least two samples arrive (first full, second delta-only).
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (stream.Received.Count < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            cts.Cancel();
            try { await call; }
            catch (OperationCanceledException) { /* normal termination path */ }
            catch (RpcException) { /* same path through a real gRPC stack */ }

            Assert.True(stream.Received.Count >= 2, $"expected ?2 samples, got {stream.Received.Count}");
            Assert.True(stream.Received[0].Deltas.Count > 0);   // first sample is FULL
            Assert.Equal(200, stream.Received[0].IntervalClamped == false ? 200 : 200); // shape sanity
        }
        finally { provider.Dispose(); }
    }

    [Fact]
    public async Task SetLayer_Unspecified_InvalidArgument()
    {
        var (svc, _, _, provider, _, _) = Build(_reader);
        try
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                svc.SetLayer(new LayerControlRequest(), new TestCallContext()));
            Assert.Equal(StatusCode.InvalidArgument, ex.Status.StatusCode);
        }
        finally { provider.Dispose(); }
    }

    [Fact]
    public void LayerKindMapping_IsOneToOne()
    {
        Assert.Equal(TrayToggleRules.LayerKind.Modbus, TelemetryServiceImpl.MapLayer(LayerKind.Modbus));
        Assert.Equal(TrayToggleRules.LayerKind.LanSniffer, TelemetryServiceImpl.MapLayer(LayerKind.LanSniffer));
        Assert.Equal(TrayToggleRules.LayerKind.OpcDa, TelemetryServiceImpl.MapLayer(LayerKind.OpcDa));
        // RESTORED 2026-08-27: LAYER_KIND_WEB=2 maps to the persist-only arm.
        Assert.Equal(TrayToggleRules.LayerKind.Web, TelemetryServiceImpl.MapLayer(LayerKind.Web));
    }

    [Fact]
    public void ShutdownHostRpc_IsAbsent_FromContractDescriptor()
    {
        // E.2.7 LAW (owner decree 2026-08-24): ����� ����������� ������ ��
        // ������� �� �����. The ShutdownHost RPC was REMOVED from
        // telemetry.proto (ShutdownRequest/ShutdownAck > tombstone, numbers
        // 1..2/1..2 RESERVED, never reuse). This falsifiable guard fails if
        // the method ever reappears in the generated service descriptor.
        var methods = TelemetryService.Descriptor.Methods;
        // W21.1 additive delta + 2026-08-27 delta (−ControlEngine, +SetAutostart).
        // 2026-08-31 delta (+GetUiPrefs, +SetUiTheme — the station-wide theme
        // bool, owner decree «даже смена темы — тоже CLI»).
        Assert.Equal(10, methods.Count); // GetStatus, SubscribeMetrics, SetLayer, GetRegisters, SubscribeBootStages, SetConsoleVisible, SetAutostart, NavigateUi, GetUiPrefs, SetUiTheme
        Assert.Null(methods.FirstOrDefault(m => m.Name == "ShutdownHost"));

        // And the tombstoned message types are gone from the generated surface.
        var assembly = typeof(HostStatus).Assembly;
        Assert.Null(assembly.GetType("iCemDataMonitor.Contracts.ShutdownRequest"));
        Assert.Null(assembly.GetType("iCemDataMonitor.Contracts.ShutdownAck"));
    }

    // === 2026-08-27: SetAutostart + autostart_enabled truth ===
    // The service's AutostartManager is wired to a THROWAWAY HKCU subkey (Build
    // above) � the real Run key is NEVER touched by these tests.

    [Fact]
    public async Task SetAutostart_Enable_WritesRunValue_StatusReportsTruth()
    {
        var (svc, _, autostart, provider, _, _) = Build(_reader);
        try
        {
            var status = await svc.SetAutostart(new SetAutostartRequest { Enabled = true }, new TestCallContext());

            Assert.True(status.AutostartEnabled);              // fresh HostStatus mirrors the registry
            Assert.True(autostart.IsEnabled());                // direct truth probe agrees
            Assert.Equal("\"C:\\deploy\\iCemDataMonitor.Host.exe\" --autostart", autostart.ExpectedValueData);
        }
        finally
        {
            autostart.SetEnabled(false); // hygiene: delete the throwaway value
            provider.Dispose();
        }
    }

    [Fact]
    public async Task SetAutostart_Disable_RemovesRunValue_StatusReportsTruth()
    {
        var (svc, _, autostart, provider, _, _) = Build(_reader);
        try
        {
            autostart.SetEnabled(true); // pre-seed: the value exists
            Assert.True(autostart.IsEnabled());

            var status = await svc.SetAutostart(new SetAutostartRequest { Enabled = false }, new TestCallContext());

            Assert.False(status.AutostartEnabled);
            Assert.False(autostart.IsEnabled());
        }
        finally
        {
            autostart.SetEnabled(false); // hygiene: delete the throwaway value
            provider.Dispose();
        }
    }

    [Fact]
    public async Task SetAutostart_Toggle_IsRoundTrip()
    {
        var (svc, _, autostart, provider, _, _) = Build(_reader);
        try
        {
            var on = await svc.SetAutostart(new SetAutostartRequest { Enabled = true }, new TestCallContext());
            Assert.True(on.AutostartEnabled);

            var off = await svc.SetAutostart(new SetAutostartRequest { Enabled = false }, new TestCallContext());
            Assert.False(off.AutostartEnabled);

            var get = await svc.GetStatus(new HostStatusRequest(), new TestCallContext());
            Assert.False(get.AutostartEnabled); // GetStatus = same truth, sensor-at-send
        }
        finally
        {
            autostart.SetEnabled(false);
            provider.Dispose();
        }
    }

    // === 2026-08-28: SetConsoleVisible routes through the ONE service ===
    // Owner decree (�������� �� ���� ������ � �� ��������� � ����� ��������
    // host�): the RPC must hit ConsoleVisibilityService � the SAME instance the
    // tray toggle uses � with the policy table deciding the action. The �15.7(a)
    // seam makes the actions observable WITHOUT touching real Win32 windows.

    [Theory]
    [InlineData(true, ConsoleWindowAction.AttachAndShow)] // None + show > demand-attach
    [InlineData(false, ConsoleWindowAction.Nothing)]      // None + hide > nothing exists
    public async Task SetConsoleVisible_RoutesThrough_TheSingleService(
        bool requestedVisible, ConsoleWindowAction expectedAction)
    {
        var executed = new List<ConsoleWindowAction>();
        var (svc, _, _, provider, _, _) = Build(_reader, executedActions: executed);
        try
        {
            var status = await svc.SetConsoleVisible(
                new SetConsoleVisibleRequest { Visible = requestedVisible }, new TestCallContext());

            Assert.Contains(expectedAction, executed);          // the action DID route through the service
            Assert.Single(executed);                            // exactly one action � no second path
            Assert.Equal(Tray.ConsoleWindow.Visible, status.ConsoleVisible); // fresh truth in the response
        }
        finally { provider.Dispose(); }
    }

    // === W20 F.1: GetRegisters (bus reads AS RPC for thin CLI forwarders) ===

    /// <summary>Seeds a known full-live-bus state: header words 0..3, the Unix
    /// timestamp pair 4..5 (raw bits CDAB), and one distinctive float per zone
    /// (com 6..7, lan 200..201, raw 400..401) + the last live pair 598..599.</summary>
    private void SeedFullBus()
    {
        WriteRawWords(
            (0, 7),    // comPortActive
            (1, 9),    // caseState
            (2, 11),   // caseLanState
            (3, 1234), // heartbeat
            (4, 0x00A3), (5, 0x6553)); // Unix ts raw bits: low=0x00A3 high=0x6553 > 0x655300A3

        WriteFloat(6, 1.5f);
        WriteFloat(200, -2.25f);
        WriteFloat(400, 15.79f);
        WriteFloat(598, 3.5f);
    }

    [Fact]
    public async Task GetRegisters_LanAndRawUpdatesAtFixedComTime_AreIndependent()
    {
        var (svc, _, _, provider, _, _) = Build(_reader);
        try
        {
            SeedFullBus();
            var before = await svc.GetRegisters(new RegistersRequest(), new TestCallContext());
            WriteFloat(200, 9.5f);
            var lan = await svc.GetRegisters(new RegistersRequest(), new TestCallContext());
            WriteFloat(400, -7.25f);
            var raw = await svc.GetRegisters(new RegistersRequest(), new TestCallContext());
            foreach (var reply in new[] { before, lan, raw })
                Assert.Equal(unchecked((int)0x655300A3), reply.Values.Single(v => v.Addr == 4).Int32Value);
            Assert.Equal(-2.25f, before.Values.Single(v => v.Addr == 200).FloatValue);
            Assert.Equal(9.5f, lan.Values.Single(v => v.Addr == 200).FloatValue);
            Assert.Equal(15.79f, lan.Values.Single(v => v.Addr == 400).FloatValue);
            Assert.Equal(9.5f, raw.Values.Single(v => v.Addr == 200).FloatValue);
            Assert.Equal(-7.25f, raw.Values.Single(v => v.Addr == 400).FloatValue);
        }
        finally { provider.Dispose(); }
    }

    [Fact]
    public async Task GetRegisters_Unspecified_FullSweep_CoversAllLiveAddresses()
    {
        var (svc, _, _, provider, _, _) = Build(_reader);
        try
        {
            SeedFullBus();

            var reply = await svc.GetRegisters(new RegistersRequest(), new TestCallContext());

            Assert.NotNull(reply.TimestampUtc);
            Assert.Equal(1234, reply.Heartbeat);

            // Decode law = ComputeDeltas semantics: ONE entry per header word
            // (4) + ONE per CDAB pair at its base address (298 pairs in 4..599).
            int expectedEntries = iCemDataMonitor.Sdk.Bus.BusLayout.HeaderRegCount
                                  + (iCemDataMonitor.Data.Telemetry.RegisterDecoder.LiveEndExclusive
                                     - iCemDataMonitor.Sdk.Bus.BusLayout.HeaderRegCount) / 2; // 4 + 298 = 302
            Assert.Equal(expectedEntries, reply.Values.Count);

            // Full coverage of absolute addresses 0..599 (first/last + no gaps
            // between consecutive pair bases).
            Assert.Equal(0u, reply.Values[0].Addr);
            Assert.Equal(598u, reply.Values[^1].Addr);
            for (int i = 4; i < reply.Values.Count - 1; i++)
                Assert.Equal(reply.Values[i].Addr + 2, reply.Values[i + 1].Addr);

            // Header words 0..3 as int32.
            Assert.Equal(RegisterValue.ValueOneofCase.Int32Value, reply.Values[0].ValueCase);
            Assert.Equal(7, reply.Values[0].Int32Value);
            Assert.Equal(9, reply.Values[1].Int32Value);
            Assert.Equal(11, reply.Values[2].Int32Value);
            Assert.Equal(1234, reply.Values[3].Int32Value);

            // Pair 4..5 = Unix timestamp int32 RAW BITS (low | high << 16).
            Assert.Equal(4u, reply.Values[4].Addr);
            Assert.Equal(RegisterValue.ValueOneofCase.Int32Value, reply.Values[4].ValueCase);
            Assert.Equal(unchecked((int)0x655300A3), reply.Values[4].Int32Value);

            // Zone floats via CDAB.
            var byAddr = reply.Values.ToDictionary(v => v.Addr);
            Assert.Equal(1.5f, byAddr[6].FloatValue, 3);
            Assert.Equal(-2.25f, byAddr[200].FloatValue, 3);
            Assert.Equal(15.79f, byAddr[400].FloatValue, 3);
            Assert.Equal(3.5f, byAddr[598].FloatValue, 3);
        }
        finally { provider.Dispose(); }
    }

    [Fact]
    public async Task GetRegisters_ZoneFilter_ReturnsOnlyZoneRange_NoHeader()
    {
        var (svc, _, _, provider, _, _) = Build(_reader);
        try
        {
            SeedFullBus();

            // COM = com_stream 4..199 > 98 pairs, first addr 4, last addr 198,
            // NO header words (header belongs to NO data zone � CONTRACT_BusV2 v5).
            var com = await svc.GetRegisters(
                new RegistersRequest { Zone = BusZoneKind.BusZoneCom }, new TestCallContext());
            Assert.Equal(98, com.Values.Count);
            Assert.Equal(4u, com.Values[0].Addr);
            Assert.Equal(198u, com.Values[^1].Addr);
            Assert.DoesNotContain(com.Values, v => v.Addr < 4 || v.Addr > 198);

            // LAN = lan_stream_crio 200..399 > 100 pairs.
            var lan = await svc.GetRegisters(
                new RegistersRequest { Zone = BusZoneKind.BusZoneLan }, new TestCallContext());
            Assert.Equal(100, lan.Values.Count);
            Assert.Equal(200u, lan.Values[0].Addr);
            Assert.Equal(398u, lan.Values[^1].Addr);
            Assert.DoesNotContain(lan.Values, v => v.Addr < 200 || v.Addr > 398);

            // RAW = raw_com_line 400..599 > 100 pairs.
            var raw = await svc.GetRegisters(
                new RegistersRequest { Zone = BusZoneKind.BusZoneRaw }, new TestCallContext());
            Assert.Equal(100, raw.Values.Count);
            Assert.Equal(400u, raw.Values[0].Addr);
            Assert.Equal(598u, raw.Values[^1].Addr);
            Assert.DoesNotContain(raw.Values, v => v.Addr < 400 || v.Addr > 598);

            // Zone values still decode through the shared law.
            Assert.Equal(-2.25f, lan.Values[0].FloatValue, 3);   // addr 200
            Assert.Equal(15.79f, raw.Values[0].FloatValue, 3);   // addr 400
            Assert.Equal(RegisterValue.ValueOneofCase.Int32Value, com.Values[0].ValueCase); // ts pair @4
        }
        finally { provider.Dispose(); }
    }

    [Fact]
    public async Task GetRegisters_BusClosed_ThrowsUnavailable_WithDetail()
    {
        var (svc, _, _, provider, _, _) = Build(_reader);
        try
        {
            // A reader over a NON-EXISTENT bus file that was never opened:
            // Snapshot() ? null ? honest UNAVAILABLE (����������� ������ �����).
            var closedPath = Path.Combine(_dir, "no_such_bus.dat");
            using var closedReader = new BusReader(closedPath, $"TsvcClosedMap_{Guid.NewGuid():N}", $"TsvcClosedMutex_{Guid.NewGuid():N}");
            using var closedAcquisition = new BusAcquisitionService(closedReader, TimeSpan.FromMilliseconds(200));
            closedAcquisition.CaptureOnce();
            var sampler = new MetricsSampler(closedAcquisition);
            var autostart = new AutostartManager(
                runKeyPath: $@"Software\iCemDataMonitor.Host.Tests\run_{Guid.NewGuid():N}",
                valueName: $"autostart_{Guid.NewGuid():N}",
                exePath: @"C:\deploy\iCemDataMonitor.Host.exe");
            var services = new ServiceCollection().BuildServiceProvider();
            var closedSvc = new TelemetryServiceImpl(
                new StationHealthState(), closedAcquisition, sampler, autostart,
                new ConsoleVisibilityService(null, () => ConsolePresence.None, (_, _) => { }),
                // 2026-08-31: the ctor gained the UiPrefsStore — a throwaway
                // temp path here (this test never exercises the theme RPCs).
                new UiPrefsStore(Path.Combine(Path.GetTempPath(), $"tsvc_theme_{Guid.NewGuid():N}")),
                services, NullLogger<TelemetryServiceImpl>.Instance);
            try
            {
                var ex = await Assert.ThrowsAsync<RpcException>(() =>
                    closedSvc.GetRegisters(new RegistersRequest(), new TestCallContext()));
                Assert.Equal(StatusCode.Unavailable, ex.Status.StatusCode);
                Assert.False(string.IsNullOrWhiteSpace(ex.Status.Detail));
                Assert.Contains("bus is not open", ex.Status.Detail);
            }
            finally { services.Dispose(); }
        }
        finally { provider.Dispose(); }
    }

    // ═══ 2026-08-31 (owner decree «даже смена темы — тоже CLI: один bool для
    // одновременного использования в web и winui»): GetUiPrefs / SetUiTheme ═══
    // The store is wired through the §15.7(a) ctor seam (explicit temp-file
    // path + recorded poke): the REAL ProgramData ui-theme file and the
    // owner's live shell poke event are NEVER touched by these tests.

    /// <summary>Instance helper: a theme-focused service over THIS test's bus
    /// reader, with the prefs store isolated to an explicit path + poke.</summary>
    private (TelemetryServiceImpl Svc, UiPrefsStore Prefs, ServiceProvider Provider) BuildThemeService(
        string uiThemePath, Action? poke = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var prefs = new UiPrefsStore(uiThemePath, poke, null);
        var latest = _acquisition ??= new BusAcquisitionService(_reader, TimeSpan.FromMilliseconds(200));
        var svc = new TelemetryServiceImpl(
            new StationHealthState(), latest, new MetricsSampler(latest),
            new AutostartManager(
                runKeyPath: $@"Software\iCemDataMonitor.Host.Tests\run_{Guid.NewGuid():N}",
                valueName: $"autostart_{Guid.NewGuid():N}",
                exePath: @"C:\deploy\iCemDataMonitor.Host.exe"),
            new ConsoleVisibilityService(null, () => ConsolePresence.None, (_, _) => { }),
            prefs, services, NullLogger<TelemetryServiceImpl>.Instance);
        return (svc, prefs, services);
    }

    [Fact]
    public async Task GetUiPrefs_AbsentFile_DefaultDark()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tsvc_theme_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var (svc, prefs, provider) = BuildThemeService(Path.Combine(dir, "ui-theme"));
        try
        {
            var prefsMsg = await svc.GetUiPrefs(new UiPrefsRequest(), new TestCallContext());

            // The station default is DARK (matches the shell's LoadThemeIsLight
            // and the SPA's dark default) — an absent file is an honest dark,
            // never an exception and never a fake light.
            Assert.False(prefsMsg.ThemeLight);
            Assert.False(prefs.IsThemeLight());
        }
        finally
        {
            provider.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SetUiTheme_WritesFile_PokesEvent_AnswersFreshTruth()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tsvc_theme_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var themePath = Path.Combine(dir, "ui-theme");
        int pokes = 0;
        var (svc, prefs, provider) = BuildThemeService(themePath, poke: () => pokes++);
        try
        {
            var fresh = await svc.SetUiTheme(new SetUiThemeRequest { ThemeLight = true }, new TestCallContext());

            // 1. The response IS the fresh authoritative truth (re-read from
            //    the file, sensor-at-send — not a projection of the request).
            Assert.True(fresh.ThemeLight);
            Assert.True(prefs.IsThemeLight());
            Assert.Equal("light", File.ReadAllText(themePath).Trim());
            // 2. The poke fired exactly once AFTER the write — the same
            //    ordering the shell's SaveTheme uses (listeners re-read a
            //    durable bool).
            Assert.Equal(1, pokes);

            // GetUiPrefs agrees with the set (one station bool, one truth).
            var got = await svc.GetUiPrefs(new UiPrefsRequest(), new TestCallContext());
            Assert.True(got.ThemeLight);
        }
        finally
        {
            provider.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SetUiTheme_IsIdempotent_SameValueTwice_SameTruth()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tsvc_theme_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var themePath = Path.Combine(dir, "ui-theme");
        int pokes = 0;
        var (svc, prefs, provider) = BuildThemeService(themePath, poke: () => pokes++);
        try
        {
            var first = await svc.SetUiTheme(new SetUiThemeRequest { ThemeLight = true }, new TestCallContext());
            var second = await svc.SetUiTheme(new SetUiThemeRequest { ThemeLight = true }, new TestCallContext());

            // Idempotency law: repeated identical sets leave the STATE
            // unchanged and always answer the same fresh truth (the poke is a
            // notification, not a state change — 2 pokes for 2 commands).
            Assert.True(first.ThemeLight);
            Assert.True(second.ThemeLight);
            Assert.True(prefs.IsThemeLight());
            Assert.Equal("light", File.ReadAllText(themePath).Trim());
            Assert.Equal(2, pokes);
        }
        finally
        {
            provider.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SetUiTheme_RoundTrip_LightDarkLight_GetAgrees()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tsvc_theme_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var themePath = Path.Combine(dir, "ui-theme");
        var (svc, prefs, provider) = BuildThemeService(themePath);
        try
        {
            Assert.True((await svc.SetUiTheme(new SetUiThemeRequest { ThemeLight = true }, new TestCallContext())).ThemeLight);
            var dark = await svc.SetUiTheme(new SetUiThemeRequest { ThemeLight = false }, new TestCallContext());
            Assert.False(dark.ThemeLight);
            var back = await svc.SetUiTheme(new SetUiThemeRequest { ThemeLight = true }, new TestCallContext());

            Assert.True(back.ThemeLight);
            Assert.Equal("light", File.ReadAllText(themePath).Trim());
            Assert.True(prefs.IsThemeLight());

            var got = await svc.GetUiPrefs(new UiPrefsRequest(), new TestCallContext());
            Assert.True(got.ThemeLight);
        }
        finally
        {
            provider.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
