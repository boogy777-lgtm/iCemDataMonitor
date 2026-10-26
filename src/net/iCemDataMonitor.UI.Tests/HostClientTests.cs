using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.HostClient;
using Xunit;

namespace iCemDataMonitor.UI.Tests;

// ═══════════════════════════════════════════════════════════════════════════
// D.5 unit tests for the WinUI host-client layer (iCemDataMonitor.HostClient).
// The shell is now a CLIENT of the headless host (in-proc hosting removed —
// GAP-2), so the decision table / FSM / banner logic moved into a net8 library
// where it is testable WITHOUT launching WinUI.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>B.2.1 ensure-host decision table (pipe = authoritative probe).</summary>
public class EnsureHostDecisionTests
{
    [Fact]
    public void MutexAndPipe_Running()
    {
        var v = EnsureHost.Decide(mutexExists: true, pipeResponds: true);
        Assert.Equal(HostPresence.Running, v.Presence);
    }

    [Fact]
    public void NoMutexNoPipe_NotRunning()
    {
        var v = EnsureHost.Decide(mutexExists: false, pipeResponds: false);
        Assert.Equal(HostPresence.NotRunning, v.Presence);
        Assert.Contains("не запущен", v.Detail);
    }

    [Fact]
    public void MutexButNoPipe_NotRunning_WithHint()
    {
        // host is starting up or wedged — the shell spawns it IMMEDIATELY
        // (2026-08-31 decree: no dialog), but the detail explains WHY the
        // probe failed (operator diagnostics in startup.log).
        var v = EnsureHost.Decide(mutexExists: true, pipeResponds: false);
        Assert.Equal(HostPresence.NotRunning, v.Presence);
        Assert.Contains("mutex", v.Detail);
    }

    [Fact]
    public void PipeAnswersWithoutMutex_Running_PipeWins()
    {
        // mutex probe is advisory only; a serving pipe means RUNNING.
        var v = EnsureHost.Decide(mutexExists: false, pipeResponds: true);
        Assert.Equal(HostPresence.Running, v.Presence);
    }
}

/// <summary>Tray tooltip formatter (alive/dead + heartbeat pulse).</summary>
public class HostTooltipTests
{
    [Fact]
    public void Alive_ContainsPulse()
    {
        var text = HostTooltip.Format(hostAlive: true, heartbeat: 2139);
        Assert.Contains("хост работает", text);
        Assert.Contains("2139", text);
        Assert.DoesNotContain("НЕ", text);
    }

    [Fact]
    public void Dead_ExplicitState()
    {
        var text = HostTooltip.Format(hostAlive: false, heartbeat: 0);
        Assert.Contains("НЕ запущен", text);
    }
}

/// <summary>
/// B.2.1 path resolution for [Запустить хост]: sibling first, dev-layout
/// fallback second. Pure candidate-list test on a synthetic tree.
/// </summary>
public class HostProcessLauncherTests : IDisposable
{
    private readonly string _root;

