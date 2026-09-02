using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using iCemDataMonitor.Contracts;

namespace iCemDataMonitor.HostClient;

/// <summary>
/// 2026-08-31 (owner BUG1 decree): the THREE-STATE model of the status area.
/// The driver is HOST LIVENESS (pipe answers / process presence), NOT the
/// data flow — the metrics stream aborts with UNAVAILABLE when the bus is
/// closed («bus is not open») while the host itself serves GetStatus fine;
/// conflating the two disabled «Перезапуск хоста» on a healthy host.
///   Healthy  (a) host alive + data flowing        → green, restart ✓
///   Degraded (b) host alive + bus closed/no data  → orange, restart ✓
///   Down     (c) host not running / viewer mode   → orange, launch ✓, restart ✗
/// </summary>
public enum HostState
{
    Healthy,
    Degraded,
    Down,
}

/// <summary>
/// D.5.1/B.2.2 status page view-model: the data source is SubscribeMetrics +
/// periodic GetStatus over gRPC (the in-proc StationHealthState /
/// LayerRuntimeController polling of W11 is GONE — GAP-2).
///
/// Preserved doctrines:
/// - W11.1 toggle-switch law: while a toggle command is in flight its switch
///   state does NOT update (CombineLatest + Where(!IsExecuting) gate);
/// - stale law (D.5): no samples >3 s ⇒ the area goes ORANGE with the honest
///   reason and the age of the last sample; last known facts are NOT masked.
///   2026-08-31 (owner BUG1 decree): stale/no-data NO LONGER means «host
///   lost» — it is the Degraded state of an ALIVE host; the Down state (host
///   controls disabled) follows the hostAlive probe only;
/// - unidirectional flow: observables in, OAPH strings out.
///
/// Degraded mode («Только просмотр bus», host not running): control == null ⇒
/// toggles disabled and a permanent honest banner is shown; bus facts still
/// arrive via statusTicks (mapped from a direct read-only BusReader). The
/// «Запустить хост» button is the sanctioned way OUT of this mode.
/// </summary>
public sealed class HostStatusViewModel : ReactiveObject, IDisposable
{
    /// <summary>D.5/B.2.2 stale threshold for the lost-link banner.</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(3);

    private readonly CompositeDisposable _cleanup = new();
    private readonly IHostControl? _control;
    // W21.4/P2: SetConsoleVisible RPC seam — returns the FRESH HostStatus so
    // the checkbox re-arms immediately on RPC completion instead of waiting
    // for the next ~1 Hz status tick (owner complaint #2: perceived lag).
    private readonly Func<bool, Task<HostStatus>>? _setConsoleVisible;
    // 2026-08-27: SetAutostart RPC seam — same fresh-response law.
    private readonly Func<bool, Task<HostStatus>>? _setAutostart;
    // 2026-08-31 (owner decree): «Перезапуск хоста» button seam — pokes the
    // host's named restart event (E.2.7 preserved: the HOST restarts itself;
    // the shell only signals, the reverse of the tray [Закрыть] →
    // ShellShutdownEvent pattern). Client mode only — viewer mode has no host.
    private readonly Action? _signalHostRestart;
    // 2026-08-31 (decree 2): «Запустить хост» button seam — the SHELL runs the
    // SAME ensure-host flow as the cold start (probe → connect | spawn). The
    // VM only gates the button by the Down state; it knows nothing about
    // process spawning (B.2.1 stays in the shell layer).
    private readonly Func<Task>? _launchHost;
    private readonly bool _degradedMode;
    private readonly Func<DateTimeOffset> _clock;

