// EmuEngineHost (W11.4, hosted since 2026-08-27) — the emulator ENGINE: the
// same 1 Hz tick loop the WinForms TrayApp ran (COM serial stream + LAN ACE
// superframe + DFB network + unit conversion), minus every UI surface. State
// that lived in tabs now lives here (enable flags, unit settings, equipment,
// firmware tables). The operator command channel (named pipe) is served by a
// thread-safe headless adapter.
//
// Hosting (2026-08-27 owner decree «эмулятор = отдельное независимое
// приложение со встроенным UI + CLI»): the engine runs IN-PROCESS inside
// iCemEmulator.exe — the app calls Start() (non-blocking) at launch and
// Stop() on window close; the built-in UI (EmulatorPage) and the external
// `iCemEmulator.exe --cli` talk to it over the SAME control pipe
// (\\.\pipe\iCemEmule.Control). No child spawn, no host RPC.

using System.Globalization;
using System.Text;
using Halliburton.iCemEmule.Control;
using Halliburton.iCemEmule.Dfb;
using Halliburton.iCemEmule.Emitters;

namespace Halliburton.iCemEmule.Core;

public sealed class EmuEngineHost : IEmuControl
{
    private readonly object _gate = new();
    private readonly EmuSettings _settings;
    private readonly ManualResetEventSlim _quitEvent = new(false);

    // engines / emitters / providers (same wiring as TrayApp)
    private readonly SimulationEngine _comEngine = new();
    private readonly IComEmitter _comEmitter;
    private readonly AcePayloadProvider _baseProvider = new();
    private readonly DfbNetwork? _dfbNetwork;
    private readonly FrozenPayloadProvider _frozenProvider;
    private readonly ManualOverridePayloadProvider _overrideProvider;
    private readonly UnitConvertingPayloadProvider _lanUnitProvider;
    private readonly IAceLanEmitter _lanEmitter;
    // 2026-08-29 (Npcap honesty): adapter listing behind a ctor seam so the
    // «adapters command, Npcap missing ⇒ empty, no crash» path is provable
    // in tests; production default = the real SharpPcap probe.
    private readonly Func<IReadOnlyList<string>> _adapterLister;

    // state that used to live in the tabs
    private bool _comEnabled;
    private bool _lanEnabled;
    private bool _comEmulating;   // W14: OFF = freeze waveform, manual writes only
    private bool _lanEmulating;
    private UnitSettings _unitSettings = new();
    private double _lanElapsed;
    private bool _wasLanActive;
    private bool _lanSending;

    // control plane
    private readonly ControlPipeServer _ctlServer;
    private readonly LantableRegistry _lantable = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private DateTime _prevSampleUtc = DateTime.UtcNow;
    private long _prevComLines;
    private long _prevLanFrames;

    private Timer? _timer;

    // P2 2026-08-26: constructing the host starts NOTHING: no timer, no
    // pipe bind, no ports (those happen in Start()/RunInternal). PUBLIC since
    // the merged app (iCemEmulator) hosts the engine in-process.
    public EmuEngineHost(EmuSettings settings)
        : this(settings, new ComSerialEmitter(), new AceLanEmitter())
    {
    }

    /// <summary>P4 2026-08-27 (owner decree «нужно иметь возможность
    /// включать\выключать поток данных»): emitter-injection seam for the
    /// tests (InternalsVisibleTo iCemEmule.Tests) — runtime start/stop
    /// streaming is provable with FAKE emitters, no hardware. Production
    /// wiring = the single-arg ctor above (real COM/LAN emitters), unchanged.
    /// <paramref name="controlPipeName"/> defaults to the canonical operator
    /// channel; tests pass a unique name so the live engine on the stand is
    /// never touched.</summary>
    internal EmuEngineHost(EmuSettings settings, IComEmitter comEmitter, IAceLanEmitter lanEmitter,
        string? controlPipeName = null, Func<IReadOnlyList<string>>? adapterLister = null)
    {
        _settings = settings;
        _comEmitter = comEmitter;
        _lanEmitter = lanEmitter;
        _adapterLister = adapterLister ?? AceLanEmitter.ListAdapters;

        // LAN provider chain: base → DFB decorate → FREEZE gate → manual overrides → units
        _dfbNetwork = DfbEngineBridge.TryCreateNetwork();
        var dfbProvider = _dfbNetwork is not null
            ? DfbEngineBridge.Decorate(_baseProvider, _dfbNetwork)
            : _baseProvider;
        _frozenProvider = new FrozenPayloadProvider(dfbProvider);
        _overrideProvider = new ManualOverridePayloadProvider(_frozenProvider);
        _lanUnitProvider = new UnitConvertingPayloadProvider(_overrideProvider, (UnitPreset)_settings.UnitPreset);

        // P2 2026-08-26 (owner decree «дефолт у эмулятора — всё выключено
        // lan\com»): CONSTRUCTION stays fully passive — both emulation gates
        // OFF, freeze ON, nothing bound. Explicit emule_config.xml enables
        // (2026-08-31: renamed/moved from the legacy settings.xml) are
        // applied at Start() (see ApplySettingsAtStartup) — the transport
        // live gate patches emule_config.xml pre-launch and relies on it
        // being honored at startup; unpatched defaults stay all-off.
        _comEmulating = false;
        _lanEmulating = false;
        _frozenProvider.Frozen = true;

        ApplyUnitNumber();   // W14: unit number = equipment уже на старте (не только по setequipment)
        ApplyUnitSettings();
        _baseProvider.IncludeFirmwareTables = _settings.LanFirmwareSvEnabled;

        _ctlServer = new ControlPipeServer(Execute, StatusTickJson,
            controlPipeName ?? ControlPipeServer.DefaultPipeName);
    }

