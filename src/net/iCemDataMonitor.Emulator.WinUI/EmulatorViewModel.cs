using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using ReactiveUI;
using Halliburton.iCemEmule.Control;

namespace iCemDataMonitor.Emulator.WinUI;

/// <summary>One parsed status snapshot of the in-process engine (W11.6, extended W11.7).</summary>
public sealed record EmuSnapshot(
    bool Ok, string Uptime, string Pid,
    bool ComStreaming, string ComPort, string ComTemplate, string ComLines,
    bool LanStreaming, string LanAdapter, string LanEquipment, string LanFrames,
    string LanSvCount, string LanFirmware, string UnitsPreset,
    // W11.7
    bool LanFirmwareOn, int UnitsPresetIndex,
    string UnitFlowOv, string UnitTemperatureOv, string UnitDensityOv,
    string UnitVolumeOv, string UnitLevelOv, string UnitPressureOv,
    // W14: emulation gates (OFF = freeze, manual writes only)
    bool ComEmulating, bool LanEmulating,
    // 2026-08-29 (LAN UX): raw sent-frames counter + Npcap truth for the
    // header indicator («LAN: стрим → …» / «LAN: ⚠ Npcap не установлен»).
    long LanFramesSent, bool LanNpcapAvailable)
{
    public static EmuSnapshot Empty { get; } = new(
        false, "—", "—", false, "—", "—", "—", false, "—", "—", "—", "—", "—", "—",
        false, -1, "", "", "", "", "", "",
        // snapshot renders emulation gates OFF, matching the engine's passive
        // startup (no phantom ON switches before the first honest poll).
        false, false,
        // no phantom ⚠ before the first honest poll: assume Npcap present
        // until the engine says otherwise (old engines lack the field).
        0, true);
}

/// <summary>COM template profile row.</summary>
public sealed record EmuProfile(string Name, string DeviceType, bool Active);

