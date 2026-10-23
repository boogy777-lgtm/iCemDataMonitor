using Microsoft.Extensions.DependencyInjection;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Host.Core;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Host.Tests;

public sealed class LayerCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "legacy-c2-" + Guid.NewGuid());
    private string IniPath => Path.Combine(_dir, "config.ini");
    public LayerCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Actuator : LayerRuntimeController.ILayerActuator
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Calls = new();
        public bool Bound;
        public int Active, MaxActive;
        public bool BlockStart = true;
        public bool FailStart, FailSniffer;
        public async Task StartModbusAsync()
        {
            Calls.Add("start");
            Active++;
            MaxActive = Math.Max(MaxActive, Active);
            try
            {
                Entered.TrySetResult();
                if (BlockStart) await Release.Task;
                if (FailStart) throw new InvalidOperationException("start failed");
                Bound = true;
            }
            finally { Active--; }
        }
        public Task StopModbusAsync() { Calls.Add("stop"); Bound = false; return Task.CompletedTask; }
        public Task StartSnifferAsync() { Calls.Add("sniffer-start"); return FailSniffer ? Task.FromException(new Exception("sniffer failed")) : Task.CompletedTask; }
        public Task StopSnifferAsync() { Calls.Add("sniffer-stop"); return Task.CompletedTask; }
    }

    private LayerRuntimeController Create(Actuator actuator, bool persist = false) => new(
        new IniOptions(ModbusOn: 0, LansnifferOn: 0, UiSetAsDefault: persist ? 1 : 0),
        new TrayToggleRules.Layers(false, false, false, true), true, IniPath, actuator)
        { ModbusBoundProbe = () => actuator.Bound };

    [Fact]
    public async Task CommandsSerialize_TwoTogglesThenSave_StatusDoesNotWait()
    {
        var actuator = new Actuator();
        var c = Create(actuator);
        var first = c.ToggleAsync(TrayToggleRules.LayerKind.Modbus);
        await actuator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = c.ToggleAsync(TrayToggleRules.LayerKind.Modbus);
        var save = c.SetSetAsDefaultAsync(true);
        var refresh = c.RefreshIniAsync();
        Assert.True(c.CaptureStatus().Busy);
        Assert.False(second.IsCompleted);
        Assert.False(save.IsCompleted);
        Assert.False(refresh.IsCompleted);
        Assert.Equal(new[] { "start" }, actuator.Calls);
        actuator.Release.SetResult();
        await Task.WhenAll(first, second, save, refresh).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "start", "stop" }, actuator.Calls);
        Assert.Equal(1, actuator.MaxActive);
        Assert.False(c.ModbusRunning);
        Assert.False(c.Busy);
        Assert.Equal(0, c.Ini.ModbusOn);
        Assert.True(c.SetAsDefault);
    }

    [Fact]
    public async Task CancelledQueuedCommand_HasNoEffects_AndNextCommandWorks()
    {
        var actuator = new Actuator();
        var c = Create(actuator);
        var first = c.ToggleAsync(TrayToggleRules.LayerKind.Modbus);
        await actuator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        var queued = c.ToggleAsync(TrayToggleRules.LayerKind.Modbus, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(c.LastError);
        Assert.Equal(new[] { "start" }, actuator.Calls);
        actuator.Release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await c.ToggleAsync(TrayToggleRules.LayerKind.Modbus);
        Assert.Equal(new[] { "start", "stop" }, actuator.Calls);
    }

    [Fact]
    public async Task CancellationAfterAdmission_DoesNotAbortOperation()
    {
        var actuator = new Actuator();
        var c = Create(actuator);
        using var cancel = new CancellationTokenSource();
        var command = c.ToggleAsync(TrayToggleRules.LayerKind.Modbus, cancel.Token);
        await actuator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        actuator.Release.SetResult();
        await command.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(c.ModbusRunning);
        Assert.Null(c.LastError);
    }

    [Fact]
    public async Task SaveFailure_KeepsAppliedRequestAndObserved_ButLastPersistedIni_ThenClearsError()
    {
        var actuator = new Actuator { BlockStart = false };
        var c = Create(actuator, persist: true);
        Directory.CreateDirectory(IniPath); // deterministic file/directory collision
        await c.ToggleAsync(TrayToggleRules.LayerKind.Modbus);
        var status = c.CaptureStatus();
        Assert.True(status.ModbusBound);
        Assert.True(status.RequestedModbus);
        Assert.Equal(0, status.Ini.ModbusOn);
        Assert.NotNull(status.LastError);
        await c.SetSetAsDefaultAsync(false);
        Assert.True(c.SetAsDefault); // failed save retains previous persisted checkbox
        Assert.NotNull(c.LastError);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var previousError = c.LastError;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.SetSetAsDefaultAsync(false, cancelled.Token));
        Assert.Equal(previousError, c.LastError);
        Directory.Delete(IniPath);
        await c.SetSetAsDefaultAsync(true);
        Assert.Null(c.LastError);
        Assert.Equal(1, c.Ini.ModbusOn);
    }

    [Fact]
    public async Task ActuatorFailures_RollbackModbusOnly_AndRetainRequestedState()
    {
        var actuator = new Actuator { BlockStart = false, FailStart = true, FailSniffer = true };
        var c = Create(actuator);
        await c.ToggleAsync(TrayToggleRules.LayerKind.Modbus);
        Assert.Equal(new[] { "start", "stop" }, actuator.Calls);
        Assert.False(c.ModbusRunning);
        Assert.Equal("start failed", c.LastError);
        await c.ToggleAsync(TrayToggleRules.LayerKind.LanSniffer);
        Assert.Equal(new[] { "start", "stop", "sniffer-start" }, actuator.Calls);
        Assert.False(c.LanSnifferRunning);
    }

    [Fact]
    public async Task InvalidRefresh_PreservesStrictFailureContract_AndReleasesGate()
    {
        var c = Create(new Actuator { BlockStart = false });
        File.WriteAllText(IniPath, "invalid config");
        Assert.ThrowsAny<Exception>(() => c.RefreshIni());
        Assert.NotNull(c.LastError);
        Assert.False(c.Busy);
        await c.ToggleAsync(TrayToggleRules.LayerKind.Web).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(c.LastError);
    }

    [Fact]
    public async Task Rpc_PreservesToggleLaw_IgnoresDesired_WebPlanAndOpcDaNoActuator()
    {
        var actuator = new Actuator { BlockStart = false };
        var controller = Create(actuator);
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(controller).BuildServiceProvider();
        using var reader = new iCemDataMonitor.Data.Bus.BusReader(); // unopened: no named MMF dependency
        using var latest = new iCemDataMonitor.Data.Bus.BusAcquisitionService(reader, TimeSpan.FromMilliseconds(200));
        var service = new iCemDataMonitor.Host.Grpc.TelemetryServiceImpl(
            new iCemDataMonitor.Host.Core.Health.StationHealthState(), latest,
            new iCemDataMonitor.Data.Telemetry.MetricsSampler(latest),
            new iCemDataMonitor.Host.Tray.AutostartManager(),
            new iCemDataMonitor.Host.Tray.ConsoleVisibilityService(),
            new iCemDataMonitor.Host.Tray.UiPrefsStore(Path.Combine(_dir, "prefs.json")), services,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<iCemDataMonitor.Host.Grpc.TelemetryServiceImpl>.Instance);
        var request = new iCemDataMonitor.Contracts.LayerControlRequest
            { Layer = iCemDataMonitor.Contracts.LayerKind.Modbus, DesiredState = false };
        Assert.True((await service.SetLayer(request, new TestCallContext())).Modbus);
        Assert.False((await service.SetLayer(request, new TestCallContext())).Modbus);
        Assert.False((await service.SetLayer(new() { Layer = iCemDataMonitor.Contracts.LayerKind.Web }, new TestCallContext())).Web);
        await service.SetLayer(new() { Layer = iCemDataMonitor.Contracts.LayerKind.OpcDa }, new TestCallContext());
        Assert.Equal(new[] { "start", "stop" }, actuator.Calls);
    }

    [Fact]
    public async Task StateChanged_CallbackCanReadAndSubmitCommand()
    {
        var c = Create(new Actuator { BlockStart = false });
        int notifications = 0;
        c.StateChanged += () =>
        {
            Assert.False(c.CaptureStatus().Busy);
            if (++notifications == 1) c.Toggle(TrayToggleRules.LayerKind.Web);
        };
        await Task.Run(() => c.ToggleAsync(TrayToggleRules.LayerKind.OpcDa)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, notifications);
        Assert.False(c.Layers.Web);
    }
}
