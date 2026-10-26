using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Host.Core;

/// <summary>
/// Single shared layer-toggle engine. Mutations use one async gate; Busy is
/// observed status only. Short state locks never surround actuators or IO.
/// Human-facing result strings are Russian (W8) — they surface in the Status
/// window footer/toasts.
/// 2026-08-27: the Web arm is BACK — persist-only (Э7 pin «web persist-only
/// (no actuator)»): the toggle writes web_on through the SAME persist path as
/// the runtime layers (SetAsDefault-gated) and applies after the next host
/// start; Kestrel in-process cannot re-bind without killing the host.
/// </summary>
public sealed class LayerRuntimeController
{
    /// <summary>Actuator seam — production wires the real publisher/sniffer; tests use fakes.</summary>
    public interface ILayerActuator
    {
        /// <summary>Bind Modbus (retry allowed inside; throws on exhaustion).</summary>
        Task StartModbusAsync();

        /// <summary>Release :502 (bounded wait inside; throws on timeout).</summary>
        Task StopModbusAsync();

        /// <summary>Resume sniffer frame processing.</summary>
        Task StartSnifferAsync();

        /// <summary>Pause sniffer frame processing.</summary>
        Task StopSnifferAsync();
    }

    public TrayToggleRules.Layers Layers { get; }
    public string IniPath { get; }
    public event Action? StateChanged;

    /// <summary>Persisted projection (last loaded/saved options). Ports/zones are startup-static.</summary>
    public IniOptions Ini { get { lock (_state) return _ini; } }

    /// <summary>False when the sniffer service could not be registered (registry missing) — its checkbox stays disabled.</summary>
    public bool SnifferAvailable { get; }

    private readonly ILayerActuator _actuator;
    private IniOptions _ini;

    private bool _modbusRunning;
    private bool _lanSnifferRunning;
    private volatile bool _busy;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _state = new();
    private string? _lastError;

    /// <summary>Runtime state: Modbus bound right now (toggle/ini view — use
    /// ActualModbusRunning for UI truth).</summary>
    public bool ModbusRunning { get { lock (_state) return _modbusRunning; } }

    /// <summary>Optional live probe (W10.2): when set, reports the REAL socket
    /// bind state (a startup bind failure ⇒ false even though the ini flag is 1).</summary>
    public Func<bool>? ModbusBoundProbe { get; set; }

    /// <summary>TRUTH signal for UI — Status window facts and web header lamps
    /// must agree with each other AND with reality.</summary>
    public bool ActualModbusRunning => ModbusBoundProbe?.Invoke() ?? ModbusRunning;

    /// <summary>Runtime state: sniffer processing frames right now (toggle/ini
    /// view — use ActualLanSnifferRunning for UI truth).</summary>
    public bool LanSnifferRunning { get { lock (_state) return _lanSnifferRunning; } }

    /// <summary>Optional live probe (W10.2): reports the sniffer service state
    /// machine (ADAPTER_MISSING/STOPPED ⇒ false), not the ini flag.</summary>
    public Func<bool>? SnifferRunningProbe { get; set; }

    /// <summary>TRUTH signal for UI (see ActualModbusRunning).</summary>
    public bool ActualLanSnifferRunning => SnifferRunningProbe?.Invoke() ?? LanSnifferRunning;

    /// <summary>A toggle/persist operation is in flight (menu/window disable their controls).</summary>
    public bool Busy => _busy;

    /// <summary>Last failed operation message (null = no failure).</summary>
    public string? LastError { get { lock (_state) return _lastError; } }

    /// <summary>
    /// "Set as default" checkbox. ON: every layer toggle persists to the active
    /// config file (canonical config.ini, 2026-08-28 rename law; the path is
    /// IniConfig.ActivePath — the file Load() actually used). OFF: runtime
    /// toggles are session-only — ini untouched. The checkbox state ITSELF is
    /// always persisted.
    /// </summary>
    public bool SetAsDefault => Ini.UiSetAsDefault == 1;

    public LayerRuntimeController(
        IniOptions ini,
        TrayToggleRules.Layers startupLayers,
        bool snifferAvailable,
        string iniPath,
        ILayerActuator actuator)
    {
        _ini = ini;
        Layers = new TrayToggleRules.Layers(
            startupLayers.Modbus, startupLayers.LanSniffer, startupLayers.OpcDa, startupLayers.Web);
        _modbusRunning = startupLayers.Modbus;
        _lanSnifferRunning = startupLayers.LanSniffer;
        SnifferAvailable = snifferAvailable;
        IniPath = iniPath;
        _actuator = actuator;
    }

    /// <summary>One observed sample, with requested and last persisted plans kept distinct.</summary>
    public LayerRuntimeStatus CaptureStatus()
    {
        // Probes are external calls: never hold the state lock around them.
        var modbus = ActualModbusRunning;
        var sniffer = ActualLanSnifferRunning;
        lock (_state)
            return new(modbus, sniffer, SnifferAvailable, Layers.Modbus,
                Layers.LanSniffer, Layers.Web, Layers.OpcDa, _ini, _busy, _lastError);
    }

    public string Toggle(TrayToggleRules.LayerKind kind) => ToggleAsync(kind).GetAwaiter().GetResult();