    private volatile HostLinkState _linkState = HostLinkState.Lost;
    // 2026-08-31 (owner BUG1 decree): host liveness — fed by the shell's pipe
    // probe (GetStatus + mutex, EnsureHost.Decide table), NOT by the metrics
    // FSM. Honest prior when no probe observable is supplied: a client session
    // was JUST verified against the pipe (ensure-host flow), a viewer/degraded
    // session has no host — see the ctor.
    private volatile bool _hostAlive;
    // Last GetStatus bus truth (from status ticks) — picks the honest Degraded
    // wording: bus closed vs data stream stale. An error-swallowed tick
    // (empty HostStatus) briefly reports BusOpen=false; self-corrects on the
    // next 1 Hz tick and only affects the Degraded text, never the state.
    private volatile bool _lastBusOpen;
    private long _lastSampleAtTicks;
    private int _lastHeartbeat;
    private bool _modbusTruth;
    private bool _snifferTruth;
    private bool _consoleTruth;    // W21.4: host console visibility (host's truth)
    private bool _autostartTruth;  // 2026-08-27: HKCU Run-key truth (host's truth)

    private readonly ObservableAsPropertyHelper<string> _busText;
    private readonly ObservableAsPropertyHelper<string> _comText;
    private readonly ObservableAsPropertyHelper<string> _lanText;
    private readonly ObservableAsPropertyHelper<string> _rawText;
    private readonly ObservableAsPropertyHelper<string> _modbusText;
    private readonly ObservableAsPropertyHelper<string> _snifferText;
    private readonly ObservableAsPropertyHelper<string> _opcText;
    private readonly ObservableAsPropertyHelper<string> _statusLine;
    private readonly ObservableAsPropertyHelper<bool> _modbusOn;
    private readonly ObservableAsPropertyHelper<bool> _snifferOn;
    private readonly ObservableAsPropertyHelper<string> _connectionBanner;
    private readonly ObservableAsPropertyHelper<bool> _isLost;
    private readonly ObservableAsPropertyHelper<HostState> _hostState;
    private readonly ObservableAsPropertyHelper<bool> _consoleOn;
    private readonly ObservableAsPropertyHelper<bool> _autostartOn;

    public string BusText => _busText.Value;
    public string ComText => _comText.Value;
    public string LanText => _lanText.Value;
    public string RawText => _rawText.Value;
    public string ModbusText => _modbusText.Value;
    public string SnifferText => _snifferText.Value;
    public string OpcText => _opcText.Value;
    public string StatusLine => _statusLine.Value;
    public bool ModbusOn => _modbusOn.Value;
    public bool SnifferOn => _snifferOn.Value;

    /// <summary>2026-08-31 (owner BUG1 decree): area text; empty string =
    /// Healthy state. Degraded/Down carry the honest reason.</summary>
    public string ConnectionBanner => _connectionBanner.Value;

    /// <summary>2026-08-31 (owner BUG1 decree): TRUE = the area is ORANGE.
    /// Deliberately NOT «host lost» anymore — a stale data stream on an ALIVE
    /// host is orange too (Degraded), and host controls stay available.</summary>
    public bool IsLost => _isLost.Value;

    /// <summary>2026-08-31 (owner BUG1 decree): the three-state model — see
    /// <see cref="HostState"/>. Driver = host liveness (pipe probe), data
    /// flow only picks the honest Degraded wording.</summary>
    public HostState State => _hostState.Value;

    /// <summary>W21.4/P2: host-console visibility checkbox (mirrors the HOST's
    /// truth from status ticks AND from the SetConsoleVisible RPC response —
    /// gated while ToggleConsole is in flight).</summary>
    public bool ConsoleOn => _consoleOn.Value;

    /// <summary>2026-08-27: autostart checkbox (mirrors HostStatus.autostart_enabled
    /// from status ticks AND from the SetAutostart RPC response — same W11.1
    /// gate law as the console checkbox).</summary>
    public bool AutostartOn => _autostartOn.Value;

    public ReactiveCommand<Unit, Unit> ToggleModbus { get; }
    public ReactiveCommand<Unit, Unit> ToggleSniffer { get; }
    public ReactiveCommand<Unit, Unit> ToggleConsole { get; }
    public ReactiveCommand<Unit, Unit> ToggleAutostart { get; }