    public HostProcessLauncherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hostlaunch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Sibling_Exe_Wins()
    {
        var shellDir = Path.Combine(_root, "deploy");
        Directory.CreateDirectory(shellDir);
        File.WriteAllText(Path.Combine(shellDir, "iCemDataMonitor.Host.exe"), "stub");

        var resolved = HostProcessLauncher.ResolveHostExePath(shellDir);
        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(shellDir, "iCemDataMonitor.Host.exe"), resolved);
    }

    [Fact]
    public void ParentFallback_CliSubfolder_FindsRootHost()
    {
        // Flat monitor-root layout: cli exe lives in cli\, the host is one
        // level up at the monitor root.
        var cliDir = Path.Combine(_root, "deploy", "cli");
        Directory.CreateDirectory(cliDir);
        var hostAtRoot = Path.Combine(_root, "deploy", "iCemDataMonitor.Host.exe");
        File.WriteAllText(hostAtRoot, "stub");

        var resolved = HostProcessLauncher.ResolveHostExePath(cliDir);
        Assert.NotNull(resolved);
        Assert.Equal(hostAtRoot, resolved);
    }

    [Fact]
    public void MsiLayout_ShellSubfolder_FindsSiblingHostFolder()
    {
        // MSI production layout (P1 2026-08-26): <monitor>\shell\ + <monitor>\host\
        // — the host is NOT at the monitor root and NOT next to the shell exe.
        // Owner complaint #1: resolution returned null ⇒ launch aborted BEFORE
        // the boot splash ever appeared.
        var shellDir = Path.Combine(_root, "monitor", "shell");
        var hostDir = Path.Combine(_root, "monitor", "host");
        Directory.CreateDirectory(shellDir);
        Directory.CreateDirectory(hostDir);
        var hostInHostFolder = Path.Combine(hostDir, "iCemDataMonitor.Host.exe");
        File.WriteAllText(hostInHostFolder, "stub");

        var resolved = HostProcessLauncher.ResolveHostExePath(shellDir);
        Assert.NotNull(resolved);
        Assert.Equal(hostInHostFolder, resolved);
    }

    [Fact]
    public void MsiLayout_CliSubfolder_FindsSiblingHostFolder_Too()
    {
        // Same MSI layout for the CLI (icemdatamonitor-cli --start uses the
        // SAME resolver): <monitor>\cli\ must resolve <monitor>\host\.
        var cliDir = Path.Combine(_root, "monitor", "cli");
        var hostDir = Path.Combine(_root, "monitor", "host");
        Directory.CreateDirectory(cliDir);
        Directory.CreateDirectory(hostDir);
        var hostInHostFolder = Path.Combine(hostDir, "iCemDataMonitor.Host.exe");
        File.WriteAllText(hostInHostFolder, "stub");

        var resolved = HostProcessLauncher.ResolveHostExePath(cliDir);
        Assert.NotNull(resolved);
        Assert.Equal(hostInHostFolder, resolved);
    }

    [Fact]
    public void DevLayout_Fallback_FoundViaWalkUp()
    {
        // deep dir with NO sibling exe; the walk-up must find the dev-tree host
        var shellDir = Path.Combine(_root, "a", "b", "c", "shell");
        Directory.CreateDirectory(shellDir);
        var devHost = Path.Combine(_root, "iCemDataMonitor.Host", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(devHost);
        File.WriteAllText(Path.Combine(devHost, "iCemDataMonitor.Host.exe"), "stub");

        var resolved = HostProcessLauncher.ResolveHostExePath(shellDir);
        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(devHost, "iCemDataMonitor.Host.exe"), resolved);
    }

    [Fact]
    public void NothingFound_Null_NoThrow()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Null(HostProcessLauncher.ResolveHostExePath(empty));
    }
}

/// <summary>Fake gRPC surface: controllable metrics stream + RPC recorder.</summary>
internal sealed class FakeHostApi : IHostApi
{
    public sealed class FakeStream : IMetricsStream
    {
        public event Action<MetricsSample>? Sample;
        public event Action<Exception>? Failed;
        public bool Disposed { get; private set; }
        public void Fire(MetricsSample s) => Sample?.Invoke(s);
        public void Fail(Exception e) => Failed?.Invoke(e);
        public void Dispose() => Disposed = true;
    }

    public FakeStream? LastStream { get; private set; }
    public int StreamCount { get; private set; }
    public List<(LayerKind Layer, bool Desired)> SetLayerCalls { get; } = new();

    public Task<HostStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new HostStatus { BusOpen = true, Heartbeat = 100 });

    public IMetricsStream SubscribeMetrics(int intervalMs)
    {
        var stream = new FakeStream();
        LastStream = stream;
        StreamCount++;
        return stream;
    }

    public Task<LayerState> SetLayerAsync(LayerKind layer, bool desiredState, CancellationToken ct = default)
    {
        SetLayerCalls.Add((layer, desiredState));
        return Task.FromResult(new LayerState { Modbus = desiredState });
    }

    // TOMBSTONE (2026-08-27): ControlEngineAsync REMOVED — the emulator is a
    // separate independent app, no engine control over the pipe anymore.

    // W20 F.1: additive IHostApi member — the fake records the requested zone.
    public List<BusZoneKind> GetRegistersCalls { get; } = new();

    public Task<RegistersReply> GetRegistersAsync(BusZoneKind zone, CancellationToken ct = default)
    {
        GetRegistersCalls.Add(zone);
        return Task.FromResult(new RegistersReply());
    }

    // W21.4: additive IHostApi members — recorded, honest defaults.
    public List<bool> SetConsoleVisibleCalls { get; } = new();
    public List<bool> SetAutostartCalls { get; } = new();
    public List<UiSection> NavigateUiCalls { get; } = new();
    public UiSection PendingSection { get; set; } = UiSection.Unspecified;

    public Task<HostStatus> SetConsoleVisibleAsync(bool visible, CancellationToken ct = default)
    {
        SetConsoleVisibleCalls.Add(visible);
        return Task.FromResult(new HostStatus { ConsoleVisible = visible });
    }

    public Task<HostStatus> SetAutostartAsync(bool enabled, CancellationToken ct = default)
    {
        SetAutostartCalls.Add(enabled);
        return Task.FromResult(new HostStatus { AutostartEnabled = enabled });
    }

    public Task<NavigateUiResponse> NavigateUiAsync(UiSection section, CancellationToken ct = default)
    {
        NavigateUiCalls.Add(section);
        return Task.FromResult(new NavigateUiResponse { UiAlive = true, Pending = PendingSection });
    }

    // 2026-08-31 (owner decree «даже смена темы — тоже CLI»): the station-wide
    // theme bool rides the client surface — STATEFUL so the fake models the
    // fresh-truth law (SetUiTheme's answer carries the new value, GetUiPrefs
    // reads the same truth; the default is the station default = dark).
    public bool ThemeLight { get; set; }
    public List<bool> SetUiThemeCalls { get; } = new();
    public int GetUiPrefsCalls { get; private set; }

    public Task<UiPrefs> GetUiPrefsAsync(CancellationToken ct = default)
    {
        GetUiPrefsCalls++;
        return Task.FromResult(new UiPrefs { ThemeLight = ThemeLight });
    }

    public Task<UiPrefs> SetUiThemeAsync(bool themeLight, CancellationToken ct = default)
    {
        SetUiThemeCalls.Add(themeLight);
        ThemeLight = themeLight;
        return Task.FromResult(new UiPrefs { ThemeLight = ThemeLight });
    }

    public IBootStageStream SubscribeBootStages() => new FakeBootStream();

    public sealed class FakeBootStream : IBootStageStream
    {
        public event Action<BootStage>? Stage;
        public event Action<Exception>? Failed;
        public void Fire(BootStage s) => Stage?.Invoke(s);
        // W22: Fail raiser mirrors FakeStream — the event is part of the
        // IBootStageStream contract, so the fake can simulate a dead stream.
        public void Fail(Exception e) => Failed?.Invoke(e);
        public void Dispose() { }
    }

    // E.2.7: ShutdownHostAsync removed from IHostApi (host closes ONLY from its
    // own tray) — the fake no longer implements it either.

    public void Dispose() { }
}