    // ── entry ────────────────────────────────────────────────────────────────
    /// <summary>Console hosting path (blocking): load settings, start the
    /// engine, wait for the pipe 'quit' command. Used by dev tools; the
    /// merged iCemEmulator app hosts the engine in-proc via Start().</summary>
    public static int Run()
    {
        var settings = EmuSettings.Load();
        var host = new EmuEngineHost(settings);
        return host.RunInternal();
    }

    /// <summary>
    /// HOSTED in-proc start (merged app, 2026-08-27 owner decree «эмулятор =
    /// отдельное независимое приложение со встроенным UI + CLI»): binds the
    /// control pipe, applies emule_config.xml to the runtime gates
    /// (passive-start defaults: streams/emulation start ONLY when
    /// emule_config.xml explicitly enables them), then runs the 1 Hz tick.
    /// Non-blocking; the app stops the engine with Stop() when its window
    /// closes.
    /// </summary>
    public void Start()
    {
        ApplySettingsAtStartup();
        _ctlServer.Start();
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    /// <summary>Blocks until the 'quit' pipe command requests shutdown.</summary>
    public void WaitForQuit() => _quitEvent.Wait();

    /// <summary>Stop the engine: timer, control pipe, emitters, settings save.
    /// Idempotent — the app's window-close and the 'quit' path may both fire.</summary>
    public void Stop() => Shutdown();

    /// <summary>
    /// Passive-start law with explicit-enable honoring (2026-08-27):
    ///   · emulation gates follow emule_config.xml (defaults OFF);
    ///   · streams auto-start ONLY when emule_config.xml explicitly enables
    ///     them AND the endpoint is configured (the transport live gate
    ///     patches emule_config.xml pre-launch and expects it honored at
    ///     startup);
    ///   · an open failure stays silent here — the operator's next `start`
    ///     command reports the honest error.
    /// </summary>
    private void ApplySettingsAtStartup()
    {
        _comEmulating = _settings.ComEmuleEnabled;
        _lanEmulating = _settings.LanEmuleEnabled;
        _frozenProvider.Frozen = !_lanEmulating;
        _baseProvider.IncludeFirmwareTables = _settings.LanFirmwareSvEnabled;

        if (_settings.ComEnabled && !string.IsNullOrEmpty(_settings.ComPort))
        {
            if (string.IsNullOrEmpty(_comEmitter.Open(_settings.ComPort)))
            {
                _comEnabled = true;
                _comEngine.ResetClock();
            }
        }
        if (_settings.LanEnabled && !string.IsNullOrEmpty(_settings.LanAdapter))
        {
            if (string.IsNullOrEmpty(_lanEmitter.Open(_settings.LanAdapter)))
                _lanEnabled = true;
        }
    }

    private int RunInternal()
    {
        // crash diagnostics (headless — log to the operator Logs policy path;
        // console may not exist when the host spawns us with CreateNoWindow).
        // CrashLog = canonical TapLog contract: append + 25 MB cap rotation
        // (keep-tail-half), never-throw — previous crash evidence survives.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Log($"Unhandled: {e.ExceptionObject}");

        // graceful stop for a MANUAL console launch (Ctrl+C); harmless when
        // spawned windowless by the host
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _quitEvent.Set(); };

        Start();

        // block until quit requested (pipe 'shutdown' or process kill)
        _quitEvent.Wait();

        Shutdown();
        return 0;
    }

    private void Shutdown()
    {
        try { _ctlServer.Dispose(); } catch { }
        try { _timer?.Dispose(); } catch { }
        try { _comEmitter.Close(); } catch { }
        try { _lanEmitter.Close(); } catch { }
        try { _comEmitter.Dispose(); } catch { }
        try { _lanEmitter.Dispose(); } catch { }
        try { _settings.Save(); } catch { }
    }

    // ── settings → runtime state ─────────────────────────────────────────────
    private void ApplyUnitSettings()
    {
        _unitSettings = new UnitSettings
        {
            Preset = (UnitPreset)_settings.UnitPreset,
            FlowOverride = StrOverride(_settings.UnitFlowOverride),
            TemperatureOverride = StrOverride(_settings.UnitTemperatureOverride),
            DensityOverride = StrOverride(_settings.UnitDensityOverride),
            VolumeOverride = StrOverride(_settings.UnitVolumeOverride),
            LevelOverride = StrOverride(_settings.UnitLevelOverride),
            PressureOverride = StrOverride(_settings.UnitPressureOverride),
        };
        _lanUnitProvider.Settings = _unitSettings;
    }