    /// <summary>2026-08-31 (owner decree): «Перезапуск хоста» — signals the
    /// host's named restart event; the host restarts ITSELF through the same
    /// path as its tray [Перезапуск]. 2026-08-31 (BUG1 decree): CanExecute =
    /// host ALIVE (Healthy OR Degraded) AND control channel present — a closed
    /// bus does NOT blind the operator to a serving host. Only Down greys it.</summary>
    public ReactiveCommand<Unit, Unit> RestartHost { get; }

    /// <summary>2026-08-31 (decree 2): «Запустить хост» — the shell runs the
    /// SAME ensure-host flow as the cold start (probe → connect | spawn, no
    /// dialog). CanExecute = Down state AND the launch delegate present
    /// (client and viewer sessions both pass it).</summary>
    public ReactiveCommand<Unit, Unit> LaunchHost { get; }

    /// <summary>False in degraded mode — switches stay disabled without the host.</summary>
    public bool CanToggle => _control is not null;

    /// <summary>W21.4: the console checkbox needs BOTH a control channel and
    /// the SetConsoleVisible delegate (client mode only).</summary>
    public bool CanToggleConsole => _control is not null && _setConsoleVisible is not null;

    /// <summary>2026-08-27: the autostart checkbox needs BOTH a control channel
    /// and the SetAutostart delegate (client mode only) — same honesty law as
    /// the console checkbox.</summary>
    public bool CanToggleAutostart => _control is not null && _setAutostart is not null;

    /// <summary>2026-08-31 (owner BUG1 decree): the restart button is enabled
    /// whenever the HOST is alive — Healthy AND Degraded (a closed bus must
    /// not blind host controls). Disabled only in the Down state (nothing to
    /// restart) and in viewer mode (no control channel).</summary>
    public bool CanRestartHost => _control is not null && _signalHostRestart is not null && State != HostState.Down;

    /// <summary>2026-08-31 (decree 2): the launch button is enabled ONLY in
    /// the Down state (host not running / viewer mode) — when the host is
    /// alive the restart button owns the row.</summary>
    public bool CanLaunchHost => _launchHost is not null && State == HostState.Down;

    /// <summary>Last heartbeat seen on the metrics stream (tray tooltip pulse).</summary>
    public int LastHeartbeat => _lastHeartbeat;

    /// <summary>True when the link is up AND the last sample is fresh.</summary>
    public bool IsAlive =>
        !_degradedMode && _linkState == HostLinkState.Connected && SampleAge() <= StaleThreshold;