/// <summary>D.2.4 reconnect FSM over the fake stream (no real timers).</summary>
public class HostLinkReconnectTests
{
    private sealed class ImmediateScheduler : List<(TimeSpan Delay, Action Retry)>
    {
        public void RunAll()
        {
            while (Count > 0)
            {
                var (_, retry) = this[0];
                RemoveAt(0);
                retry();
            }
        }
    }

    private static DateTimeOffset _now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static HostLink MakeLink(FakeHostApi api, ImmediateScheduler scheduler, Random jitter) =>
        new(
            apiFactory: () => api,
            clock: () => _now,
            scheduleRetry: (delay, action) => scheduler.Add((delay, action)),
            backoffJitter: jitter);

    [Fact]
    public void Connected_Lost_Reconnecting_BackToConnected()
    {
        var api = new FakeHostApi();
        var sched = new ImmediateScheduler();
        var link = MakeLink(api, sched, new Random(7));
        var states = new List<HostLinkState>();
        using var sub = link.States.Subscribe(states.Add);

        link.Start();
        Assert.Equal(HostLinkState.Lost, link.State); // no data yet — not connected

        api.LastStream!.Fire(new MetricsSample { Heartbeat = 1 });
        Assert.Equal(HostLinkState.Connected, link.State);

        api.LastStream.Fail(new InvalidOperationException("boom")); // stream died
        // The FSM moves Lost→Reconnecting SYNCHRONOUSLY inside failure handling
        // (retry scheduled before Fail() returns); the transient Lost is
        // observable via the States stream.
        Assert.Equal(HostLinkState.Reconnecting, link.State);
        Assert.True(sched.Count > 0); // a retry IS scheduled (¬silent)
        Assert.Contains(HostLinkState.Lost, states);

        _now += TimeSpan.FromSeconds(10);
        sched.RunAll(); // fire the retry → fresh stream opens
        Assert.Equal(HostLinkState.Reconnecting, link.State);
        Assert.Equal(2, api.StreamCount);

        api.LastStream!.Fire(new MetricsSample { Heartbeat = 2 }); // data flows again
        Assert.Equal(HostLinkState.Connected, link.State);
        Assert.Contains(HostLinkState.Lost, states);
        Assert.Contains(HostLinkState.Reconnecting, states);
    }

