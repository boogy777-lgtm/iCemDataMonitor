using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using iCemDataMonitor.Core;
using iCemDataMonitor.Core.Models;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Core.Tests;

/// <summary>
/// Contract tests — verify orchestrator behavior against interfaces.
/// All tests use Moq mocks; no real database, network, or process dependencies.
/// Each test manages full start→verify→stop lifecycle.
/// </summary>
public sealed class ContractTests
{
    // ──────────────────────────────────────────────
    // ContractTest_MongoDataSourcePushesToTimeseries
    // IDataSource callback → Channel → ITimeseriesStore.InsertSampleAsync(SourceSample)
    // E5 source-provenance doctrine: legacy channel path tags SourceKind.Mongo.
    // Verify InsertSampleAsync receives exact payload with Mongo provenance.
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ContractTest_MongoDataSourcePushesToTimeseries()
    {
        // Arrange
        var dataSourceMock = new Mock<IDataSource>();
        var configStoreMock = new Mock<IConfigStore>();
        var timeseriesStoreMock = new Mock<ITimeseriesStore>();
        var modbusMock = new Mock<IModbusPublisher>();
        var loggerMock = new Mock<ILogger<PipelineWorker>>();

        var options = new CoreOptions
        {
            MongoNameCollectionTimeoutMs = 500,  // short startup window for test
            MongoNameCollectionBatches = 1,
        };
        var channel = Channel.CreateUnbounded<TimeSeriesSample>();

        Action<DateTime, IReadOnlyDictionary<string, double>>? capturedCallback = null;
        dataSourceMock
            .Setup(ds => ds.Subscribe(It.IsAny<Action<DateTime, IReadOnlyDictionary<string, double>>>()))
            .Callback<Action<DateTime, IReadOnlyDictionary<string, double>>>(cb => capturedCallback = cb);
        dataSourceMock.Setup(ds => ds.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        dataSourceMock.Setup(ds => ds.StopAsync()).Returns(Task.CompletedTask);

        configStoreMock.Setup(cs => cs.ActivateConfigAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        configStoreMock.Setup(cs => cs.GetActiveConfig()).Returns(new DeviceConfig("test", new List<VariableDefinition>()));
        configStoreMock.Setup(cs => cs.GetActiveVariables()).Returns(new List<VariableDefinition>());
        configStoreMock.Setup(cs => cs.GetModbusRegisterMap()).Returns(new List<ModbusRegisterEntry>());

        modbusMock.Setup(m => m.ConfigureAsync(It.IsAny<IReadOnlyList<ModbusRegisterEntry>>())).Returns(Task.CompletedTask);
        modbusMock.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        modbusMock.Setup(m => m.StopAsync()).Returns(Task.CompletedTask);

        timeseriesStoreMock.Setup(ts => ts.InsertSampleAsync(It.IsAny<SourceSample>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new PipelineWorker(
            dataSourceMock.Object, configStoreMock.Object, timeseriesStoreMock.Object,
            modbusMock.Object, channel, options, loggerMock.Object);

        // Act — start worker, fire callback via subscription
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await Task.Delay(300); // let startup sequence complete

        Assert.NotNull(capturedCallback);

        var testTimestamp = DateTime.UtcNow;
        var testVariables = new Dictionary<string, double>
        {
            ["SlurryDensity"] = 14.5,
            ["PumpPressure"] = 3200.0,
            ["PumpRate"] = 4.2,
        };

        capturedCallback!(testTimestamp, testVariables.AsReadOnly());
        await Task.Delay(300); // let channel drain to InsertAsync

        // Cleanup
        channel.Writer.Complete();
        await worker.StopAsync(CancellationToken.None);

        // Assert — InsertSampleAsync received exact payload with Mongo provenance
        timeseriesStoreMock.Verify(
            ts => ts.InsertSampleAsync(
                It.Is<SourceSample>(s =>
                    s.Timestamp == testTimestamp &&
                    s.Source == SourceKind.Mongo &&
                    s.Variables.Count == 3 &&
                    Math.Abs(s.Variables["SlurryDensity"] - 14.5) < 0.001 &&
                    Math.Abs(s.Variables["PumpPressure"] - 3200.0) < 0.001 &&
                    Math.Abs(s.Variables["PumpRate"] - 4.2) < 0.001),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "ITimeseriesStore.InsertSampleAsync must receive SourceSample(Mongo) from IDataSource callback");
    }

    // ──────────────────────────────────────────────
    // ContractTest_ConfigActivateTriggersModbusConfigure
    // ActivateConfigAsync → IModbusPublisher.ConfigureAsync with register map.
    // Verifies both call occurrence and ordering.
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ContractTest_ConfigActivateTriggersModbusConfigure()
    {
        // Arrange
        var dataSourceMock = new Mock<IDataSource>();
        var configStoreMock = new Mock<IConfigStore>();
        var timeseriesStoreMock = new Mock<ITimeseriesStore>();
        var modbusMock = new Mock<IModbusPublisher>();
        var loggerMock = new Mock<ILogger<PipelineWorker>>();

        var options = new CoreOptions
        {
            DefaultFamily = "flecs",
            DefaultConfig = "flecs_ver4p53_ppg_bpm_psi",
            MongoNameCollectionTimeoutMs = 500,
        };

        var channel = Channel.CreateUnbounded<TimeSeriesSample>();

        var expectedRegisterMap = new List<ModbusRegisterEntry>
        {
            new("SlurryDensity", 2), new("PumpRate", 4), new("PumpPressure", 6),
        };

        var expectedVariables = new List<VariableDefinition>
        {
            new("SlurryDensity", "Recirc Density", "ppg", "#CC3300", 1, false, "ssf"),
            new("PumpRate", "Comb Pump Rate", "bpm", "#339933", 2, false, "ssf"),
            new("PumpPressure", "PS Pump Press", "psi", "#006699", 3, false, "ssf"),
        };

        var callOrder = new ConcurrentQueue<string>();

        dataSourceMock
            .Setup(ds => ds.Subscribe(It.IsAny<Action<DateTime, IReadOnlyDictionary<string, double>>>()))
            .Callback<Action<DateTime, IReadOnlyDictionary<string, double>>>(_ => callOrder.Enqueue("Subscribe"));
        dataSourceMock.Setup(ds => ds.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<CancellationToken>(_ => callOrder.Enqueue("DataSource.StartAsync"));
        dataSourceMock.Setup(ds => ds.StopAsync()).Returns(Task.CompletedTask);

        configStoreMock.Setup(cs => cs.ActivateConfigAsync("flecs", "flecs_ver4p53_ppg_bpm_psi"))
            .Returns(Task.CompletedTask)
            .Callback(() => callOrder.Enqueue("ActivateConfigAsync"));
        configStoreMock.Setup(cs => cs.GetActiveConfig())
            .Returns(new DeviceConfig("flecs_ver4p53_ppg_bpm_psi", expectedVariables));
        configStoreMock.Setup(cs => cs.GetActiveVariables()).Returns(expectedVariables);
        configStoreMock.Setup(cs => cs.GetModbusRegisterMap()).Returns(expectedRegisterMap);

        modbusMock.Setup(m => m.ConfigureAsync(It.IsAny<IReadOnlyList<ModbusRegisterEntry>>()))
            .Returns(Task.CompletedTask)
            .Callback<IReadOnlyList<ModbusRegisterEntry>>(_ => callOrder.Enqueue("Modbus.ConfigureAsync"));
        modbusMock.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<CancellationToken>(_ => callOrder.Enqueue("Modbus.StartAsync"));
        modbusMock.Setup(m => m.StopAsync()).Returns(Task.CompletedTask);

        timeseriesStoreMock.Setup(ts => ts.InsertSampleAsync(It.IsAny<SourceSample>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new PipelineWorker(
            dataSourceMock.Object, configStoreMock.Object, timeseriesStoreMock.Object,
            modbusMock.Object, channel, options, loggerMock.Object);

        // Act — startup triggers the full sequence
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await Task.Delay(300);

        channel.Writer.Complete();
        await worker.StopAsync(CancellationToken.None);

        // Assert — ConfigureAsync called with exact register map
        modbusMock.Verify(
            m => m.ConfigureAsync(It.Is<IReadOnlyList<ModbusRegisterEntry>>(map =>
                map.Count == 3 &&
                map[0].VariableName == "SlurryDensity" && map[0].RegisterAddress == 2 &&
                map[1].VariableName == "PumpRate" && map[1].RegisterAddress == 4 &&
                map[2].VariableName == "PumpPressure" && map[2].RegisterAddress == 6)),
            Times.Once,
            "IModbusPublisher.ConfigureAsync must be called with register map from IConfigStore");

        // Assert — call ordering: ActivateConfigAsync precedes Modbus.ConfigureAsync
        var orderList = callOrder.ToList();
        var activateIndex = orderList.IndexOf("ActivateConfigAsync");
        var modbusConfigIndex = orderList.IndexOf("Modbus.ConfigureAsync");

        Assert.True(activateIndex >= 0, "ActivateConfigAsync must be called");
        Assert.True(modbusConfigIndex >= 0, "Modbus.ConfigureAsync must be called");
        Assert.True(activateIndex < modbusConfigIndex,
            $"ActivateConfigAsync (idx {activateIndex}) must precede Modbus.ConfigureAsync (idx {modbusConfigIndex})");
    }

    // ──────────────────────────────────────────────
    // ContractTest_HealthCheckDetectsMongoDown
    // IDataSource.DiscoverAsync throws → HealthEvent.DbOk = false
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ContractTest_HealthCheckDetectsMongoDown()
    {
        // HealthCheckService is Windows-only by design ([SupportedOSPlatform]);
        // on a non-Windows test host there is nothing to exercise (CA1416 guard).
        if (!OperatingSystem.IsWindows())
            return;

        // Arrange
        var dataSourceMock = new Mock<IDataSource>();
        var loggerMock = new Mock<ILogger<HealthCheckService>>();
        var options = new CoreOptions { HealthCheckIntervalSeconds = 1 };
        var healthChannel = Channel.CreateUnbounded<HealthEvent>();

        dataSourceMock
            .Setup(ds => ds.DiscoverAsync())
            .ThrowsAsync(new TimeoutException("MongoDB unreachable"));

        var service = new HealthCheckService(
            dataSourceMock.Object, healthChannel, options, loggerMock.Object);

        // Act — start, read first health event
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await service.StartAsync(cts.Token);

        HealthEvent? capturedEvent = null;
        try { capturedEvent = await healthChannel.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { }

        await service.StopAsync(CancellationToken.None);

        // Assert — DbOk is the deterministic part of this test.
        // IcemOk is environment-dependent (legacy iCem.exe may or may not run on the
        // test host) ⇒ not asserted here. Exact-name process check (iCem.exe, ¬
        // FlecsTray* emulators) is covered by code review of HealthCheckService.
        Assert.NotNull(capturedEvent);
        Assert.False(capturedEvent.Value.DbOk,
            "HealthEvent.DbOk must be false when IDataSource.DiscoverAsync throws (MongoDB down)");
    }

    // ──────────────────────────────────────────────
    // ContractTest_ShutdownGraceful
    // worker.StopAsync → IDataSource.StopAsync + both publishers.StopAsync called.
    // Channel completed, no orphan processing.
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ContractTest_ShutdownGraceful()
    {
        // Arrange
        var dataSourceMock = new Mock<IDataSource>();
        var configStoreMock = new Mock<IConfigStore>();
        var timeseriesStoreMock = new Mock<ITimeseriesStore>();
        var modbusMock = new Mock<IModbusPublisher>();
        var loggerMock = new Mock<ILogger<PipelineWorker>>();

        var options = new CoreOptions { MongoNameCollectionTimeoutMs = 500 };
        var channel = Channel.CreateUnbounded<TimeSeriesSample>();

        var dsStopCalled = false;
        var modbusStopCalled = false;

        dataSourceMock
            .Setup(ds => ds.Subscribe(It.IsAny<Action<DateTime, IReadOnlyDictionary<string, double>>>()))
            .Callback<Action<DateTime, IReadOnlyDictionary<string, double>>>(_ => { });
        dataSourceMock.Setup(ds => ds.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        dataSourceMock.Setup(ds => ds.StopAsync()).Returns(Task.CompletedTask).Callback(() => dsStopCalled = true);

        configStoreMock.Setup(cs => cs.ActivateConfigAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        configStoreMock.Setup(cs => cs.GetActiveConfig()).Returns(new DeviceConfig("test", new List<VariableDefinition>()));
        configStoreMock.Setup(cs => cs.GetActiveVariables()).Returns(new List<VariableDefinition>());
        configStoreMock.Setup(cs => cs.GetModbusRegisterMap()).Returns(new List<ModbusRegisterEntry>());

        modbusMock.Setup(m => m.ConfigureAsync(It.IsAny<IReadOnlyList<ModbusRegisterEntry>>())).Returns(Task.CompletedTask);
        modbusMock.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        modbusMock.Setup(m => m.StopAsync()).Returns(Task.CompletedTask).Callback(() => modbusStopCalled = true);

        timeseriesStoreMock.Setup(ts => ts.InsertSampleAsync(It.IsAny<SourceSample>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new PipelineWorker(
            dataSourceMock.Object, configStoreMock.Object, timeseriesStoreMock.Object,
            modbusMock.Object, channel, options, loggerMock.Object);

        // Act — start → let startup complete → graceful stop
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await Task.Delay(300); // let ExecuteAsync reach ReadAllAsync loop

        await worker.StopAsync(CancellationToken.None);

        // Assert — all StopAsync methods called
        Assert.True(dsStopCalled,
            "IDataSource.StopAsync must be called during graceful shutdown");
        Assert.True(modbusStopCalled,
            "IModbusPublisher.StopAsync must be called during graceful shutdown");

        // Channel reader must be completed — no orphan processing
        Assert.True(channel.Reader.Completion.IsCompleted,
            "Channel reader must be completed after shutdown — no orphan processing");
    }

    // ──────────────────────────────────────────────
    // ContractTest_ContinuousHarvestDetectsNewNames (F15)
    // Verifies that after the startup window, unseen variable names trigger
    // a throttled RefreshFromLiveAsync call, enabling device-switch without restart.
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ContractTest_ContinuousHarvestDetectsNewNames()
    {
        // Arrange
        var dataSourceMock = new Mock<IDataSource>();
        var configStoreMock = new Mock<IConfigStore>();
        var timeseriesStoreMock = new Mock<ITimeseriesStore>();
        var modbusMock = new Mock<IModbusPublisher>();
        var loggerMock = new Mock<ILogger<PipelineWorker>>();

        var options = new CoreOptions
        {
            MongoReharvestDebounceMs = 100,   // very short debounce for test speed
            MongoNameCollectionBatches = 1,   // startup after single batch
            MongoNameCollectionTimeoutMs = 3000,
            DefaultFamily = "flecs",
            DefaultConfig = "flecs_ver4p53_ppg_bpm_psi",
        };

        var channel = Channel.CreateUnbounded<TimeSeriesSample>();

        Action<DateTime, IReadOnlyDictionary<string, double>>? capturedCallback = null;
        dataSourceMock
            .Setup(ds => ds.Subscribe(It.IsAny<Action<DateTime, IReadOnlyDictionary<string, double>>>()))
            .Callback<Action<DateTime, IReadOnlyDictionary<string, double>>>(cb => capturedCallback = cb);
        dataSourceMock.Setup(ds => ds.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        dataSourceMock.Setup(ds => ds.StopAsync()).Returns(Task.CompletedTask);

        var reharvestCalls = new ConcurrentBag<IEnumerable<string>>();
        var startupReharvestTcs = new TaskCompletionSource();
        var continuousReharvestTcs = new TaskCompletionSource();

        configStoreMock
            .Setup(cs => cs.RefreshFromLiveAsync(It.IsAny<IEnumerable<string>>()))
            .Returns(Task.CompletedTask)
            .Callback<IEnumerable<string>>(names =>
            {
                var nameList = names.ToList();
                reharvestCalls.Add(nameList);
                if (reharvestCalls.Count == 1)
                    startupReharvestTcs.TrySetResult();
                else
                    continuousReharvestTcs.TrySetResult();
            });

        configStoreMock.Setup(cs => cs.ActivateConfigAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        configStoreMock.Setup(cs => cs.GetActiveConfig())
            .Returns(new DeviceConfig("test", new List<VariableDefinition>()));
        configStoreMock.Setup(cs => cs.GetActiveVariables())
            .Returns(new List<VariableDefinition>());
        configStoreMock.Setup(cs => cs.GetModbusRegisterMap())
            .Returns(new List<ModbusRegisterEntry>());

        modbusMock.Setup(m => m.ConfigureAsync(It.IsAny<IReadOnlyList<ModbusRegisterEntry>>()))
            .Returns(Task.CompletedTask);
        modbusMock.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        modbusMock.Setup(m => m.StopAsync()).Returns(Task.CompletedTask);

        timeseriesStoreMock
            .Setup(ts => ts.InsertSampleAsync(It.IsAny<SourceSample>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new PipelineWorker(
            dataSourceMock.Object, configStoreMock.Object, timeseriesStoreMock.Object,
            modbusMock.Object, channel, options, loggerMock.Object);

        // Act — start worker
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(cts.Token);
        await Task.Delay(100);
        Assert.NotNull(capturedCallback);

        // Fire batch 1 ⇒ completes startup window immediately (threshold = 1)
        capturedCallback!(DateTime.UtcNow,
            new Dictionary<string, double> { ["StartupVarA"] = 1.0 }.AsReadOnly());

        // Wait for startup RefreshFromLiveAsync (call #1)
        await startupReharvestTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fire batch 2 with NEW name ⇒ triggers continuous harvest
        capturedCallback!(DateTime.UtcNow,
            new Dictionary<string, double> { ["NewVarB"] = 2.0 }.AsReadOnly());

        // Wait for continuous harvest (debounce 100ms + Task.Run scheduling)
        await continuousReharvestTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Small extra delay for the Task.Run to complete the mock callback
        await Task.Delay(200);

        // Cleanup
        channel.Writer.Complete();
        await worker.StopAsync(CancellationToken.None);

        // Assert — at least 2 calls: startup + continuous harvest
        // NOTE: ConcurrentBag does NOT preserve insertion order. Mock callbacks may
        // interleave due to thread-pool scheduling. Verify the combined result, not order.
        var allNames = reharvestCalls.SelectMany(c => c)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(reharvestCalls.Count >= 2,
            $"Expected ≥2 RefreshFromLiveAsync calls, got {reharvestCalls.Count}. Names: [{string.Join(",", allNames)}]");
        Assert.Contains("NewVarB", allNames);
        Assert.Contains("StartupVarA", allNames);

        // At least one call must contain more names than the minimum (proving growth)
        var minNameCount = reharvestCalls.Min(c => c.Count());
        var maxNameCount = reharvestCalls.Max(c => c.Count());
        Assert.True(maxNameCount > minNameCount,
            $"Expected name count growth across calls. Min={minNameCount}, Max={maxNameCount}");
    }
}