    public HostStatusViewModel(
        IObservable<HostStatus>? statusTicks,
        IObservable<MetricsSample>? samples,
        IObservable<HostLinkState>? linkStates,
        IObservable<Unit>? watchdogTicks,
        IHostControl? control,
        bool degradedMode = false,
        Func<DateTimeOffset>? clock = null,
        Func<bool, Task<HostStatus>>? setConsoleVisible = null,
        Func<bool, Task<HostStatus>>? setAutostart = null,
        Action? signalHostRestart = null,
        IObservable<bool>? hostAlive = null,
        Func<Task>? launchHost = null)
    {
        _control = control;
        _degradedMode = degradedMode;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _setConsoleVisible = setConsoleVisible;
        _setAutostart = setAutostart;
        _signalHostRestart = signalHostRestart;
        _launchHost = launchHost;
        // Honest liveness prior (no probe tick yet): a client session was JUST
        // verified against the host pipe by the ensure-host flow; a viewer/
        // degraded session has no host. The probe observable (when supplied)
        // corrects this within its first tick.
        _hostAlive = !degradedMode && control is not null;
        var tick = statusTicks ?? Observable.Empty<HostStatus>();

        // ── toggle commands → SetLayer RPC (desired = flip of last known truth).
        // Block-bodied lambdas: the awaited result must be DISCARDED so the
        // command type stays ReactiveCommand<Unit, Unit>.
        var canToggle = this.WhenAnyValue(x => x.CanToggle);
        ToggleModbus = ReactiveCommand.CreateFromTask(
            async () => { await _control!.SetLayerAsync(LayerKind.Modbus, !_modbusTruth); }, canToggle);
        ToggleSniffer = ReactiveCommand.CreateFromTask(
            async () => { await _control!.SetLayerAsync(LayerKind.LanSniffer, !_snifferTruth); }, canToggle);
        // W21.4/P2: console checkbox → SetConsoleVisible RPC (same flip-of-truth
        // law). The RPC response IS the fresh host truth — adopt it immediately
        // so the checkbox never waits for the next 1 Hz tick (owner complaint #2).
        ToggleConsole = ReactiveCommand.CreateFromTask(
            async () =>
            {
                var fresh = await _setConsoleVisible!(!_consoleTruth);
                _consoleTruth = fresh.ConsoleVisible;
            },
            this.WhenAnyValue(x => x.CanToggleConsole));
        // 2026-08-27: autostart checkbox → SetAutostart RPC (same flip-of-truth
        // law as the console checkbox; the response IS the fresh host truth).
        ToggleAutostart = ReactiveCommand.CreateFromTask(
            async () =>
            {
                var fresh = await _setAutostart!(!_autostartTruth);
                _autostartTruth = fresh.AutostartEnabled;
            },
            this.WhenAnyValue(x => x.CanToggleAutostart));

        // ── truth cache refresh — subscribed BEFORE every projection that reads
        // it: within one tick the cache must already hold THIS tick's values,
        // otherwise a gate can observe a stale truth (race caught by W21.4 test).
        tick.Subscribe(s =>
        {
            var layers = s.Layers;
            if (layers is not null)
            {
                _modbusTruth = layers.Modbus;
                _snifferTruth = layers.Sniffer;
            }
            _consoleTruth = s.ConsoleVisible;     // W21.4: host's truth
            _autostartTruth = s.AutostartEnabled; // 2026-08-27: host's truth
            _lastBusOpen = s.BusOpen;             // 2026-08-31: Degraded wording
        }).DisposeWith(_cleanup);

        // ── fact projections (wording kept from the W11 status page) ───────────
        _busText = tick.Select(BusFact).ToProperty(this, x => x.BusText, initialValue: "iCem: …").DisposeWith(_cleanup);
        _comText = tick.Select(s => ZoneFact("COM", s, BusZoneKind.BusZoneCom)).ToProperty(this, x => x.ComText, initialValue: "COM: …").DisposeWith(_cleanup);
        _lanText = tick.Select(s => ZoneFact("LAN", s, BusZoneKind.BusZoneLan)).ToProperty(this, x => x.LanText, initialValue: "LAN: …").DisposeWith(_cleanup);
        _rawText = tick.Select(s => ZoneFact("RAW", s, BusZoneKind.BusZoneRaw)).ToProperty(this, x => x.RawText, initialValue: "RAW: …").DisposeWith(_cleanup);
        _modbusText = tick.Select(ModbusFact).ToProperty(this, x => x.ModbusText, initialValue: "Modbus TCP: …").DisposeWith(_cleanup);
        _snifferText = tick.Select(SnifferFact).ToProperty(this, x => x.SnifferText, initialValue: "Сниффер ACE LAN: …").DisposeWith(_cleanup);
        _opcText = tick.Select(OpcFact).ToProperty(this, x => x.OpcText, initialValue: "OPC DA (внешний)").DisposeWith(_cleanup);
        // TOMBSTONE (2026-08-27): WebText (веб-панель) and EmuleText (engine)
        // facts REMOVED — the web SPA is gone and the emulator is a separate
        // independent app.
        _statusLine = tick.Select(_ => _degradedMode
                ? "режим просмотра — хост не запущен · обновление: " + _clock().ToString("HH:mm:ss")
                : "обновление: " + _clock().ToString("HH:mm:ss"))
            .ToProperty(this, x => x.StatusLine, initialValue: "обновление: …").DisposeWith(_cleanup);

        // ── W11.1 switch gates: no update while the command is in flight ───────
        var modbusOnSource = tick.CombineLatest(ToggleModbus.IsExecuting, (s, exec) => (s, exec))
            .Where(x => !x.exec)
            .Select(_ => _modbusTruth);
        var snifferOnSource = tick.CombineLatest(ToggleSniffer.IsExecuting, (s, exec) => (s, exec))
            .Where(x => !x.exec)
            .Select(_ => _snifferTruth);
        _modbusOn = modbusOnSource.ToProperty(this, x => x.ModbusOn, initialValue: false).DisposeWith(_cleanup);
        _snifferOn = snifferOnSource.ToProperty(this, x => x.SnifferOn, initialValue: false).DisposeWith(_cleanup);

        // W21.4: console checkbox gate — same W11.1 law as the layer switches.
        var consoleOnSource = tick.CombineLatest(ToggleConsole.IsExecuting, (s, exec) => (s, exec))
            .Where(x => !x.exec)
            .Select(_ => _consoleTruth);
        _consoleOn = consoleOnSource.ToProperty(this, x => x.ConsoleOn, initialValue: false).DisposeWith(_cleanup);

        // 2026-08-27: autostart checkbox gate — same W11.1 law: no update while
        // the ToggleAutostart command is in flight.
        var autostartOnSource = tick.CombineLatest(ToggleAutostart.IsExecuting, (s, exec) => (s, exec))
            .Where(x => !x.exec)
            .Select(_ => _autostartTruth);
        _autostartOn = autostartOnSource.ToProperty(this, x => x.AutostartOn, initialValue: false).DisposeWith(_cleanup);

        // ── metrics stream bookkeeping (heartbeat pulse + freshness) ───────────
        samples?.Subscribe(s =>
        {
            Interlocked.Exchange(ref _lastSampleAtTicks, _clock().UtcTicks);
            Interlocked.Exchange(ref _lastHeartbeat, s.Heartbeat);
        }).DisposeWith(_cleanup);
        linkStates?.Subscribe(st => _linkState = st).DisposeWith(_cleanup);

        // 2026-08-31 (owner BUG1 decree): host LIVENESS probe feed — the shell
        // polls the host pipe (GetStatus + single-instance mutex via the SAME
        // EnsureHost.Decide table the cold start uses) every ~2 s. Deliberately
        // NOT the metrics FSM: the stream aborts with UNAVAILABLE when the bus
        // is closed while the host itself serves GetStatus fine.
        hostAlive?.Subscribe(v => _hostAlive = v).DisposeWith(_cleanup);

        // ── three-state model (D.5 stale law: explicit, ¬masked data) ──────────
        var stateSource = (watchdogTicks ?? Observable.Empty<Unit>())
            .Select(_ => ComputeState())
            .StartWith(ComputeState());
        _hostState = stateSource.ToProperty(this, x => x.State, initialValue: ComputeState()).DisposeWith(_cleanup);
        _connectionBanner = stateSource.Select(_ => BannerText()).ToProperty(this, x => x.ConnectionBanner, initialValue: BannerText()).DisposeWith(_cleanup);
        _isLost = stateSource.Select(s => s != HostState.Healthy).ToProperty(this, x => x.IsLost, initialValue: ComputeState() != HostState.Healthy).DisposeWith(_cleanup);

        // 2026-08-31 (owner decree): «Перезапуск хоста» button → named-event
        // poke. BUG1 law: enabled whenever the HOST is alive (Healthy OR
        // Degraded — a closed bus must not blind host controls); only the
        // Down state greys it out. ORDERING LAW: created AFTER the state
        // OAPH — WhenAnyValue over an OAPH-backed property needs the helper
        // to exist (a command built earlier crashes the pipeline with an NRE,
        // caught by the UI test run 2026-08-31).
        RestartHost = ReactiveCommand.Create(
            () => _signalHostRestart?.Invoke(),
            this.WhenAnyValue(x => x.State)
                .Select(s => s != HostState.Down && _control is not null && _signalHostRestart is not null));

        // 2026-08-31 (decree 2): «Запустить хост» — fire-and-forget trigger:
        // the SHELL runs the ensure-host flow (probe → connect | spawn) and
        // rebuilds the session; the command must NOT stay "executing" across
        // that rebuild (the VM itself is replaced on success). Double-clicks
        // are gated shell-side while the flow runs.
        LaunchHost = ReactiveCommand.Create(
            () => { _ = _launchHost?.Invoke(); },
            this.WhenAnyValue(x => x.State)
                .Select(s => s == HostState.Down && _launchHost is not null));
    }