    private static string? StrOverride(string s) => string.IsNullOrEmpty(s) ? null : s;

    // ── 1 Hz tick (port of TrayApp.OnTick) ───────────────────────────────────
    private void Tick()
    {
        lock (_gate)
        {
            _baseProvider.IncludeFirmwareTables = _settings.LanFirmwareSvEnabled;

            // ── COM stream ──
            if (_comEnabled && _comEmitter.IsOpen)
            {
                try
                {
                    // W14: emulation OFF = freeze — переотправляем последние
                    // значения + актуальные ручные override'ы (генератор не
                    // двигает провод; старый баг flecsEmu «эмулированные
                    // данные текут даже при выключенной эмуляции» закрыт).
                    var values = _comEmulating ? _comEngine.Tick() : FrozenComValues();
                    var template = _comEngine.Current;
                    if (template is not null && values.Length > 0)
                    {
                        for (int i = 0; i < values.Length && i < template.Variables.Length; i++)
                        {
                            var v = template.Variables[i];
                            if (UnitConversion.IsConvertible(v.UoM))
                                values[i] = UnitConversion.ToTarget(values[i], v.UoM, _unitSettings);
                        }
                        _comEmitter.SendLine(SignalWireCodec.FormatLine(values, template));
                    }
                }
                catch
                {
                    // stream error — next tick retries; status reflects IsOpen
                }
            }

            // ── LAN stream ──
            bool lanActive = _lanEnabled && _lanEmitter.IsOpen;
            if (lanActive)
            {
                if (!_wasLanActive)
                    _lanElapsed = 0.0;

                if (!_lanSending)
                {
                    _lanSending = true;
                    var equipment = _settings.LanEquipment;
                    var srcMac = "";
                    var elapsed = _lanElapsed;

                    if (_dfbNetwork is not null && _lanEmulating)
                        _dfbNetwork.Execute(DfbContext.Create(elapsed, (long)elapsed, DfbEngineBridge.Seed));

                    IReadOnlyList<AceVectorData> vectors;
                    try
                    {
                        vectors = _lanUnitProvider.GetSuperframe(equipment, elapsed);
                    }
                    catch
                    {
                        _lanSending = false;
                        _lanElapsed += 1.0;
                        _wasLanActive = lanActive;
                        return;
                    }

                    Task.Run(() =>
                    {
                        try
                        {
                            _lanEmitter.SendSuperframe(vectors, equipment, srcMac);
                        }
                        catch
                        {
                            // send error — frames continue next tick
                        }
                        finally
                        {
                            _lanSending = false;
                        }
                    });

                    _lanElapsed += 1.0;
                }
            }
            _wasLanActive = lanActive;
        }
    }

    // ── IEmuControl (headless, thread-safe via the gate) ─────────────────────
    public ControlReply Execute(string line)
    {
        lock (_gate)
        {
            return CommandProcessor.ProcessLine(line, this);
        }
    }

    public string StatusTickJson() => Execute("status").Json;

    public void RequestQuit() => _quitEvent.Set();

    public Dictionary<string, object?> GetStatus()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Max(0.001, (now - _prevSampleUtc).TotalSeconds);
        long comLines = _comEmitter.LinesSent;
        long lanFrames = _lanEmitter.FramesSent;
        double comRate = (comLines - _prevComLines) / dt;
        double lanRate = (lanFrames - _prevLanFrames) / dt;
        _prevSampleUtc = now;
        _prevComLines = comLines;
        _prevLanFrames = lanFrames;

        var template = _comEngine.Current;
        var comOverrides = _comEngine.SnapshotOverrides();
        var lanOverrides = _overrideProvider.SnapshotOverrides();

        var overrides = new List<object?>();
        foreach (var kv in comOverrides)
            overrides.Add(new Dictionary<string, object?> { ["channel"] = "com", ["name"] = kv.Key, ["value"] = kv.Value });
        foreach (var kv in lanOverrides)
            overrides.Add(new Dictionary<string, object?>
            {
                ["channel"] = "lan", ["sv"] = $"0x{kv.Key.Sv:X8}", ["name"] = kv.Key.Name, ["value"] = kv.Value
            });

        var comValues = new Dictionary<string, object?>();
        if (template is not null)
        {
            var last = _comEngine.LastValues;
            for (int i = 0; i < template.Variables.Length && i < last.Length; i++)
                if (!template.Variables[i].ExcludeFromTelemetry)
                    comValues[template.Variables[i].Name] = last[i];
        }

