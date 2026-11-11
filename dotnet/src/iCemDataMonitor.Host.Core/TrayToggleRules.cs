using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Host.Core;

/// <summary>
/// Pure tray-toggle logic (no WinForms, no I/O). The state machine:
///   • Modbus / Sniffer — runtime start/stop (actuated by Program handlers)
///   • OPC DA — external stub: never actuated in-process
///   • Web — persist-only (Kestrel in-process cannot re-bind without a
///     restart; the flag applies on the next host start)
/// 2026-08-27: the Web layer RESTORED (corrected owner decree «Web - удали
/// (контрол sim)» = remove the sim control from the page, not the page).
/// The tray tooltip intentionally stays WITHOUT a web field — the host
/// tooltip already carries web:порт separately.
/// </summary>
public static class TrayToggleRules
{
    /// <summary>Layer state the Status window shows (mutable — its checkboxes flip it in place).</summary>
    public sealed class Layers
    {
        public bool Modbus { get; set; }
        public bool LanSniffer { get; set; }
        public bool OpcDa { get; set; }
        public bool Web { get; set; }

        public Layers(bool modbus, bool lanSniffer, bool opcDa, bool web)
        {
            Modbus = modbus;
            LanSniffer = lanSniffer;
            OpcDa = opcDa;
            Web = web;
        }

        public LayerFlags ToFlags() => new(Modbus, LanSniffer, OpcDa, Web);

        public static Layers From(LayerFlags f) => new(f.Modbus, f.LanSniffer, f.OpcDa, f.Web);
    }

    public enum LayerKind { Modbus, LanSniffer, OpcDa, Web }

    /// <summary>Whether the layer is toggled at runtime (true) or persists for the next start (false).</summary>
    public static bool IsRuntimeToggleable(LayerKind kind) => kind switch
    {
        LayerKind.Modbus or LayerKind.LanSniffer => true,
        LayerKind.OpcDa or LayerKind.Web => false,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Compute the ini update after a toggle (flags only; zones/ports untouched).</summary>
    public static IniOptions ApplyToggleToIni(IniOptions current, Layers layers) =>
        current with
        {
            ModbusOn = layers.Modbus ? 1 : 0,
            LansnifferOn = layers.LanSniffer ? 1 : 0,
            OpcdaOn = layers.OpcDa ? 1 : 0,
            WebOn = layers.Web ? 1 : 0,
        };

    /// <summary>Persist the "Set as default" checkbox state (ONLY this key changes).</summary>
    public static IniOptions ApplyUiSetAsDefaultToIni(IniOptions current, bool on) =>
        current with { UiSetAsDefault = on ? 1 : 0 };

    /// <summary>Human label per layer (W8: Russian — the Status window shows it to operators).</summary>
    public static string Label(LayerKind kind) => kind switch
    {
        LayerKind.Modbus => "Modbus TCP",
        LayerKind.LanSniffer => "Сниффер ACE LAN",
        LayerKind.OpcDa => "OPC DA (внешний — заглушка)",
        LayerKind.Web => "Веб-панель",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Tray tooltip string (NotifyIcon.Text limit is 63 chars). Deliberately
    /// WITHOUT the web field: the layer state window owns layer truth, and the
    /// host tooltip already shows web:порт separately.
    /// </summary>
    public static string BuildTooltip(Layers s) =>
        $"iCEM — Modbus:{(s.Modbus ? "вкл" : "выкл")} " +
        $"Сниффер:{(s.LanSniffer ? "вкл" : "выкл")} OPC:{(s.OpcDa ? "внешн" : "выкл")}";
}