    private TimeSpan SampleAge() =>
        TimeSpan.FromTicks(_clock().UtcTicks - Interlocked.Read(ref _lastSampleAtTicks));

    /// <summary>2026-08-31 (owner BUG1 decree): the decision table of the
    /// status area. Liveness first (owner's mental model), data freshness
    /// second — a stale stream NEVER masks a serving host.</summary>
    private HostState ComputeState()
    {
        if (_degradedMode || !_hostAlive)
            return HostState.Down;   // (c) viewer mode / host not running
        return SampleAge() <= StaleThreshold
            ? HostState.Healthy      // (a) host alive + data flowing
            : HostState.Degraded;    // (b) host alive + bus closed / no data
    }

    private string BannerText()
    {
        return ComputeState() switch
        {
            HostState.Down when _degradedMode =>
                "ХОСТ НЕ ЗАПУЩЕН — режим «только просмотр»: данные читаются с шины напрямую, управление недоступно",
            HostState.Down =>
                "хост не запущен",
            HostState.Degraded when !_lastBusOpen =>
                "хост работает · шина закрыта — запустите iCem или эмулятор",
            HostState.Degraded when Interlocked.Read(ref _lastSampleAtTicks) == 0 =>
                // No sample EVER arrived this session — an age against the
                // clock epoch would be meaningless garbage.
                "хост работает · данные не поступают",
            HostState.Degraded =>
                $"хост работает · данные не обновляются · последний сэмпл {(int)SampleAge().TotalSeconds}с назад",
            _ => "", // Healthy — green area, no banner
        };
    }

