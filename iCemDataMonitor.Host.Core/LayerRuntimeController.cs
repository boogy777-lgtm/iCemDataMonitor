using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Host.Core;

/// <summary>
/// Single shared layer-toggle engine (W7/W8): the Status window is the ONLY
/// toggle view over this controller (the tray menu is a launcher — panel /
/// web / console / restart / close). Threading contract: Toggle/
/// SetAsDefaultChanged may run on a worker thread (Program wraps them in
/// Task.Run); state reads (Layers/Busy/LastError) are UI-safe because they are
/// only mutated inside the guarded sections and flushed via StateChanged, which
/// Program marshals onto the UI thread. The controller itself is WinForms-free.
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
        void StartModbus();

        /// <summary>Release :502 (bounded wait inside; throws on timeout).</summary>
        void StopModbus();

        /// <summary>Resume sniffer frame processing.</summary>
        void StartSniffer();

        /// <summary>Pause sniffer frame processing.</summary>
        void StopSniffer();
    }

    public TrayToggleRules.Layers Layers { get; }
    public string IniPath { get; }
    public event Action? StateChanged;

    /// <summary>Persisted projection (last loaded/saved options). Ports/zones are startup-static.</summary>
    public IniOptions Ini => _ini;

    /// <summary>False when the sniffer service could not be registered (registry missing) — its checkbox stays disabled.</summary>
    public bool SnifferAvailable { get; }

    private readonly ILayerActuator _actuator;
    private IniOptions _ini;

    private bool _modbusRunning;
    private bool _lanSnifferRunning;
    private bool _busy;
    private string? _lastError;

    /// <summary>Runtime state: Modbus bound right now (toggle/ini view — use
    /// ActualModbusRunning for UI truth).</summary>
    public bool ModbusRunning => _modbusRunning;

    /// <summary>Optional live probe (W10.2): when set, reports the REAL socket
    /// bind state (a startup bind failure ⇒ false even though the ini flag is 1).</summary>
    public Func<bool>? ModbusBoundProbe { get; set; }

    /// <summary>TRUTH signal for UI — Status window facts and web header lamps
    /// must agree with each other AND with reality.</summary>
    public bool ActualModbusRunning => ModbusBoundProbe?.Invoke() ?? _modbusRunning;

    /// <summary>Runtime state: sniffer processing frames right now (toggle/ini
    /// view — use ActualLanSnifferRunning for UI truth).</summary>
    public bool LanSnifferRunning => _lanSnifferRunning;

    /// <summary>Optional live probe (W10.2): reports the sniffer service state
    /// machine (ADAPTER_MISSING/STOPPED ⇒ false), not the ini flag.</summary>
    public Func<bool>? SnifferRunningProbe { get; set; }

    /// <summary>TRUTH signal for UI (see ActualModbusRunning).</summary>
    public bool ActualLanSnifferRunning => SnifferRunningProbe?.Invoke() ?? _lanSnifferRunning;

    /// <summary>A toggle/persist operation is in flight (menu/window disable their controls).</summary>
    public bool Busy => _busy;

    /// <summary>Last failed operation message (null = no failure).</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// "Set as default" checkbox. ON: every layer toggle persists to the active
    /// config file (canonical config.ini, 2026-08-28 rename law; the path is
    /// IniConfig.ActivePath — the file Load() actually used). OFF: runtime
    /// toggles are session-only — ini untouched. The checkbox state ITSELF is
    /// always persisted.
    /// </summary>
    public bool SetAsDefault => _ini.UiSetAsDefault == 1;

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
    public LayerRuntimeStatus CaptureStatus() => new(
        ActualModbusRunning, ActualLanSnifferRunning, SnifferAvailable,
        Layers.Modbus, Layers.LanSniffer, Layers.Web, Layers.OpcDa,
        _ini, Busy, LastError);

    /// <summary>
    /// Toggle one layer. Returns a human result. Never throws — failures land in
    /// LastError and the state rolls back (Layers unchanged on failure).
    /// </summary>
    public string Toggle(TrayToggleRules.LayerKind kind)
    {
        if (_busy)
            return "занято — дождитесь завершения предыдущего переключения";

        _busy = true;
        _lastError = null;
        try
        {
            return kind switch
            {
                TrayToggleRules.LayerKind.Modbus => ToggleModbus(),
                TrayToggleRules.LayerKind.LanSniffer => ToggleSniffer(),
                TrayToggleRules.LayerKind.OpcDa => "OPC DA — внешний слой, управление не из процесса",
                TrayToggleRules.LayerKind.Web => ToggleWeb(),
                _ => "неизвестная служба",
            };
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return ex.Message; // caller decides whether to show a toast
        }
        finally
        {
            _busy = false;
            StateChanged?.Invoke();
        }
    }

    private string ToggleModbus()
    {
        var desired = !_modbusRunning;
        try
        {
            if (desired)
            {
                _actuator.StartModbus();
                _modbusRunning = true;
            }
            else
            {
                _actuator.StopModbus();
                _modbusRunning = false;
            }
        }
        catch (Exception ex)
        {
            // State rollback: an actuator failure must not leave a lying checkbox.
            TryRollbackModbus(desired);
            _lastError = ex.Message;
            return ex.Message;
        }

        Layers.Modbus = desired;
        PersistLayerFlagsIfDefault();
        return desired
            ? $"Modbus TCP запущен{(SetAsDefault ? " (сохранено по умолчанию)" : " (только на сеанс)")}"
            : "Modbus TCP остановлен — порт освобождён";
    }

    private void TryRollbackModbus(bool wasStarting)
    {
        try
        {
            if (wasStarting) _actuator.StopModbus();
            else _actuator.StartModbus();
        }
        catch { /* best effort — the reported failure stands */ }
    }

    private string ToggleSniffer()
    {
        var desired = !_lanSnifferRunning;
        try
        {
            if (desired) _actuator.StartSniffer();
            else _actuator.StopSniffer();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return ex.Message;
        }

        _lanSnifferRunning = desired;
        Layers.LanSniffer = desired;
        PersistLayerFlagsIfDefault();
        return desired ? "Сниффер ACE LAN запущен" : "Сниффер ACE LAN на паузе";
    }

    /// <summary>
    /// PERSIST-ONLY web arm (Э7 pin «web persist-only (no actuator)»): no
    /// ILayerActuator member exists for web — Kestrel in-process cannot re-bind
    /// without killing the host. The flip goes through the SAME persist path as
    /// the runtime layers (PersistLayerFlagsIfDefault — SetAsDefault-gated) and
    /// applies after the next host start. Busy-guard: Toggle() holds the gate.
    /// </summary>
    private string ToggleWeb()
    {
        var desired = !Layers.Web;
        Layers.Web = desired;
        PersistLayerFlagsIfDefault();
        return desired
            ? $"Веб-панель будет включена после перезапуска{(SetAsDefault ? "" : " (только на сеанс)")}"
            : "Веб-панель будет выключена после перезапуска";
    }

    /// <summary>Set-as-default checkbox flip. The checkbox state is ALWAYS persisted;
    /// turning it ON also persists the CURRENT layer states as the new default.</summary>
    public string SetSetAsDefault(bool on)
    {
        _busy = true;
        try
        {
            var updated = TrayToggleRules.ApplyUiSetAsDefaultToIni(_ini, on);
            if (on)
                updated = TrayToggleRules.ApplyToggleToIni(updated, Layers);
            IniConfig.Save(IniPath, updated);
            _ini = updated;
            return on
                ? "Текущие состояния служб сохранены как настройки по умолчанию"
                : "Сохранение при переключении выключено — переключения действуют только на этот сеанс";
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return ex.Message;
        }
        finally
        {
            _busy = false;
            StateChanged?.Invoke();
        }
    }

    private void PersistLayerFlagsIfDefault()
    {
        if (!SetAsDefault)
            return;
        var updated = TrayToggleRules.ApplyToggleToIni(_ini, Layers);
        IniConfig.Save(IniPath, updated);
        _ini = updated;
    }

    /// <summary>Re-read the persisted projection (used after external Save paths).</summary>
    public void RefreshIni() => _ini = IniConfig.Load(IniPath);

    public string Tooltip => TrayToggleRules.BuildTooltip(Layers);
}