/// <summary>COM variable row. W15 2026-08-28: mutable Value/Overridden with
/// update, zero ItemsSource rebuild ⇒ zero flicker; owner decree «обновление
/// должно быть нативным изменением цифры»). Name/Uom are template-fixed.</summary>
/// должно быть нативным изменением цифры»). Name/Uom are template-fixed.</summary>
public sealed class EmuComVar : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; }
    public string Uom { get; }
    private string _value;
    public string Value { get => _value; private set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } } }
    private bool _overridden;
    public bool Overridden { get => _overridden; private set { if (_overridden != value) { _overridden = value; PropertyChanged?.Invoke(this, new(nameof(Overridden))); } } }

    public EmuComVar(string name, string value, string uom, bool overridden)
    {
        Name = name; _value = value; Uom = uom; _overridden = overridden;
    }

    /// <summary>In-place refresh; raises PropertyChanged only on real changes.</summary>
    public void Update(string value, bool overridden) { Value = value; Overridden = overridden; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
/// <summary>LAN signal row (W14: + live Value из superframe-декода движка).
/// <summary>LAN signal row (W14: + live Value из superframe-декода движка).
/// W15 2026-08-28: INPC in-place update — see EmuComVar.</summary>
public sealed class EmuLanVar : System.ComponentModel.INotifyPropertyChanged
{
    public string Sv { get; }
    public string Name { get; }
    public string Uom { get; }
    private string _value;
    public string Value { get => _value; private set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } } }
    private bool _overridden;
    public bool Overridden { get => _overridden; private set { if (_overridden != value) { _overridden = value; PropertyChanged?.Invoke(this, new(nameof(Overridden))); } } }

    public EmuLanVar(string sv, string name, string value, string uom, bool overridden)
    {
        Sv = sv; Name = name; _value = value; Uom = uom; _overridden = overridden;
    }

    /// <summary>In-place refresh; raises PropertyChanged only on real changes.</summary>
    public void Update(string value, bool overridden) { Value = value; Overridden = overridden; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>W11.7: one value-edit request (COM: Sv null; LAN: Sv = "0x...").</summary>
public sealed record EmuVarEdit(string Channel, string? Sv, string Name, double Value);

/// <summary>
/// Emulator view-model (W11.6/W11.7, merged app 2026-08-27): REACTIVE — 1 s
/// `status` poll over the control pipe drives one Snapshot OAPH; the view
/// renders it (unidirectional). Toggles/profiles/vars/ports/adapters/
/// equipment/firmware/units are ReactiveCommands over the pipe.
///
/// MERGED-APP LAW (2026-08-27, owner decree «эмулятор = отдельное
/// независимое приложение со встроенным UI + CLI»): the engine runs
/// IN-PROCESS inside iCemEmulator.exe — there is NO process to launch or
/// stop, no host RPC, no EmuleSupervisor. The VM reaches the engine over the
/// SAME control pipe the CLI uses (P2 decree «UI должен использовать
/// CLI-команды как стандарт в нашей экосистеме») — the in-proc
/// ControlPipeServer is the ONE seam; the host-RPC seam
/// (IEngineControlChannel / GrpcEngineControl / EngineControl static) is
/// DELETED. Every toggle surfaces the engine's authoritative reply via the
/// command note — a refusal (e.g. «no COM port configured») is ACTIONABLE
/// text, never a silent snap-back.
/// </summary>
public sealed class EmulatorViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _cleanup = new();

    private readonly ObservableAsPropertyHelper<EmuSnapshot> _snapshot;

    private readonly BehaviorSubject<IReadOnlyList<EmuProfile>> _profilesSubject = new(Array.Empty<EmuProfile>());
    private readonly BehaviorSubject<IReadOnlyList<EmuComVar>> _comVarsSubject = new(Array.Empty<EmuComVar>());
    private readonly BehaviorSubject<IReadOnlyList<EmuLanVar>> _lanVarsSubject = new(Array.Empty<EmuLanVar>());
    private readonly BehaviorSubject<string> _varsNoteSubject = new("");

    // W11.7: endpoints + command feedback
    private readonly BehaviorSubject<IReadOnlyList<string>> _portsSubject = new(Array.Empty<string>());
    private readonly BehaviorSubject<IReadOnlyList<string>> _adaptersSubject = new(Array.Empty<string>());
    // 2026-08-29 (Npcap/adapter honesty): empty adapter list after a
    // SUCCESSFUL probe = say so next to the combo («⚠ Npcap не установлен…»)
    // instead of a dead empty combobox. "" = no hint.
    private readonly BehaviorSubject<string> _lanAdapterHintSubject = new("");
    private readonly BehaviorSubject<string> _commandNoteSubject = new("");
    // P4 2026-08-27 (lying-switch kill): the AUTHORITATIVE resulting stream
    // state from the engine's start/stop reply (data.streaming) — the view
    // renders the switch from IT immediately instead of waiting up to 2 s for
    // the next 1 Hz poll. A refused start pushes false ⇒ instant snap-back.
    private readonly BehaviorSubject<(string Channel, bool Streaming)> _toggleTruthSubject = new(("", false));

    // W15 2026-08-28: staged fresh row payloads (produced on the Taskpool
    // scheduler inside RefreshVarsCommand) and the coalescing subject that
    // moves the INPC row mutation onto the UI thread (see ctor).
    private readonly object _varsGate = new();
    private List<(string Name, string Value, string Uom, bool Overridden)>? _stagedCom;
    private List<(string Sv, string Name, string Value, string Uom, bool Overridden)>? _stagedLan;
    private readonly System.Reactive.Subjects.Subject<Unit> _varsApplyPendingObservable = new();
    private bool _varsApplyPending;

    // W15: UI-thread home for INPC row updates — set by EmulatorPage`s ctor
    // (the UI thread; RxApp.MainThreadScheduler is NOT UI-bound in a raw
    // WinUI standalone — ObserveOn on it ran on a LongRunning background
    // thread: 0x8001010E again, crash.log 2026-08-29). Hard-wired through
    // the page`s DispatcherQueue — TryEnqueue → guaranteed UI thread.
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    /// <summary>Called by EmulatorPage`s ctor (UI thread) — the ONLY UI
    /// scheduler the row-apply path trusts.</summary>
    internal void BindUi(Microsoft.UI.Dispatching.DispatcherQueue dispatcher) =>
        _uiDispatcher = dispatcher;


    /// <summary>
    /// Merged app: the engine is IN-PROCESS — the page never renders a launch
    /// stub. The status line says so honestly.
    /// </summary>
    public string StatusText { get; } = "Эмулятор работает — движок встроен в приложение";

    public EmuSnapshot Snapshot => _snapshot.Value;
    /// <summary>W11.6: observable streams (BehaviorSubject) — no INPC surface needed.</summary>
    public IObservable<IReadOnlyList<EmuProfile>> Profiles => _profilesSubject;
    public IObservable<IReadOnlyList<EmuComVar>> ComVars => _comVarsSubject;
    public IObservable<IReadOnlyList<EmuLanVar>> LanVars => _lanVarsSubject;
    public IObservable<string> VarsNote => _varsNoteSubject;
    /// <summary>W11.7.</summary>
    public IObservable<IReadOnlyList<string>> Ports => _portsSubject;
    public IObservable<IReadOnlyList<string>> Adapters => _adaptersSubject;
    /// <summary>2026-08-29: «⚠ Npcap не установлен…» / «⚠ адаптеры не найдены» when the probed adapter list is empty; "" otherwise.</summary>
    public IObservable<string> LanAdapterHint => _lanAdapterHintSubject;
    /// <summary>W11.7: last command outcome (ok/error), rendered as one note line.</summary>
    public IObservable<string> CommandNote => _commandNoteSubject;
    /// <summary>P4 2026-08-27: engine's authoritative stream state per channel
    /// from the start/stop reply — the view snaps the switch to it instantly.</summary>
    public IObservable<(string Channel, bool Streaming)> ToggleTruth => _toggleTruthSubject;

    /// <summary>P3 2026-08-27: the view passes the SWITCH'S OWN target state
    /// (IsOn right after the user flipped it). Reading the snapshot here was
    /// a stale-read race: two rapid clicks (on→off) both saw the pre-toggle
    /// state and sent the SAME command, so the switch desynced from the
    /// engine (operator's second intent silently lost).</summary>
    public ReactiveCommand<bool, Unit> ToggleComCommand { get; }
    public ReactiveCommand<bool, Unit> ToggleLanCommand { get; }
    public ReactiveCommand<string, Unit> SetProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshVarsCommand { get; }

    // W11.7: full functional surface (engine commands already live + verified)
    public ReactiveCommand<Unit, Unit> RefreshEndpointsCommand { get; }
    public ReactiveCommand<string, Unit> SetPortCommand { get; }
    public ReactiveCommand<string, Unit> SetAdapterCommand { get; }
    public ReactiveCommand<string, Unit> SetEquipmentCommand { get; }
    public ReactiveCommand<bool, Unit> SetFirmwareCommand { get; }
    public ReactiveCommand<int, Unit> SetPresetCommand { get; }
    /// <summary>W14: emulation on/off per channel (freeze = manual writes only).</summary>
    public ReactiveCommand<(string Channel, bool On), Unit> SetEmulateCommand { get; }
    /// <summary>(group, unit) — empty unit clears the override.</summary>
    public ReactiveCommand<(string Group, string Unit), Unit> SetUnitCommand { get; }
    public ReactiveCommand<EmuVarEdit, Unit> SetVarCommand { get; }
    public ReactiveCommand<EmuVarEdit, Unit> ClearVarCommand { get; }

    public EmulatorViewModel()
    {
        UiTrace.Log("EmulatorViewModel ctor");
        var ticks = Observable
            .Interval(TimeSpan.FromSeconds(1), RxApp.TaskpoolScheduler);

        _snapshot = ticks.Select(_ => FetchStatus()).ToProperty(this, x => x.Snapshot,
            initialValue: EmuSnapshot.Empty)
            .DisposeWith(_cleanup);

        // W15 2026-08-28: staged row payloads land on the UI thread — the
        // page`s DispatcherQueue (BindUi), NOT RxApp.MainThreadScheduler (it is
        // NOT UI-bound in a raw WinUI standalone: ObserveOn ran on a LongRunning
        // background thread → 0x8001010E again, crash.log 2026-08-29). Until the
        // page binds, applies drop silently (the first refresh re-stages — no data lost).
        _varsApplyPendingObservable
            .Subscribe(_ =>
            {
                var d = _uiDispatcher;
                if (d is not null)
                    d.TryEnqueue(ApplyStagedVars);
            })
            .DisposeWith(_cleanup);

        // P1/P2 2026-08-26 («переключатели не работают»): the toggle flips
        // the engine with ONE start/stop command and surfaces the
        // AUTHORITATIVE reply via the command note. The engine starts PASSIVE
        // (no port/adapter configured), so a bare `start` FAILS until the
        // operator picks an endpoint — that refusal used to be SILENT (switch
        // snapped back on the next poll = «кнопки не работают»).
        // NoteChannelToggle renders it actionably («выберите порт (Обновить
        // порты)»). A dead engine reports «движок не отвечает».
        // P3 2026-08-27: the target state comes from the view (the switch's
        // own IsOn) — see the property doc; the snapshot is NOT re-read.
        // P4 2026-08-27: (1) the pipe round trip moved OFF the UI thread —
        // ControlPipeClient.Cmd is synchronous and blocks up to the connect
        // timeout when the engine is busy/down; a frozen page read as a dead
        // switch. (2) The reply's data.streaming is pushed via ToggleTruth so
        // the switch renders the engine's truth instantly (success = immediate
        // ON/OFF, refused start = immediate snap-back OFF).
        ToggleComCommand = ReactiveCommand.CreateFromTask<bool>(async targetOn =>
        {
            await Task.Run(() =>
            {
                using var reply = ControlPipeClient.Cmd(targetOn ? "start" : "stop", "channel", "com");
                NoteChannelToggle(reply, "com", targetOn);
                PushToggleTruth(reply, "com", targetOn);
            });
            await Task.Delay(250); // let the engine flip state before the next poll renders
        });

        ToggleLanCommand = ReactiveCommand.CreateFromTask<bool>(async targetOn =>
        {
            await Task.Run(() =>
            {
                using var reply = ControlPipeClient.Cmd(targetOn ? "start" : "stop", "channel", "lan");
                NoteChannelToggle(reply, "lan", targetOn);
                PushToggleTruth(reply, "lan", targetOn);
            });
            await Task.Delay(250);
        });

        SetProfileCommand = ReactiveCommand.CreateFromTask<string>(async name =>
        {
            // W22: the client returns a pooled-memory JsonDocument — dispose every reply.
            using var reply = ControlPipeClient.Cmd("profile", "name", name);
            Note(reply, "профиль «" + name + "»");
            await Task.Delay(250);
        });

        RefreshVarsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await Task.Run(() =>
            {
                using var profiles = ControlPipeClient.Cmd("profiles");
                using var vars = ControlPipeClient.Cmd("vars");
                if (profiles is null || vars is null)
                {
                    // P3 2026-08-27: was a SILENT no-op (stale VarsNote, zero
                    // feedback on a pipe hiccup) — honest note instead.
                    _commandNoteSubject.OnNext("⚠ движок не отвечает — профили/переменные не обновлены");
                    return;
                }

                var profilesRoot = profiles.Raw.RootElement;
                var varsRoot = vars.Raw.RootElement;

                var profilesList = new List<EmuProfile>();
                if (profilesRoot.TryGetProperty("ok", out var pok) && pok.GetBoolean() &&
                    profilesRoot.TryGetProperty("data", out var pdata) &&
                    pdata.TryGetProperty("profiles", out var plist))
                {
                    foreach (var p in plist.EnumerateArray())
                    {
                        profilesList.Add(new EmuProfile(
                            p.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?",
                            p.TryGetProperty("deviceType", out var d) ? d.GetString() ?? "" : "",
                            p.TryGetProperty("active", out var a) && a.GetBoolean()));
                    }
                }
                _profilesSubject.OnNext(profilesList);

                // W15: fresh rows as plain data — applied IN PLACE over the
                // existing INPC rows (native digit updates, no rebuild).
                var comFresh = new List<(string Name, string Value, string Uom, bool Overridden)>();
                var lanFresh = new List<(string Sv, string Name, string Value, string Uom, bool Overridden)>();
                if (varsRoot.TryGetProperty("ok", out var vok) && vok.GetBoolean() &&
                    varsRoot.TryGetProperty("data", out var vdata))
                {
                    if (vdata.TryGetProperty("com", out var com) && com.TryGetProperty("variables", out var cvars))
                    {
                        foreach (var v in cvars.EnumerateArray())
                        {
                            comFresh.Add((
                                v.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?",
                                v.TryGetProperty("value", out var val) ? FmtNum(val) : "—",
                                v.TryGetProperty("uom", out var u) ? u.GetString() ?? "" : "",
                                v.TryGetProperty("overridden", out var o) && o.GetBoolean()));
                        }
                    }
                    if (vdata.TryGetProperty("lan", out var lan) && lan.TryGetProperty("variables", out var lvars))
                    {
                        foreach (var v in lvars.EnumerateArray())
                        {
                            lanFresh.Add((
                                v.TryGetProperty("sv", out var sv) ? sv.GetString() ?? "?" : "?",
                                v.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?",
                                v.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Number ? FmtNum(val) : "—",
                                v.TryGetProperty("uom", out var u) ? u.GetString() ?? "" : "",
                                v.TryGetProperty("overridden", out var o) && o.GetBoolean()));
                        }
                    }
                }
                StageVars(comFresh, lanFresh);
                _varsNoteSubject.OnNext("обновлено " + DateTime.Now.ToString("HH:mm:ss"));
            });
        });

        /// <summary>W15: stage the fresh payloads (Taskpool thread) and request
        /// ONE coalesced UI-thread apply. The INPC row mutation itself happens
        /// on the UI thread (ApplyStagedVars via _varsApplyPendingObservable).</summary>
        void StageVars(
            List<(string Name, string Value, string Uom, bool Overridden)> comFresh,
            List<(string Sv, string Name, string Value, string Uom, bool Overridden)> lanFresh)
        {
            lock (_varsGate)
            {
                _stagedCom = comFresh;
                _stagedLan = lanFresh;
                if (_varsApplyPending) return;   // an apply is already queued
                _varsApplyPending = true;
            }
            _varsApplyPendingObservable.OnNext(Unit.Default);
        }

        /// <summary>W15: runs on the UI thread (ObserveOnMain). Mutates the
        /// existing INPC rows in place — native digit updates, zero rebuild;
        /// a template swap (name/Sv sequence differs) rebuilds the rows.</summary>
        void ApplyStagedVars()
        {
            List<(string Name, string Value, string Uom, bool Overridden)>? comFresh;
            List<(string Sv, string Name, string Value, string Uom, bool Overridden)>? lanFresh;
            lock (_varsGate)
            {
                _varsApplyPending = false;
                comFresh = _stagedCom;
                lanFresh = _stagedLan;
            }
            if (comFresh is not null) ApplyComVars(comFresh);
            if (lanFresh is not null) ApplyLanVars(lanFresh);
        }

        /// <summary>W15: apply fresh COM data in place — same list instance,
        /// INPC rows raise PropertyChanged only for real digit changes; a
        /// template swap (name sequence differs) rebuilds the rows.</summary>
        void ApplyComVars(List<(string Name, string Value, string Uom, bool Overridden)> fresh)
        {
            var rows = _comVarsSubject.Value as List<EmuComVar>;
            if (rows is null || rows.Count != fresh.Count ||
                !rows.Select((r, i) => r.Name == fresh[i].Name).All(match => match))
            {
                _comVarsSubject.OnNext(fresh
                    .Select(f => new EmuComVar(f.Name, f.Value, f.Uom, f.Overridden))
                    .ToList());
                return;
            }
            for (int i = 0; i < rows.Count; i++)
                rows[i].Update(fresh[i].Value, fresh[i].Overridden);
            _comVarsSubject.OnNext(rows);   // same instance — the view skips re-set
        }

        /// <summary>W15: apply fresh LAN data in place — see ApplyComVars (key = Sv).</summary>
        void ApplyLanVars(List<(string Sv, string Name, string Value, string Uom, bool Overridden)> fresh)
        {
            var rows = _lanVarsSubject.Value as List<EmuLanVar>;
            if (rows is null || rows.Count != fresh.Count ||
                !rows.Select((r, i) => r.Sv == fresh[i].Sv).All(match => match))
            {
                _lanVarsSubject.OnNext(fresh
                    .Select(f => new EmuLanVar(f.Sv, f.Name, f.Value, f.Uom, f.Overridden))
                    .ToList());
                return;
            }
            for (int i = 0; i < rows.Count; i++)
                rows[i].Update(fresh[i].Value, fresh[i].Overridden);
            _lanVarsSubject.OnNext(rows);   // same instance — the view skips re-set
        }

        // ── W11.7 commands ────────────────────────────────────────────────

        RefreshEndpointsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await Task.Run(() =>
            {
                using var ports = ControlPipeClient.Cmd("ports");
                JsonElement plist = default;
                bool portsOk = ports is not null &&
                    ports.Raw.RootElement.TryGetProperty("ok", out var pok) && pok.GetBoolean() &&
                    ports.Raw.RootElement.TryGetProperty("data", out var pdata) &&
                    pdata.TryGetProperty("ports", out plist);
                if (portsOk)
                {
                    _portsSubject.OnNext(plist.EnumerateArray()
                        .Select(p => p.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?")
                        .ToList());
                }

                using var adapters = ControlPipeClient.Cmd("adapters");
                JsonElement alist = default;
                bool adaptersOk = adapters is not null &&
                    adapters.Raw.RootElement.TryGetProperty("ok", out var aok) && aok.GetBoolean() &&
                    adapters.Raw.RootElement.TryGetProperty("data", out var adata) &&
                    adata.TryGetProperty("adapters", out alist);
                if (adaptersOk)
                {
                    var names = alist.EnumerateArray()
                        .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?")
                        .ToList();
                    _adaptersSubject.OnNext(names);

                    // 2026-08-29 (Npcap/adapter honesty): an EMPTY list after a
                    // successful probe used to render as a silent dead combo.
                    // npcapAvailable=false (engine 2026-08-29+) names the root
                    // cause; absent field (old engine) = generic hint.
                    bool npcapMissing =
                        adapters!.Raw.RootElement.TryGetProperty("data", out var adata2) &&
                        adata2.TryGetProperty("npcapAvailable", out var npv) &&
                        npv.ValueKind == JsonValueKind.False;
                    _lanAdapterHintSubject.OnNext(names.Count > 0 ? "" :
                        npcapMissing
                            ? "⚠ Npcap не установлен — установите Npcap (режим совместимости с WinPcap) и нажмите «Обновить адаптеры»"
                            : "⚠ сетевые адаптеры не найдены — проверьте Npcap и нажмите «Обновить адаптеры»");
                }

                // P3 2026-08-27: both probes failed ⇒ the pipe is dead — a
                // silent no-op here read as «кнопка не работает».
                if (!portsOk && !adaptersOk)
                    _commandNoteSubject.OnNext("⚠ движок не отвечает — порты/адаптеры не обновлены");
            });
        });

        SetPortCommand = ReactiveCommand.CreateFromTask<string>(async port =>
        {
            using var reply = ControlPipeClient.Cmd("setport", "name", port);
            Note(reply, "порт " + port);
            await Task.Delay(250);
        });

        SetAdapterCommand = ReactiveCommand.CreateFromTask<string>(async adapter =>
        {
            using var reply = ControlPipeClient.Cmd("setadapter", "name", adapter);
            Note(reply, "адаптер");
            await Task.Delay(250);
        });

        SetEquipmentCommand = ReactiveCommand.CreateFromTask<string>(text =>
        {
            if (!TryParseEquipment(text, out uint eq))
            {
                _commandNoteSubject.OnNext("⚠ оборудование: ожидается 0x… или десятичное число");
                return Task.CompletedTask;
            }
            using var reply = ControlPipeClient.Cmd("setequipment",
                new Dictionary<string, object?> { ["value"] = (double)eq });
            Note(reply, $"оборудование 0x{eq:X8}");
            return Task.CompletedTask;
        });

        SetFirmwareCommand = ReactiveCommand.CreateFromTask<bool>(async on =>
        {
            using var reply = ControlPipeClient.Cmd("setfirmware",
                new Dictionary<string, object?> { ["value"] = on ? 1.0 : 0.0 });
            Note(reply, "firmware-таблицы " + (on ? "вкл" : "выкл"));
            await Task.Delay(250);
        });

        SetPresetCommand = ReactiveCommand.CreateFromTask<int>(async index =>
        {
            using var reply = ControlPipeClient.Cmd("setpreset",
                new Dictionary<string, object?> { ["value"] = (double)index });
            Note(reply, "пресет единиц");
            await Task.Delay(250);
        });

        SetEmulateCommand = ReactiveCommand.CreateFromTask<(string Channel, bool On)>(async req =>
        {
            using var reply = ControlPipeClient.Cmd("setemulate",
                new Dictionary<string, object?> { ["channel"] = req.Channel, ["value"] = req.On ? 1.0 : 0.0 });
            Note(reply, $"эмуляция {req.Channel.ToUpperInvariant()} " + (req.On ? "вкл" : "выкл (freeze — только ручная запись)"));
            await Task.Delay(250);
        });

        SetUnitCommand = ReactiveCommand.CreateFromTask<(string Group, string Unit)>(async req =>
        {
            var args = new Dictionary<string, object?> { ["channel"] = req.Group };
            if (!string.IsNullOrWhiteSpace(req.Unit))
                args["name"] = req.Unit.Trim();
            using var reply = ControlPipeClient.Cmd("setunit", args);
            Note(reply, string.IsNullOrWhiteSpace(req.Unit)
                ? $"группа {req.Group}: по пресету"
                : $"группа {req.Group}: {req.Unit.Trim()}");
        });

        SetVarCommand = ReactiveCommand.CreateFromTask<EmuVarEdit>(async edit =>
        {
            var args = new Dictionary<string, object?>
            {
                ["channel"] = edit.Channel,
                ["name"] = edit.Name,
                ["value"] = edit.Value
            };
            if (edit.Sv is not null) args["sv"] = edit.Sv;
            using var reply = ControlPipeClient.Cmd("set", args);
            Note(reply, $"{edit.Name} = {edit.Value.ToString("G6", CultureInfo.InvariantCulture)}");
            RefreshVarsCommand.Execute().Subscribe();
            await Task.CompletedTask;
        });

        ClearVarCommand = ReactiveCommand.CreateFromTask<EmuVarEdit>(async edit =>
        {
            var args = new Dictionary<string, object?>
            {
                ["channel"] = edit.Channel,
                ["name"] = edit.Name
            };
            if (edit.Sv is not null) args["sv"] = edit.Sv;
            using var reply = ControlPipeClient.Cmd("clear", args);
            Note(reply, $"{edit.Name}: сброшено");
            RefreshVarsCommand.Execute().Subscribe();
            await Task.CompletedTask;
        });

        // Merged app (2026-08-27): the engine is IN-PROCESS and started by the
        // app BEFORE the page is constructed — pull endpoints/vars once after
        // the pipe is bound (W11.7`s «на фронте Running» initial refresh,
        // re-expressed without the process-liveness concept).
        Observable.Timer(TimeSpan.FromMilliseconds(400), RxApp.TaskpoolScheduler)
            .Select(_ => Unit.Default)
            .Take(1)
            .Subscribe(_ =>
            {
                RefreshEndpointsCommand.Execute().Subscribe();
                RefreshVarsCommand.Execute().Subscribe();
            })
            .DisposeWith(_cleanup);

        // W15 2026-08-28 (owner complaint «данные в iCem поступают, а в
        // эмуляторе они не изменяются»): the vars tables were populated ONCE
        // (initial refresh + the manual button) — the operator watched FROZEN
        // values while the bus streamed live data (verified: engine values DO
        // move — vars diff over 3 s changes every row). Refresh the tables on
        // a 2 s cadence through the SAME RefreshVarsCommand the button uses —
        // one code path, selection-preserving (W14), in-proc cheap.
        ticks.Where(i => i % 2 == 1)
            .Subscribe(_ => RefreshVarsCommand.Execute().Subscribe())
            .DisposeWith(_cleanup);
    }

    /// <summary>Render the reply into the one-line command note (⚠ on error/dead engine).</summary>
    private void Note(ControlPipeClient.Reply? reply, string what)
    {
        UiTrace.Log($"Note('{what}') reply={(reply is null ? "NULL" : $"ok={reply.Ok} err={reply.Error}")}");
        if (reply is null)
        {
            _commandNoteSubject.OnNext("⚠ движок не отвечает — " + what);
            return;
        }
        if (reply.Ok)
            _commandNoteSubject.OnNext("✓ " + what);
        else
            _commandNoteSubject.OnNext("⚠ " + (reply.Error ?? "ошибка") + " — " + what);
    }

    /// <summary>
    /// P2 2026-08-26: stream-toggle feedback with ACTIONABLE refusals. The
    /// engine starts passive (no port/adapter), so `start` fails until the
    /// operator picks an endpoint — the failure text now says exactly what to
    /// do instead of the silent snap-back that read as «кнопки не работают».
    /// P3 2026-08-27: targetOn = the state the OPERATOR requested (the
    /// switch's own IsOn), not the pre-toggle snapshot.
    /// P4 2026-08-27: every refusal string the engine actually emits
    /// (iCemEmule.Core, wire PINNED) maps to a concrete operator action.
    /// Unknown errors keep the generic «⚠ {err} — поток X» contract.
    /// </summary>
    private void NoteChannelToggle(ControlPipeClient.Reply? reply, string channel, bool targetOn)
    {
        string ch = channel.ToUpperInvariant();
        string action = targetOn ? "включён" : "выключен";
        UiTrace.Log($"NoteChannelToggle('{channel}', targetOn={targetOn}) reply={(reply is null ? "NULL" : $"ok={reply.Ok} err={reply.Error}")}");
        if (reply is null)
        {
            _commandNoteSubject.OnNext("⚠ движок не отвечает — поток " + ch);
            return;
        }
        if (reply.Ok)
        {
            _commandNoteSubject.OnNext("✓ поток " + ch + " " + action);
            return;
        }
        var err = reply.Error ?? "ошибка";
        if (err.Contains("no COM port configured", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Port name must not be empty", StringComparison.OrdinalIgnoreCase))
            _commandNoteSubject.OnNext("⚠ нет COM-порта — выберите порт в списке (Обновить порты)");
        else if (err.Contains("no LAN adapter configured", StringComparison.OrdinalIgnoreCase) ||
                 err.Contains("Adapter name must not be empty", StringComparison.OrdinalIgnoreCase))
            _commandNoteSubject.OnNext("⚠ нет LAN-адаптера — выберите адаптер (Обновить адаптеры)");
        else if (err.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
            _commandNoteSubject.OnNext("⚠ порт занят другим приложением — выберите другой COM-порт");
        else if (err.Contains("Invalid port name", StringComparison.OrdinalIgnoreCase))
            _commandNoteSubject.OnNext("⚠ неверное имя порта — выберите порт из списка (Обновить порты)");
        else if (err.Contains("Adapter not found", StringComparison.OrdinalIgnoreCase))
            _commandNoteSubject.OnNext("⚠ адаптер не найден — выберите адаптер из списка (Обновить адаптеры)");
        else if (err.Contains("Npcap not found", StringComparison.OrdinalIgnoreCase))
            _commandNoteSubject.OnNext("⚠ Npcap не установлен — установите Npcap (WinPcap API-совместимый режим)");
        else
            _commandNoteSubject.OnNext("⚠ " + err + " — поток " + ch);
    }

    /// <summary>
    /// P4 2026-08-27 (lying-switch kill): the engine's start/stop reply carries
    /// the RESULTING stream state (data.streaming) — the engine contract says
    /// the UI renders the switch from the AUTHORITATIVE reply, not from a
    /// possibly stale snapshot. Success ⇒ push the reply's streaming value
    /// (instant ON after start, instant OFF after stop). A refused start ⇒ the
    /// engine is definitively NOT streaming: push false so the switch snaps
    /// back immediately while the refusal note stays on screen. Null reply =
    /// engine unreachable — push nothing; the next honest poll renders truth.
    /// </summary>
    private void PushToggleTruth(ControlPipeClient.Reply? reply, string channel, bool targetOn)
    {
        if (reply is null)
            return;
        if (reply.Ok &&
            reply.Raw.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("streaming", out var st) &&
            st.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _toggleTruthSubject.OnNext((channel, st.GetBoolean()));
            return;
        }
        if (!reply.Ok && targetOn)
            _toggleTruthSubject.OnNext((channel, false));
    }

    private static bool TryParseEquipment(string text, out uint eq)
    {
        eq = 0;
        var t = (text ?? "").Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out eq);
        return uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out eq);
    }

    private static string FmtNum(JsonElement el)
    {
        // W14: табличный формат — ≥1000 → F1, иначе F4-trimmed (как старый
        // flecsEmu: F4.TrimEnd); G6 давал рваную точность («7.26085»).
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
            return Math.Abs(d) >= 1000
                ? d.ToString("F1", CultureInfo.InvariantCulture)
                : d.ToString("F4", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return el.ToString();
    }

    private static readonly string[] PresetNames = { "FirmwareDefault", "LanNative", "Metric", "Imperial" };

    private static EmuSnapshot FetchStatus()
    {
        // W22: 1 Hz poll — an undisposed JsonDocument here leaks pooled buffers
        // continuously; dispose every reply.
        using var reply = ControlPipeClient.Cmd("status");
        if (reply is null || !reply.Ok)
        {
            UiTrace.Log($"FetchStatus FAILED (reply={(reply is null ? "NULL" : "ok=false")})");
            return EmuSnapshot.Empty;
        }

        var root = reply.Raw.RootElement;
        if (!root.TryGetProperty("data", out var d))
            return EmuSnapshot.Empty;

        var com = Get(d, "com");
        var lan = Get(d, "lan");
        var units = Get(d, "units");

        var presetName = Str(units, "preset");
        int presetIndex = Array.FindIndex(PresetNames,
            n => string.Equals(n, presetName, StringComparison.OrdinalIgnoreCase));

        bool firmwareOn = lan.ValueKind == JsonValueKind.Object &&
            lan.TryGetProperty("firmwareSv", out var fw) && fw.GetBoolean();

        return new EmuSnapshot(
            Ok: true,
            Uptime: d.TryGetProperty("uptimeSec", out var up) ? up.ToString() + " с" : "—",
            Pid: d.TryGetProperty("pid", out var pid) ? pid.ToString() : "—",
            ComStreaming: com.TryGetProperty("streaming", out var cs) && cs.GetBoolean(),
            ComPort: Str(com, "port"),
            ComTemplate: Str(com, "template"),
            ComLines: $"{Num(com, "linesSent")} строк · {Num(com, "linesPerSec")}/с",
            LanStreaming: lan.TryGetProperty("streaming", out var ls) && ls.GetBoolean(),
            LanAdapter: Str(lan, "adapter"),
            LanEquipment: Str(lan, "equipment"),
            LanFrames: $"{Num(lan, "framesSent")} кадров · {Num(lan, "framesPerSec")}/с",
            LanSvCount: Str(lan, "svCount"),
            LanFirmware: firmwareOn ? "вкл" : "выкл",
            UnitsPreset: presetName,
            LanFirmwareOn: firmwareOn,
            UnitsPresetIndex: presetIndex,
            UnitFlowOv: Ov(units, "flowOverride"),
            UnitTemperatureOv: Ov(units, "temperatureOverride"),
            UnitDensityOv: Ov(units, "densityOverride"),
            UnitVolumeOv: Ov(units, "volumeOverride"),
            UnitLevelOv: Ov(units, "levelOverride"),
            UnitPressureOv: Ov(units, "pressureOverride"),
            // P2 2026-08-26 (owner decree «дефолт — всё выключено»): an ABSENT
            // emulating field now means OFF (was: defaulted ON). Old engines
            // that never send the field render switches OFF, never phantom-ON.
            ComEmulating: com.TryGetProperty("emulating", out var ce) && ce.GetBoolean(),
            LanEmulating: lan.TryGetProperty("emulating", out var le) && le.GetBoolean(),
            LanFramesSent: lan.ValueKind == JsonValueKind.Object &&
                lan.TryGetProperty("framesSent", out var fs) &&
                fs.ValueKind == JsonValueKind.Number &&
                fs.TryGetInt64(out long fl) ? fl : 0,
            // absent field (old engine) = assume present — never a phantom ⚠
            LanNpcapAvailable: !(lan.ValueKind == JsonValueKind.Object &&
                lan.TryGetProperty("npcapAvailable", out var np) &&
                np.ValueKind == JsonValueKind.False));
    }

    /// <summary>Unit override: null/absent/empty ⇒ "" (следует пресету).</summary>
    private static string Ov(JsonElement units, string name) =>
        units.ValueKind == JsonValueKind.Object &&
        units.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static JsonElement Get(JsonElement d, string name) =>
        d.TryGetProperty(name, out var el) ? el : default;

    private static string Str(JsonElement d, string name) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? "—"
            : "—";

    private static string Num(JsonElement d, string name) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.ToString()
            : "0";

    public void Dispose()
    {
        UiTrace.Log("EmulatorViewModel.Dispose");
        _cleanup.Dispose();
    }
}