        return new Dictionary<string, object?>
        {
            ["uptimeSec"] = Math.Round((now - _startedUtc).TotalSeconds, 1),
            ["pid"] = Environment.ProcessId,
            ["version"] = GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ["selfTest"] = null,
            ["com"] = new Dictionary<string, object?>
            {
                ["streaming"] = _comEnabled && _comEmitter.IsOpen,
                ["emulating"] = _comEmulating,
                ["portOpen"] = _comEmitter.IsOpen,
                ["port"] = _comEmitter.PortName ?? (string.IsNullOrEmpty(_settings.ComPort) ? null : _settings.ComPort),
                ["baud"] = 9600,
                ["linesSent"] = comLines,
                ["linesPerSec"] = Math.Round(comRate, 2),
                ["deviceType"] = template?.DeviceType,
                ["template"] = template?.Name,
                ["variableCount"] = template?.Variables.Length ?? 0,
                ["values"] = comValues
            },
            ["lan"] = new Dictionary<string, object?>
            {
                ["streaming"] = _lanEnabled && _lanEmitter.IsOpen,
                ["emulating"] = _lanEmulating,
                ["adapterOpen"] = _lanEmitter.IsOpen,
                ["adapter"] = _lanEmitter.AdapterName ?? (string.IsNullOrEmpty(_settings.LanAdapter) ? null : _settings.LanAdapter),
                ["framesSent"] = lanFrames,
                ["framesPerSec"] = Math.Round(lanRate, 2),
                // 2026-08-29: the COM-tab/header LAN indicator needs the Npcap
                // truth from the 1 Hz status poll (no second probe loop).
                ["npcapAvailable"] = _lanEmitter.IsOpen || AceLanEmitter.IsNpcapAvailable(),
                ["equipment"] = $"0x{_settings.LanEquipment:X8}",
                ["svCount"] = _overrideProvider.ActiveSvNumbers.Count,
                ["firmwareSv"] = _baseProvider.IncludeFirmwareTables,
                ["svs"] = _overrideProvider.ActiveSvNumbers.Select(sv => $"0x{sv:X8}").ToList(),
                ["targets"] = new List<object?> { "03:00:01:00:00:20" }
            },
            ["units"] = new Dictionary<string, object?>
            {
                ["preset"] = _unitSettings.Preset.ToString(),
                ["flowOverride"] = _unitSettings.FlowOverride,
                ["temperatureOverride"] = _unitSettings.TemperatureOverride,
                ["densityOverride"] = _unitSettings.DensityOverride,
                ["volumeOverride"] = _unitSettings.VolumeOverride,
                ["levelOverride"] = _unitSettings.LevelOverride,
                ["pressureOverride"] = _unitSettings.PressureOverride
            },
            ["overrides"] = overrides
        };
    }

    // ── STABLE refusal strings (UI contract) ────────────────────────────────
    // P4 2026-08-27: the WinUI (EmulatorViewModel.NoteChannelToggle) maps
    // these EXACT texts to actionable notes («выберите порт…» / «выберите
    // адаптер…»). ADDITIVE-only: do NOT reword without a UI-zone sync.
    // Emitter-open failures are passthrough (vendor/OS text) and render as
    // the generic note in the UI.
    public const string ErrNoComPort = "no COM port configured (ComPort setting is empty)";
    public const string ErrNoLanAdapter = "no LAN adapter configured (LanAdapter setting is empty)";

    /// <summary>
    /// RUNTIME stream enable (owner decree 2026-08-27 «нужно иметь возможность
    /// включать\выключать поток данных»). MASTER-SWITCH SEMANTICS: `start` IS
    /// the runtime master for its channel — it raises the in-process gate
    /// (_comEnabled/_lanEnabled) AND persists the mirror flag
    /// (ComEnabled/LanEnabled in emule_config.xml). Launch stays PASSIVE by
    /// default (unpatched emule_config.xml ⇒ nothing streams); explicitly
    /// enabled
    /// settings are honored at startup (ApplySettingsAtStartup) — the
    /// persisted flags are a mirror of the runtime gate, not a gate
    /// themselves. `start` refuses ONLY when the channel is
    /// genuinely impossible right now: no port/adapter configured (stable
    /// strings above) or the emitter failed to open (passthrough error).
    /// Emulation (setemulate) stays an independent gate: `start` alone
    /// streams frozen/last values until the operator flips emulation ON.
    /// </summary>
    public ControlResult StartChannel(string channel)
    {
        if (channel.Equals("com", StringComparison.OrdinalIgnoreCase))
        {
            if (_comEnabled && _comEmitter.IsOpen)
                return ControlResult.Success(new Dictionary<string, object?>
                {
                    ["channel"] = "com", ["already"] = true,
                    ["port"] = _comEmitter.PortName,
                    // P1 2026-08-26: resulting state in EVERY start/stop reply —
                    // the UI renders the switch from the AUTHORITATIVE reply
                    // instead of guessing from a possibly stale snapshot.
                    ["streaming"] = true
                });
            var port = _settings.ComPort;
            if (string.IsNullOrEmpty(port))
                return ControlResult.Fail(ErrNoComPort);
            var err = _comEmitter.Open(port);
            if (!string.IsNullOrEmpty(err))
                return ControlResult.Fail(err);
            _comEngine.ResetClock();
            _comEnabled = true;
            _settings.ComEnabled = true;
            _settings.ComPort = port;
            return ControlResult.Success(new Dictionary<string, object?>
            {
                ["channel"] = "com", ["port"] = port, ["streaming"] = true
            });
        }

        if (_lanEnabled && _lanEmitter.IsOpen)
            return ControlResult.Success(new Dictionary<string, object?>
            {
                ["channel"] = "lan", ["already"] = true,
                ["adapter"] = _lanEmitter.AdapterName,
                ["streaming"] = true
            });
        var adapter = _settings.LanAdapter;
        if (string.IsNullOrEmpty(adapter))
            return ControlResult.Fail(ErrNoLanAdapter);
        var lerr = _lanEmitter.Open(adapter);
        if (!string.IsNullOrEmpty(lerr))
            return ControlResult.Fail(lerr);
        _lanEnabled = true;
        _settings.LanEnabled = true;
        _settings.LanAdapter = adapter;
        return ControlResult.Success(new Dictionary<string, object?>
        {
            ["channel"] = "lan", ["adapter"] = adapter, ["streaming"] = true
        });
    }

    /// <summary>RUNTIME stream disable — the mirror of StartChannel: lowers
    /// the in-process gate, closes the port/adapter and persists the OFF flag
    /// (emule_config.xml mirror). Idempotent; never refuses.</summary>
    public ControlResult StopChannel(string channel)
    {
        if (channel.Equals("com", StringComparison.OrdinalIgnoreCase))
        {
            bool was = _comEnabled || _comEmitter.IsOpen;
            _comEnabled = false;
            _settings.ComEnabled = false;
            _comEmitter.Close();
            return ControlResult.Success(new Dictionary<string, object?>
            {
                ["channel"] = "com", ["wasStreaming"] = was, ["streaming"] = false
            });
        }

        bool wasLan = _lanEnabled || _lanEmitter.IsOpen;
        _lanEnabled = false;
        _settings.LanEnabled = false;
        _lanEmitter.Close();
        return ControlResult.Success(new Dictionary<string, object?>
        {
            ["channel"] = "lan", ["wasStreaming"] = wasLan, ["streaming"] = false
        });
    }

    public ControlResult SetVariable(string? channel, string name, double value, uint? sv)
    {
        var applied = new List<object?>();
        bool wantCom = channel is null || channel.Equals("com", StringComparison.OrdinalIgnoreCase);
        bool wantLan = channel is null || channel.Equals("lan", StringComparison.OrdinalIgnoreCase);

        if (wantCom)
        {
            if (_comEngine.TryFindVariable(name, out var v, out _))
            {
                _comEngine.SetOverride(v!.Name, value);
                applied.Add(new Dictionary<string, object?> { ["channel"] = "com", ["name"] = v.Name, ["uom"] = v.UoM });
            }
            else if (channel is not null)
                return ControlResult.Fail($"COM variable '{name}' not in template '{_comEngine.Current?.Name}' (see 'vars com')");
        }

        if (wantLan)
        {
            var cands = FindLanSignals(name, sv);
            if (cands.Count == 1)
            {
                var (s, sig) = cands[0];
                if (!_overrideProvider.CanTransmit(s, sig.Name, out string reason))
                    return ControlResult.Fail($"lan signal '{sig.Name}': {reason}");
                _overrideProvider.SetOverride(s, sig.Name, value);
                applied.Add(new Dictionary<string, object?> { ["channel"] = "lan", ["sv"] = $"0x{s:X8}", ["name"] = sig.Name, ["uom"] = sig.UoM });
            }
            else if (cands.Count > 1)
                return ControlResult.Fail($"ambiguous LAN signal '{name}' in SVs: " +
                    string.Join(", ", cands.Select(c => $"0x{c.Sv:X8}")) +
                    "; disambiguate with JSON form {\"cmd\":\"set\",\"sv\":\"0x...\",\"name\":...}");
            else if (channel is not null)
                return ControlResult.Fail($"LAN signal '{name}' not in lantable (see 'vars lan')");
        }

        if (applied.Count == 0)
            return ControlResult.Fail($"variable '{name}' not found on com or lan");
        return ControlResult.Success(new Dictionary<string, object?>
        {
            ["applied"] = applied,
            ["value"] = value,
            ["units"] = "native",
            ["note"] = "LAN overrides are pre-unit-conversion (native); the wire value follows the Units preset"
        });
    }

    public ControlResult ClearVariable(string? channel, string name, uint? sv)
    {
        var cleared = new List<object?>();
        bool wantCom = channel is null || channel.Equals("com", StringComparison.OrdinalIgnoreCase);
        bool wantLan = channel is null || channel.Equals("lan", StringComparison.OrdinalIgnoreCase);
        bool known = false;

        if (wantCom && _comEngine.TryFindVariable(name, out var v, out _))
        {
            known = true;
            if (_comEngine.ClearOverride(v!.Name))
                cleared.Add("com");
        }
        if (wantLan)
        {
            var cands = FindLanSignals(name, sv);
            if (cands.Count > 1)
                return ControlResult.Fail($"ambiguous LAN signal '{name}' in SVs: " +
                    string.Join(", ", cands.Select(c => $"0x{c.Sv:X8}")) + "; pass \"sv\" in JSON form");
            if (cands.Count == 1)
            {
                known = true;
                _overrideProvider.ClearOverride(cands[0].Sv, cands[0].Sig.Name);
                cleared.Add("lan");
            }
        }

        if (!known)
            return ControlResult.Fail($"variable '{name}' not found on {(channel ?? "com or lan")}");
        return ControlResult.Success(new Dictionary<string, object?> { ["cleared"] = cleared });
    }

    public ControlResult GetVariable(string? channel, string name, uint? sv)
    {
        bool wantCom = channel is null || channel.Equals("com", StringComparison.OrdinalIgnoreCase);
        bool wantLan = channel is null || channel.Equals("lan", StringComparison.OrdinalIgnoreCase);
        TemplateVariable? cv = null;
        int cidx = -1;
        bool comFound = wantCom && _comEngine.TryFindVariable(name, out cv, out cidx);
        var lanCands = wantLan ? FindLanSignals(name, sv) : new List<(uint Sv, LantableRegistry.SignalDef Sig)>();

        if (channel is null && comFound && lanCands.Count > 0)
            return ControlResult.Fail($"variable '{name}' exists on both com and lan; prefix with com. or lan.");

        if (comFound)
        {
            var last = _comEngine.LastValues;
            var ov = _comEngine.SnapshotOverrides();
            double value = ov.TryGetValue(cv!.Name, out double o)
                ? o
                : (cidx >= 0 && cidx < last.Length ? last[cidx] : cv.BaseValue);
            return ControlResult.Success(new Dictionary<string, object?>
            {
                ["channel"] = "com", ["name"] = cv.Name, ["value"] = value,
                ["uom"] = cv.UoM, ["overridden"] = ov.ContainsKey(cv.Name),
                ["excludeFromTelemetry"] = cv.ExcludeFromTelemetry
            });
        }

        if (lanCands.Count == 1)
        {
            var (s, sig) = lanCands[0];
            var vectors = _overrideProvider.GetSuperframe(_settings.LanEquipment, _lanElapsed);
            var vec = vectors.FirstOrDefault(x => x.SvNumber == s);
            if (vec is null)
                return ControlResult.Fail($"SV 0x{s:X8} not in the current superframe (firmware SVs off?)");
            if (!SignalWireCodec.TryDecodeNative(vec.Payload, sig, out double value))
                return ControlResult.Fail($"signal '{sig.Name}' is not numeric-decodable (type {sig.ValueType})");
            bool overridden = _overrideProvider.SnapshotOverrides().ContainsKey((s, sig.Name));
            return ControlResult.Success(new Dictionary<string, object?>
            {
                ["channel"] = "lan", ["sv"] = $"0x{s:X8}", ["name"] = sig.Name,
                ["value"] = value, ["uom"] = sig.UoM, ["native"] = true, ["overridden"] = overridden
            });
        }

        if (lanCands.Count > 1)
            return ControlResult.Fail($"ambiguous LAN signal '{name}' in SVs: " +
                string.Join(", ", lanCands.Select(c => $"0x{c.Sv:X8}")) + "; pass \"sv\" in JSON form");

        return ControlResult.Fail($"variable '{name}' not found on {(channel ?? "com or lan")}");
    }

    public ControlResult GetProfiles()
    {
        var list = new List<object?>();
        foreach (var t in _comEngine.Templates)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["deviceType"] = t.DeviceType,
                ["name"] = t.Name,
                ["variableCount"] = t.Variables.Length,
                ["active"] = ReferenceEquals(t, _comEngine.Current)
            });
        }
        return ControlResult.Success(new Dictionary<string, object?> { ["profiles"] = list });
    }

    public ControlResult SetProfile(string? deviceType, string name)
    {
        var matches = _comEngine.Templates.Where(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (deviceType is null || string.Equals(t.DeviceType, deviceType, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count == 0)
            return ControlResult.Fail($"profile '{name}' not found (see 'profiles')");
        if (matches.Count > 1)
            return ControlResult.Fail($"ambiguous profile '{name}'; pass \"deviceType\" in JSON form");
        var t = matches[0];
        _comEngine.SetTemplate(t.DeviceType, t.Name);
        return ControlResult.Success(new Dictionary<string, object?>
        {
            ["deviceType"] = t.DeviceType, ["name"] = t.Name,
            ["note"] = "template swap is on-the-fly; COM stream not interrupted"
        });
    }

    public ControlResult GetVariables(string? channel)
    {
        var data = new Dictionary<string, object?>();
        var comOv = _comEngine.SnapshotOverrides();
        var lanOv = _overrideProvider.SnapshotOverrides();

        if (channel is null || channel.Equals("com", StringComparison.OrdinalIgnoreCase))
        {
            var last = _comEngine.LastValues;
            var list = new List<object?>();
            var t = _comEngine.Current;
            if (t is not null)
            {
                for (int i = 0; i < t.Variables.Length; i++)
                {
                    var v = t.Variables[i];
                    list.Add(new Dictionary<string, object?>
                    {
                        ["name"] = v.Name, ["uom"] = v.UoM,
                        ["value"] = i < last.Length ? last[i] : v.BaseValue,
                        ["overridden"] = comOv.ContainsKey(v.Name),
                        ["excludeFromTelemetry"] = v.ExcludeFromTelemetry
                    });
                }
            }
            data["com"] = new Dictionary<string, object?> { ["template"] = t?.Name, ["variables"] = list };
        }

        if (channel is null || channel.Equals("lan", StringComparison.OrdinalIgnoreCase))
        {
            var list = new List<object?>();
            // W14: live Value per signal (таблица LAN-вкладки) — один superframe,
            // декод каждого сигнала по offset-закону (как GetVariable)
            var vectors = _overrideProvider.GetSuperframe(_settings.LanEquipment, _lanElapsed);
            foreach (uint sv in _overrideProvider.ActiveSvNumbers)
            {
                var svDef = _lantable.GetBySvNumber(sv);
                if (svDef is null) continue;
                var vec = vectors.FirstOrDefault(x => x.SvNumber == sv);
                foreach (var sig in svDef.Signals)
                {
                    double? liveValue = null;
                    if (vec is not null && SignalWireCodec.TryDecodeNative(vec.Payload, sig, out double dv))
                        liveValue = dv;
                    list.Add(new Dictionary<string, object?>
                    {
                        ["sv"] = $"0x{sv:X8}", ["name"] = sig.Name, ["uom"] = sig.UoM,
                        ["value"] = liveValue,
                        ["valueType"] = sig.ValueType, ["byteOffset"] = sig.ByteOffset, ["size"] = sig.Size,
                        ["transmittable"] = _overrideProvider.CanTransmit(sv, sig.Name, out _),
                        ["overridden"] = lanOv.ContainsKey((sv, sig.Name))
                    });
                }
            }
            data["lan"] = new Dictionary<string, object?> { ["variables"] = list };
        }

        return ControlResult.Success(data);
    }

    // ── W11.7: full functional surface (embedded-UI parity) ───────────────────

    public ControlResult GetPorts()
    {
        var ports = ComSerialEmitter.ListPorts()
            .Select(p => new Dictionary<string, object?> { ["name"] = p, ["open"] = _comEmitter.IsOpen && _comEmitter.PortName == p })
            .ToList();
        return ControlResult.Success(new Dictionary<string, object?> { ["ports"] = ports });
    }

    public ControlResult GetAdapters()
    {
        var list = _adapterLister();
        var adapters = list
            .Select(a => new Dictionary<string, object?> { ["name"] = a, ["open"] = _lanEmitter.IsOpen && _lanEmitter.AdapterName == a })
            .ToList();
        // 2026-08-29 (Npcap/adapter honesty): an empty adapter list + missing
        // driver is reported explicitly so the UI can say «Npcap не
        // установлен» next to the combo instead of showing a dead empty list.
        bool npcapAvailable = list.Count > 0 || _lanEmitter.IsOpen || AceLanEmitter.IsNpcapAvailable();
        return ControlResult.Success(new Dictionary<string, object?>
        {
            ["adapters"] = adapters,
            ["npcapAvailable"] = npcapAvailable
        });
    }

    public ControlResult SetPort(string port)
    {
        if (string.IsNullOrWhiteSpace(port))
            return ControlResult.Fail("empty port name");
        bool wasStreaming = _comEnabled;
        string? old = _settings.ComPort;
        if (_comEmitter.IsOpen)
            _comEmitter.Close();
        _settings.ComPort = port;
        _settings.ComEnabled = _comEnabled || wasStreaming;
        if (wasStreaming)
        {
            var err = _comEmitter.Open(port);
            if (!string.IsNullOrEmpty(err))
            {
                _comEmitter.Close();
                _settings.ComPort = old ?? "";
                return ControlResult.Fail(err);
            }
            _comEngine.ResetClock();
            _comEnabled = true;
        }
        _settings.Save();
        return ControlResult.Success(new Dictionary<string, object?> { ["port"] = port, ["streaming"] = _comEnabled });
    }

    public ControlResult SetAdapter(string adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter))
            return ControlResult.Fail("empty adapter name");
        bool wasStreaming = _lanEnabled;
        string? old = _settings.LanAdapter;
        if (_lanEmitter.IsOpen)
            _lanEmitter.Close();
        _settings.LanAdapter = adapter;
        _settings.LanEnabled = _lanEnabled || wasStreaming;
        if (wasStreaming)
        {
            var err = _lanEmitter.Open(adapter);
            if (!string.IsNullOrEmpty(err))
            {
                _lanEmitter.Close();
                _settings.LanAdapter = old ?? "";
                return ControlResult.Fail(err);
            }
            _lanEnabled = true;
        }
        _settings.Save();
        return ControlResult.Success(new Dictionary<string, object?> { ["adapter"] = adapter, ["streaming"] = _lanEnabled });
    }

    public ControlResult SetEquipment(uint equipment)
    {
        _settings.LanEquipment = equipment;
        ApplyUnitNumber();
        _settings.Save();
        return ControlResult.Success(new Dictionary<string, object?> { ["equipment"] = $"0x{equipment:X8}" });
    }

    /// <summary>W14 (owner law): iCem ACE — unit number на проводе = equipment number.</summary>
    private void ApplyUnitNumber()
    {
        var seq = _dfbNetwork?.Blocks.OfType<JobSequencerBlock>().FirstOrDefault();
        if (seq is not null)
            seq.UnitNumberValue = new DfbParameter<uint>(_settings.LanEquipment);
    }

    /// <summary>W14: emulation OFF = freeze. COM: last generated values are
    /// re-sent with the CURRENT manual overrides re-applied (new `set` writes
    /// reach the wire, the waveform does not move). LAN: FrozenPayloadProvider
    /// holds the last superframe; overrides/units decorators stay live.</summary>
    public ControlResult SetEmulation(string channel, bool on)
    {
        if (channel.Equals("com", StringComparison.OrdinalIgnoreCase))
        {
            _comEmulating = on;
            _settings.ComEmuleEnabled = on;
            _settings.Save();
            return ControlResult.Success(new Dictionary<string, object?> { ["channel"] = "com", ["emulating"] = on });
        }
        if (channel.Equals("lan", StringComparison.OrdinalIgnoreCase))
        {
            _lanEmulating = on;
            _frozenProvider.Frozen = !on;
            _settings.LanEmuleEnabled = on;
            _settings.Save();
            return ControlResult.Success(new Dictionary<string, object?> { ["channel"] = "lan", ["emulating"] = on });
        }
        return ControlResult.Fail($"unknown channel '{channel}' (com|lan)");
    }

    /// <summary>Frozen COM values: last tick snapshot + current manual overrides.</summary>
    private double[] FrozenComValues()
    {
        var template = _comEngine.Current;
        var last = _comEngine.LastValues;
        if (template is null || last.Length == 0)
            return _comEngine.Tick();   // seed once, then freeze
        var ov = _comEngine.SnapshotOverrides();
        for (int i = 0; i < last.Length && i < template.Variables.Length; i++)
            if (ov.TryGetValue(template.Variables[i].Name, out double o))
                last[i] = o;
        return last;
    }

    public ControlResult SetFirmware(bool on)
    {
        _settings.LanFirmwareSvEnabled = on;
        _settings.Save();
        return ControlResult.Success(new Dictionary<string, object?> { ["firmwareSv"] = on });
    }

    public ControlResult SetPreset(int presetIndex)
    {
        if (presetIndex is < 0 or > 3)
            return ControlResult.Fail($"preset index {presetIndex} out of range 0..3");
        _settings.UnitPreset = presetIndex;
        ApplyUnitSettings();
        _settings.Save();
        return ControlResult.Success(new Dictionary<string, object?> { ["preset"] = ((UnitPreset)presetIndex).ToString() });
    }

    public ControlResult SetUnit(string group, string? unit)
    {
        string g = group.Trim().ToLowerInvariant();
        bool clear = string.IsNullOrWhiteSpace(unit);
        switch (g)
        {
            case "flow": _settings.UnitFlowOverride = clear ? "" : unit!.Trim(); break;
            case "temperature": _settings.UnitTemperatureOverride = clear ? "" : unit!.Trim(); break;
            case "density": _settings.UnitDensityOverride = clear ? "" : unit!.Trim(); break;
            case "volume": _settings.UnitVolumeOverride = clear ? "" : unit!.Trim(); break;
            case "level": _settings.UnitLevelOverride = clear ? "" : unit!.Trim(); break;
            case "pressure": _settings.UnitPressureOverride = clear ? "" : unit!.Trim(); break;
            default:
                return ControlResult.Fail($"unknown unit group '{group}' (flow|temperature|density|volume|level|pressure)");
        }
        ApplyUnitSettings();
        _settings.Save();
        return ControlResult.Success(new Dictionary<string, object?> { ["group"] = g, ["unit"] = clear ? null : unit!.Trim() });
    }

    private List<(uint Sv, LantableRegistry.SignalDef Sig)> FindLanSignals(string name, uint? sv)
    {
        var res = new List<(uint, LantableRegistry.SignalDef)>();
        foreach (var kv in _lantable.All)
        {
            if (sv is not null && kv.Key != sv.Value) continue;
            foreach (var s in kv.Value.Signals)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    res.Add((kv.Key, s));
        }
        return res;
    }
}