    public Task<string> ToggleAsync(TrayToggleRules.LayerKind kind, CancellationToken cancellationToken = default) =>
        RunCommandAsync(async () => kind switch
        {
            TrayToggleRules.LayerKind.Modbus => await ToggleModbusAsync().ConfigureAwait(false),
            TrayToggleRules.LayerKind.LanSniffer => await ToggleSnifferAsync().ConfigureAwait(false),
            TrayToggleRules.LayerKind.OpcDa => "OPC DA — внешний слой, управление не из процесса",
            TrayToggleRules.LayerKind.Web => ToggleWeb(),
            _ => "неизвестная служба",
        }, cancellationToken);

    private async Task<string> RunCommandAsync(Func<Task<string>> command, CancellationToken cancellationToken, bool propagateFailure = false)
    {
        // Cancellation only governs admission. Once admitted, finish actuator/persistence.
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_state) { _busy = true; _lastError = null; }
        try { return await command().ConfigureAwait(false); }
        catch (Exception ex)
        {
            lock (_state) _lastError = ex.Message;
            if (propagateFailure) throw;
            return ex.Message;
        }
        finally
        {
            lock (_state) _busy = false;
            _commands.Release();
            StateChanged?.Invoke(); // callbacks may read or synchronously submit another command
        }
    }

    private async Task<string> ToggleModbusAsync()
    {
        var desired = !_modbusRunning;
        try
        {
            if (desired) await _actuator.StartModbusAsync().ConfigureAwait(false);
            else await _actuator.StopModbusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // State rollback: an actuator failure must not leave a lying checkbox.
            await TryRollbackModbusAsync(desired).ConfigureAwait(false);
            lock (_state) _lastError = ex.Message;
            return ex.Message;
        }

        lock (_state) { _modbusRunning = desired; Layers.Modbus = desired; }
        PersistLayerFlagsIfDefault();
        return desired
            ? $"Modbus TCP запущен{(SetAsDefault ? " (сохранено по умолчанию)" : " (только на сеанс)")}"
            : "Modbus TCP остановлен — порт освобождён";
    }

    private async Task TryRollbackModbusAsync(bool wasStarting)
    {
        try
        {
            if (wasStarting) await _actuator.StopModbusAsync().ConfigureAwait(false);
            else await _actuator.StartModbusAsync().ConfigureAwait(false);
        }
        catch { /* best effort — the reported failure stands */ }
    }

    private async Task<string> ToggleSnifferAsync()
    {
        var desired = !_lanSnifferRunning;
        try
        {
            if (desired) await _actuator.StartSnifferAsync().ConfigureAwait(false);
            else await _actuator.StopSnifferAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_state) _lastError = ex.Message;
            return ex.Message;
        }

        lock (_state) { _lanSnifferRunning = desired; Layers.LanSniffer = desired; }
        PersistLayerFlagsIfDefault();
        return desired ? "Сниффер ACE LAN запущен" : "Сниффер ACE LAN на паузе";
    }

    /// <summary>
    /// PERSIST-ONLY web arm (Э7 pin «web persist-only (no actuator)»): no
    /// ILayerActuator member exists for web — Kestrel in-process cannot re-bind
    /// without killing the host. The flip goes through the SAME persist path as
    /// the runtime layers (PersistLayerFlagsIfDefault — SetAsDefault-gated) and
    /// applies after the next host start. The command gate owns the entire mutation.
    /// </summary>
    private string ToggleWeb()
    {
        var desired = !Layers.Web;
        lock (_state) Layers.Web = desired;
        PersistLayerFlagsIfDefault();
        return desired
            ? $"Веб-панель будет включена после перезапуска{(SetAsDefault ? "" : " (только на сеанс)")}"
            : "Веб-панель будет выключена после перезапуска";
    }

    /// <summary>Set-as-default checkbox flip. The checkbox state is ALWAYS persisted;
    /// turning it ON also persists the CURRENT layer states as the new default.</summary>
    public string SetSetAsDefault(bool on) => SetSetAsDefaultAsync(on).GetAwaiter().GetResult();

    public Task<string> SetSetAsDefaultAsync(bool on, CancellationToken cancellationToken = default) =>
        RunCommandAsync(() =>
        {
            var updated = TrayToggleRules.ApplyUiSetAsDefaultToIni(_ini, on);
            if (on) updated = TrayToggleRules.ApplyToggleToIni(updated, Layers);
            IniConfig.Save(IniPath, updated);
            lock (_state) _ini = updated;
            return Task.FromResult(on
                ? "Текущие состояния служб сохранены как настройки по умолчанию"
                : "Сохранение при переключении выключено — переключения действуют только на этот сеанс");
        }, cancellationToken);

    private void PersistLayerFlagsIfDefault()
    {
        if (!SetAsDefault)
            return;
        var updated = TrayToggleRules.ApplyToggleToIni(_ini, Layers);
        IniConfig.Save(IniPath, updated);
        lock (_state) _ini = updated;
    }

    /// <summary>Re-read the persisted projection (used after external Save paths).</summary>
    public void RefreshIni() => RefreshIniAsync().GetAwaiter().GetResult();

    public Task<string> RefreshIniAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync(() =>
        {
            var loaded = IniConfig.Load(IniPath);
            lock (_state) _ini = loaded;
            return Task.FromResult(string.Empty);
        }, cancellationToken, propagateFailure: true);

    public string Tooltip => TrayToggleRules.BuildTooltip(Layers);
}