    [Fact]
    public void Backoff_Sequence_3_6_12_24_Cap60_WithJitterBounds()
    {
        var api2 = new FakeHostApi();
        var captured = new List<TimeSpan>();
        Action? pendingRetry = null;
        var link2 = new HostLink(
            apiFactory: () => api2,
            clock: () => _now,
            scheduleRetry: (delay, action) => { captured.Add(delay); pendingRetry = action; },
            backoffJitter: new Random(42));

        link2.Start();
        for (int i = 0; i < 6; i++)
        {
            api2.LastStream!.Fail(new Exception("down"));
            // run the scheduled retry INLINE to arm the next stream attempt —
            // mirrors what the real timer would do between failures.
            var act = pendingRetry;
            pendingRetry = null;
            act?.Invoke();
        }

        Assert.Equal(6, captured.Count);
        foreach (var d in captured)
            Assert.True(d >= TimeSpan.FromSeconds(2.5), $"delay {d} below 90% bound");
        Assert.InRange(captured[0].TotalSeconds, 2.7, 3.3);   // 3s ±10%
        Assert.InRange(captured[1].TotalSeconds, 5.4, 6.6);   // 6s ±10%
        Assert.InRange(captured[2].TotalSeconds, 10.8, 13.2); // 12s ±10%
        Assert.InRange(captured[3].TotalSeconds, 21.6, 26.4); // 24s ±10%
        Assert.InRange(captured[4].TotalSeconds, 54.0, 66.0); // cap 60s ±10%
        Assert.InRange(captured[5].TotalSeconds, 54.0, 66.0); // stays capped
    }

    [Fact]
    public void StaleWatchdog_SilentStall_TriggersReconnect()
    {
        var api = new FakeHostApi();
        var sched = new ImmediateScheduler();
        var link = MakeLink(api, sched, new Random(5));
        link.Start();
        api.LastStream!.Fire(new MetricsSample()); // connected

        _now += TimeSpan.FromSeconds(10); // no samples, no error — silent stall
        link.CheckStale();
        Assert.NotEqual(HostLinkState.Connected, link.State);
        Assert.True(sched.Count > 0);
    }

    [Fact]
    public void FreshData_DoesNotTriggerWatchdog()
    {
        var api = new FakeHostApi();
        var sched = new ImmediateScheduler();
        var link = MakeLink(api, sched, new Random(5));
        link.Start();
        api.LastStream!.Fire(new MetricsSample());
        _now += TimeSpan.FromSeconds(1);
        link.CheckStale();
        Assert.Equal(HostLinkState.Connected, link.State);
        Assert.Empty(sched);
    }
}

/// <summary>Status view-model: stale banner, W11.1 toggle gates, degraded mode.</summary>
public class HostStatusViewModelTests
{
    private static DateTimeOffset _now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static HostStatus SampleStatus(bool modbus = false, bool sniffer = false) =>
        new()
        {
            BusOpen = true,
            AddinAlive = true,
            Heartbeat = 500,
            Layers = new LayerState { Modbus = modbus, Sniffer = sniffer },
            Zones =
            {
                new ZoneHealth { Kind = BusZoneKind.BusZoneCom, Active = true, AgeMs = 100 },
            },
        };

    [Fact]
    public void StaleBanner_Shows_Age_And_DoesNotMaskFacts()
    {
        var statusSubject = new Subject<HostStatus>();
        var samples = new Subject<MetricsSample>();
        var linkStates = new Subject<HostLinkState>();
        var hostAlive = new Subject<bool>();
        var watchdog = new Subject<Unit>();
        var clock = new Func<DateTimeOffset>(() => _now);

        // 2026-08-31 (BUG1 decree): liveness = the probe feed, NOT the data
        // stream — the test mirrors production and pushes hostAlive=true; a
        // stale data stream on an ALIVE host is the Degraded state (orange),
        // NEVER «host lost».
        var vm = new HostStatusViewModel(
            statusTicks: statusSubject,
            samples: samples,
            linkStates: linkStates,
            watchdogTicks: watchdog,
            control: null,
            degradedMode: false,
            clock: clock,
            hostAlive: hostAlive);

        hostAlive.OnNext(true);
        linkStates.OnNext(HostLinkState.Connected);
        statusSubject.OnNext(SampleStatus(modbus: true));
        samples.OnNext(new MetricsSample { Heartbeat = 777 });

        watchdog.OnNext(Unit.Default); // fresh — no banner
        Assert.Equal("", vm.ConnectionBanner);
        Assert.False(vm.IsLost);
        Assert.Contains("пульс 500", vm.BusText); // facts rendered

        _now += TimeSpan.FromSeconds(10); // silence past the 3 s threshold
        watchdog.OnNext(Unit.Default);
        Assert.True(vm.IsLost);
        // 2026-08-31 (BUG1 decree): the honest Degraded wording — the host is
        // alive, the DATA is stale (age visible — stale law preserved).
        Assert.Contains("хост работает", vm.ConnectionBanner);
        Assert.Contains("данные не обновляются", vm.ConnectionBanner);
        Assert.Contains("10с назад", vm.ConnectionBanner);
        // stale law: last known facts stay visible, NOT masked:
        Assert.Contains("пульс 500", vm.BusText);
        Assert.Contains("работает", vm.ModbusText);
    }