    // ── fact formatters (proto HostStatus in, UI strings out) ─────────────────

    private static string BusFact(HostStatus s) =>
        $"iCem: связь {(s.BusOpen ? "есть" : "НЕТ")} · " +
        (s.AddinAlive ? "AddIn отвечает" : "AddIn не отвечает") +
        $" · пульс {s.Heartbeat}";

    private static string ModbusFact(HostStatus s)
    {
        var l = s.Layers;
        if (l is null) return "Modbus TCP: нет данных";
        return "Modbus TCP :" + l.ModbusPort + (l.Modbus ? " работает" : " остановлен");
    }

    // TOMBSTONE (2026-08-27): WebFact («Веб-панель») and EmuleFact
    // («iCemEmule») REMOVED — the web SPA is gone; the emulator is a separate
    // independent app with its own UI.

    private static string SnifferFact(HostStatus s) =>
        s.Layers is null
            ? "Сниффер ACE LAN: нет данных"
            : "Сниффер ACE LAN: " + (s.Layers.Sniffer ? "захват идёт" : "на паузе");

    private static string OpcFact(HostStatus s) =>
        s.Layers is null
            ? "OPC DA (внешний)"
            : "OPC DA " + (s.Layers.OpcDa ? "(внешний — включён)" : "(внешний — выключен)");

    private static string ZoneFact(string name, HostStatus s, BusZoneKind kind)
    {
        var z = s.Zones.FirstOrDefault(z => z.Kind == kind);
        if (z is null) return $"{name}: нет данных";
        return z.Active
            ? $"{name}: данные идут (возраст {Age(z.AgeMs)})"
            : $"{name}: нет данных (возраст {Age(z.AgeMs)})";
    }

    private static string Age(long ms) =>
        ms < 1000 ? "<1с" : ms < 60_000 ? $"{ms / 1000}с" : $"{ms / 60_000}м";

    public void Dispose() => _cleanup.Dispose();
}