    [Fact]
    public void ToggleCommand_SendsFlippedDesiredState_ViaRpc()
    {
        var control = new FakeControl();
        var statusSubject = new Subject<HostStatus>();
        var vm = new HostStatusViewModel(
            statusTicks: statusSubject,
            samples: null,
            linkStates: null,
            watchdogTicks: null,
            control: control);

        statusSubject.OnNext(SampleStatus(modbus: false)); // truth = OFF
        vm.ToggleModbus.Execute().Subscribe();             // user flips ON
        // command runs on the taskpool — wait deterministically for the RPC:
        SpinUntil(() => control.Calls.Count > 0);
        Assert.Equal((LayerKind.Modbus, true), control.Calls[0]);
    }

    [Fact]
    public void ToggleGate_SwitchDoesNotFlipWhileExecuting_W11_1()
    {
        var tcs = new TaskCompletionSource<LayerState>();
        var control = new FakeControl { Gate = tcs.Task };
        var statusSubject = new Subject<HostStatus>();
        var vm = new HostStatusViewModel(
            statusTicks: statusSubject,
            samples: null,
            linkStates: null,
            watchdogTicks: null,
            control: control);

        statusSubject.OnNext(SampleStatus(modbus: false));
        Assert.False(vm.ModbusOn);

        vm.ToggleModbus.Execute().Subscribe();
        SpinUntil(() => control.Calls.Count > 0); // command started ⇒ IsExecuting=true
        statusSubject.OnNext(SampleStatus(modbus: false));
        Assert.False(vm.ModbusOn); // W11.1 gate holds: tick filtered while in flight

        tcs.SetResult(new LayerState { Modbus = true });
        // completion propagates IsExecuting=false on the taskpool — pump ticks
        // until the truth snaps in (deterministic outcome, bounded wait):
        SpinUntil(() =>
        {
            statusSubject.OnNext(SampleStatus(modbus: true));
            return vm.ModbusOn;
        });
        Assert.True(vm.ModbusOn);
    }

    private static void SpinUntil(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 5000)
            Thread.Sleep(10);
    }

    [Fact]
    public void DegradedMode_TogglesDisabled_HonestBanner()
    {
        var vm = new HostStatusViewModel(
            statusTicks: null,
            samples: null,
            linkStates: null,
            watchdogTicks: null,
            control: null,
            degradedMode: true);

        Assert.False(vm.CanToggle);
        Assert.True(vm.IsLost);
        Assert.Contains("ХОСТ НЕ ЗАПУЩЕН", vm.ConnectionBanner);
        Assert.Contains("только просмотр", vm.ConnectionBanner);
    }

    // ═══ W21.4/P2: console checkbox (host truth + SetConsoleVisible RPC) ═══

    [Fact]
    public void ConsoleCheckbox_MirrorsHostTruth_AndFlipsViaRpc()
    {
        var statusSubject = new Subject<HostStatus>();
        bool? requested = null;
        var vm = new HostStatusViewModel(
            statusTicks: statusSubject,
            samples: null,
            linkStates: null,
            watchdogTicks: null,
            control: new FakeControl(),
            setConsoleVisible: v =>
            {
                requested = v;
                return Task.FromResult(new HostStatus { ConsoleVisible = v });
            });

        statusSubject.OnNext(new HostStatus { ConsoleVisible = true });
        SpinUntil(() => vm.ConsoleOn);
        Assert.True(vm.CanToggleConsole);

        vm.ToggleConsole.Execute().Subscribe(); // truth=true ⇒ RPC must request FALSE
        SpinUntil(() => requested == false);
        Assert.False(requested!.Value);
        // P2 (owner complaint #2): the RPC response IS the fresh truth — the
        // checkbox must flip WITHOUT waiting for the next 1 Hz status tick.
        SpinUntil(() => !vm.ConsoleOn);
        // ...and still no tick was pushed after the initial one:
        Assert.False(vm.ConsoleOn);
    }

    [Fact]
    public void ConsoleCheckbox_DisabledWithoutControlOrDelegate()
    {
        var vm = new HostStatusViewModel(
            statusTicks: null, samples: null, linkStates: null, watchdogTicks: null,
            control: new FakeControl()); // no delegate
        Assert.False(vm.CanToggleConsole);

        var vm2 = new HostStatusViewModel(
            statusTicks: null, samples: null, linkStates: null, watchdogTicks: null,
            control: null, // no control channel (viewer)
            setConsoleVisible: _ => Task.FromResult(new HostStatus()));
        Assert.False(vm2.CanToggleConsole);
    }

    // ═══ 2026-08-27: autostart checkbox (host truth + SetAutostart RPC) ═══

    [Fact]
    public void AutostartCheckbox_MirrorsHostTruth_AndFlipsViaRpc()
    {
        var statusSubject = new Subject<HostStatus>();
        bool? requested = null;
        var vm = new HostStatusViewModel(
            statusTicks: statusSubject,
            samples: null,
            linkStates: null,
            watchdogTicks: null,
            control: new FakeControl(),
            setAutostart: v =>
            {
                requested = v;
                return Task.FromResult(new HostStatus { AutostartEnabled = v });
            });

        statusSubject.OnNext(new HostStatus { AutostartEnabled = true });
        SpinUntil(() => vm.AutostartOn);
        Assert.True(vm.CanToggleAutostart);

        vm.ToggleAutostart.Execute().Subscribe(); // truth=true ⇒ RPC must request FALSE
        SpinUntil(() => requested == false);
        Assert.False(requested!.Value);
        // Same fresh-response law as the console checkbox: the RPC response IS
        // the fresh truth — the checkbox must flip WITHOUT waiting for the
        // next 1 Hz status tick.
        SpinUntil(() => !vm.AutostartOn);
        Assert.False(vm.AutostartOn);
    }

    [Fact]
    public void AutostartCheckbox_DisabledWithoutControlOrDelegate()
    {
        var vm = new HostStatusViewModel(
            statusTicks: null, samples: null, linkStates: null, watchdogTicks: null,
            control: new FakeControl()); // no delegate
        Assert.False(vm.CanToggleAutostart);

        var vm2 = new HostStatusViewModel(
            statusTicks: null, samples: null, linkStates: null, watchdogTicks: null,
            control: null, // no control channel (viewer)
            setAutostart: _ => Task.FromResult(new HostStatus()));
        Assert.False(vm2.CanToggleAutostart);
    }

    // ═══ 2026-08-31 (owner decree «даже смена темы — тоже CLI»): the station
    // theme bool's client surface — GetUiPrefsAsync / SetUiThemeAsync. The
    // shell's client-mode persist goes through this seam (host writes the
    // shared ui-theme file + pokes the shell-owned event); this pin holds the
    // fresh-truth shape law: the SetUiTheme ANSWER carries the new value and
    // a later GetUiPrefs reads the same truth. ═══

    [Fact]
    public async Task ThemeSurface_SetUiTheme_IsFreshTruth_GetAgrees()
    {
        var api = new FakeHostApi();
        Assert.False(api.GetUiPrefsAsync().Result.ThemeLight); // station default = dark

        var fresh = await api.SetUiThemeAsync(true);
        Assert.True(fresh.ThemeLight);                       // answer = fresh truth, ¬ projection
        Assert.Equal(new[] { true }, api.SetUiThemeCalls);   // the requested bool was carried

        var again = await api.SetUiThemeAsync(true);         // idempotent repeat
        Assert.True(again.ThemeLight);

        var prefs = await api.GetUiPrefsAsync();
        Assert.True(prefs.ThemeLight);                       // one station bool, one truth
        Assert.Equal(2, api.GetUiPrefsCalls);
    }

    [Fact]
    public void AutostartCheckbox_GateHoldsWhileCommandInFlight_W11_1()
    {
        var tcs = new TaskCompletionSource<HostStatus>();
        bool delegateEntered = false;
        var statusSubject = new Subject<HostStatus>();
        var vm = new HostStatusViewModel(
            statusTicks: statusSubject,
            samples: null,
            linkStates: null,
            watchdogTicks: null,
            control: new FakeControl(),
            setAutostart: _ =>
            {
                delegateEntered = true; // command body entered ⇒ in flight
                return tcs.Task;
            });

        statusSubject.OnNext(new HostStatus { AutostartEnabled = true });
        SpinUntil(() => vm.AutostartOn);

        vm.ToggleAutostart.Execute().Subscribe();
        SpinUntil(() => delegateEntered); // RPC called, awaiting tcs ⇒ in flight
        // A tick with the OLD truth must NOT flip the checkbox while in flight.
        statusSubject.OnNext(new HostStatus { AutostartEnabled = true });
        Assert.True(vm.AutostartOn); // W11.1 gate holds: tick filtered while in flight

        tcs.SetResult(new HostStatus { AutostartEnabled = false });
        // Completion propagates IsExecuting=false on the taskpool — pump ticks
        // until the truth snaps in (deterministic outcome, bounded wait).
        SpinUntil(() =>
        {
            statusSubject.OnNext(new HostStatus { AutostartEnabled = false });
            return !vm.AutostartOn;
        });
        Assert.False(vm.AutostartOn);
    }

    // ═══ 2026-08-31 (BUG1 + decree 2): THREE-STATE button model ═══
    // (a) host alive + data flowing → green, restart ✓, launch ✗
    // (b) host alive + bus closed / stale data → orange, restart ✓, launch ✗
    // (c) host down / viewer → orange, restart ✗, launch ✓
    // BUG1 was: CanRestartHost followed the DATA flow (metrics stream aborts
    // with UNAVAILABLE on a closed bus) — a serving host had a greyed button.

    [Fact]
    public void RestartHostCommand_EnabledAndSignals_WhenHostAliveAndDataFlowing()
    {
        var hostAlive = new Subject<bool>();
        var samples = new Subject<MetricsSample>();
        var watchdog = new Subject<Unit>();
        var signals = 0;
        var vm = new HostStatusViewModel(
            statusTicks: null,
            samples: samples,
            linkStates: null,
            watchdogTicks: watchdog,
            control: new FakeControl(),
            clock: () => _now,
            signalHostRestart: () => signals++,
            hostAlive: hostAlive);

        hostAlive.OnNext(true);
        samples.OnNext(new MetricsSample { Heartbeat = 1 });
        watchdog.OnNext(Unit.Default); // fresh sample ⇒ (a) Healthy
        Assert.Equal(HostState.Healthy, vm.State);
        Assert.False(vm.IsLost);
        Assert.True(vm.CanRestartHost);
        Assert.False(vm.CanLaunchHost); // the host is alive — launch must not compete

        var canExecute = false;
        vm.RestartHost.CanExecute.Subscribe(c => canExecute = c);
        Assert.True(canExecute);

        vm.RestartHost.Execute().Subscribe();
        Assert.Equal(1, signals);
    }

    [Fact]
    public void RestartHostCommand_Enabled_EvenWhenDataStreamLost_IfHostAlive()
    {
        // THE BUG1 regression pin: link lost + stale samples on an ALIVE host
        // (the pipe serves GetStatus; only the metrics stream is down) — the
        // restart button must stay AVAILABLE.
        var hostAlive = new Subject<bool>();
        var linkStates = new Subject<HostLinkState>();
        var statusSubject = new Subject<HostStatus>();
        var watchdog = new Subject<Unit>();
        var signals = 0;
        var vm = new HostStatusViewModel(
            statusTicks: statusSubject,
            samples: null,
            linkStates: linkStates,
            watchdogTicks: watchdog,
            control: new FakeControl(),
            clock: () => _now,
            signalHostRestart: () => signals++,
            hostAlive: hostAlive);

        statusSubject.OnNext(SampleStatus()); // BusOpen=true — the bus IS open
        linkStates.OnNext(HostLinkState.Lost); // metrics stream aborted
        hostAlive.OnNext(true);                // ...but the host itself serves
        watchdog.OnNext(Unit.Default);         // ⇒ (b) Degraded, NOT Down

        Assert.Equal(HostState.Degraded, vm.State);
        Assert.True(vm.IsLost); // orange…
        Assert.Contains("хост работает", vm.ConnectionBanner);
        // No sample EVER arrived (samples: null) — the honest wording is the
        // "never flowed" variant, not an age against the clock epoch.
        Assert.Contains("данные не поступают", vm.ConnectionBanner);
        Assert.True(vm.CanRestartHost); // …but host controls stay enabled
        Assert.False(vm.CanLaunchHost);

        vm.RestartHost.Execute().Subscribe();
        Assert.Equal(1, signals);
    }

    [Fact]
    public void BusClosed_ShowsHonestText_RestartStaysEnabled()
    {
        // The stand scenario from the BUG1 report: the host runs, the AddIn/
        // iCem does NOT produce Data.dat ⇒ SubscribeMetrics aborts («bus is
        // not open»), GetStatus answers with BusOpen=false. Orange text must
        // say WHY, and the restart button must NOT be disabled.
        var hostAlive = new Subject<bool>();
        var statusSubject = new Subject<HostStatus>();
        var watchdog = new Subject<Unit>();
        var signals = 0;
        var vm = new HostStatusViewModel(
            statusTicks: statusSubject,
            samples: null,
            linkStates: null,
            watchdogTicks: watchdog,
            control: new FakeControl(),
            clock: () => _now,
            signalHostRestart: () => signals++,
            hostAlive: hostAlive);

        statusSubject.OnNext(new HostStatus { BusOpen = false }); // bus closed
        hostAlive.OnNext(true);
        watchdog.OnNext(Unit.Default);

        Assert.Equal(HostState.Degraded, vm.State);
        Assert.True(vm.IsLost);
        Assert.Contains("шина закрыта", vm.ConnectionBanner);
        Assert.Contains("запустите iCem", vm.ConnectionBanner);
        Assert.True(vm.CanRestartHost);
        Assert.False(vm.CanLaunchHost);
        Assert.Equal(0, signals); // nothing fired by the state change itself
    }

    [Fact]
    public void HostDown_RestartDisabled_LaunchEnabled_AndLaunches()
    {
        // (c) the host died while the shell was open: the probe flips false —
        // restart greys out (nothing to restart), launch takes over the row.
        var hostAlive = new Subject<bool>();
        var watchdog = new Subject<Unit>();
        var signals = 0;
        var launches = 0;
        var vm = new HostStatusViewModel(
            statusTicks: null,
            samples: null,
            linkStates: null,
            watchdogTicks: watchdog,
            control: new FakeControl(),
            clock: () => _now,
            signalHostRestart: () => signals++,
            hostAlive: hostAlive,
            launchHost: () => { launches++; return Task.CompletedTask; });

        hostAlive.OnNext(false);
        watchdog.OnNext(Unit.Default);

        Assert.Equal(HostState.Down, vm.State);
        Assert.True(vm.IsLost);
        Assert.Contains("хост не запущен", vm.ConnectionBanner);
        Assert.False(vm.CanRestartHost);
        var restartCanExecute = true;
        vm.RestartHost.CanExecute.Subscribe(c => restartCanExecute = c);
        Assert.False(restartCanExecute);

        Assert.True(vm.CanLaunchHost);
        var launchCanExecute = false;
        vm.LaunchHost.CanExecute.Subscribe(c => launchCanExecute = c);
        Assert.True(launchCanExecute);

        vm.LaunchHost.Execute().Subscribe();
        Assert.Equal(1, launches);
        Assert.Equal(0, signals); // no restart poke fired in the Down state
    }

    [Fact]
    public void LaunchCommand_Disabled_WhenHostAlive_OrWithoutDelegate()
    {
        var watchdog = new Subject<Unit>();
        var hostAlive = new Subject<bool>();
        var samples = new Subject<MetricsSample>();

        // Healthy state: the host is alive AND data flows — launch must be inert…
        var vm = new HostStatusViewModel(
            statusTicks: null, samples: samples, linkStates: null, watchdogTicks: watchdog,
            control: new FakeControl(), clock: () => _now,
            hostAlive: hostAlive,
            launchHost: () => Task.CompletedTask);
        hostAlive.OnNext(true);
        samples.OnNext(new MetricsSample { Heartbeat = 5 });
        watchdog.OnNext(Unit.Default);
        Assert.Equal(HostState.Healthy, vm.State);
        Assert.False(vm.CanLaunchHost);
        var canExecute = true;
        vm.LaunchHost.CanExecute.Subscribe(c => canExecute = c);
        Assert.False(canExecute); // the guard: the shell flow is never triggered while alive

        // …and Down WITHOUT the delegate (VM-level honesty): never enabled.
        var vm2 = new HostStatusViewModel(
            statusTicks: null, samples: null, linkStates: null, watchdogTicks: null,
            control: null, degradedMode: true, clock: () => _now);
        Assert.True(vm2.IsLost);
        Assert.False(vm2.CanLaunchHost);
    }

    [Fact]
    public void ViewerMode_BothButtonsHonest_LaunchEscapesReadOnly()
    {
        // Viewer/degraded mode: no control channel — restart impossible;
        // launch IS the sanctioned way out of the read-only mode (decree 2),
        // so it is enabled with a delegate and disabled without one.
        var viewer = new HostStatusViewModel(
            statusTicks: null, samples: null, linkStates: null, watchdogTicks: null,
            control: null, degradedMode: true, clock: () => _now,
            signalHostRestart: () => { });
        Assert.Equal(HostState.Down, viewer.State);
        Assert.Contains("ХОСТ НЕ ЗАПУЩЕН", viewer.ConnectionBanner);
        Assert.False(viewer.CanRestartHost);
        Assert.False(viewer.CanLaunchHost); // no delegate passed

        var launches = 0;
        var viewer2 = new HostStatusViewModel(
            statusTicks: null, samples: null, linkStates: null, watchdogTicks: null,
            control: null, degradedMode: true, clock: () => _now,
            launchHost: () => { launches++; return Task.CompletedTask; });
        Assert.True(viewer2.CanLaunchHost);
        viewer2.LaunchHost.Execute().Subscribe();
        Assert.Equal(1, launches);
    }

    internal sealed class FakeControl : IHostControl
    {
        public List<(LayerKind Layer, bool Desired)> Calls { get; } = new();
        public Task<LayerState>? Gate { get; init; }

        public Task<LayerState> SetLayerAsync(LayerKind layer, bool desiredState, CancellationToken ct = default)
        {
            Calls.Add((layer, desiredState));
            return Gate ?? Task.FromResult(new LayerState { Modbus = desiredState });
        }

        // TOMBSTONE (2026-08-27): ControlEngineAsync REMOVED — no engine
        // control channel exists anymore.
    }
}

