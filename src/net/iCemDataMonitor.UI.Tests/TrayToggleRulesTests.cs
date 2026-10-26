using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Sdk.Bus;
using iCemDataMonitor.Host.Core;

namespace iCemDataMonitor.UI.Tests;

/// <summary>
/// Pure tray-toggle state machine tests (W6):
/// runtime vs persist-only classification, ini update math, tooltip.
/// 2026-08-27 RESTORED (corrected decree «Web - удали (контрол sim)» = the
/// sim control died, the page survived): the Web layer is BACK — persist-only
/// (IsRuntimeToggleable=false), ini math covers web_on, label «Веб-панель».
/// The tooltip intentionally stays WITHOUT a web field (host tooltip shows
/// web:порт separately).
/// </summary>
public class TrayToggleRulesTests
{
    [Fact]
    public void IsRuntimeToggleable_ClassifiesLayers()
    {
        Assert.True(TrayToggleRules.IsRuntimeToggleable(TrayToggleRules.LayerKind.Modbus));
        Assert.True(TrayToggleRules.IsRuntimeToggleable(TrayToggleRules.LayerKind.LanSniffer));
        Assert.False(TrayToggleRules.IsRuntimeToggleable(TrayToggleRules.LayerKind.OpcDa));
        // Web: persist-only — Kestrel in-process cannot re-bind without a
        // restart (Э7 pin «web persist-only (no actuator)»).
        Assert.False(TrayToggleRules.IsRuntimeToggleable(TrayToggleRules.LayerKind.Web));
    }

    [Fact]
    public void ApplyToggleToIni_FlipsOnlyFlags_KeepsPortsAndZones()
    {
        var current = new IniOptions(
            ModbusOn: 0, LansnifferOn: 1,
            ModbusPort: 1502, WitsBase: 600, Total: 10001);

        var layers = new TrayToggleRules.Layers(modbus: true, lanSniffer: false, opcDa: true, web: true);
        var updated = TrayToggleRules.ApplyToggleToIni(current, layers);

        Assert.Equal(1, updated.ModbusOn);
        Assert.Equal(0, updated.LansnifferOn);
        Assert.Equal(1, updated.OpcdaOn);
        Assert.Equal(1, updated.WebOn);         // RESTORED: web flag rides the toggle math
        Assert.Equal(1502, updated.ModbusPort); // untouched
        Assert.Equal(600, updated.WitsBase);    // untouched (2026-08-31: dead com/lan/raw keys removed — v4-compat zone stands in)
        Assert.Equal(10001, updated.Total);     // untouched
    }

    [Fact]
    public void Layers_FromFlags_RoundTrips()
    {
        var flags = new LayerFlags(Modbus: true, LanSniffer: true, OpcDa: false, Web: true);
        var layers = TrayToggleRules.Layers.From(flags);
        Assert.True(layers.Modbus);
        Assert.True(layers.LanSniffer);
        Assert.False(layers.OpcDa);
        Assert.True(layers.Web);
        Assert.Equal(flags, layers.ToFlags());
    }

    [Fact]
    public void BuildTooltip_ReflectsStates()
    {
        var tip = TrayToggleRules.BuildTooltip(
            new TrayToggleRules.Layers(modbus: true, lanSniffer: true, opcDa: true, web: false));
        Assert.Contains("Modbus:вкл", tip);
        Assert.Contains("Сниффер:вкл", tip);
        Assert.Contains("OPC:внешн", tip);
        Assert.True(tip.Length <= 63); // NotifyIcon.Text limit
    }

    [Fact]
    public void BuildTooltip_NoWebField_HostTooltipCarriesWebPortSeparately()
    {
        // web:порт lives in TrayTooltipFormatter (host truth at 1 Hz); the
        // layer-plan tooltip stays web-free even when the plan is ON.
        var tip = TrayToggleRules.BuildTooltip(
            new TrayToggleRules.Layers(modbus: false, lanSniffer: false, opcDa: false, web: true));
        Assert.DoesNotContain("Веб", tip);
        Assert.DoesNotContain("web", tip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyUiSetAsDefaultToIni_ChangesOnlyThatKey()
    {
        var current = new IniOptions(ModbusOn: 0, ModbusPort: 1502);
        var updated = TrayToggleRules.ApplyUiSetAsDefaultToIni(current, on: false);

        Assert.Equal(0, updated.UiSetAsDefault);
        Assert.Equal(0, updated.ModbusOn);      // untouched
        Assert.Equal(1502, updated.ModbusPort); // untouched
    }

    [Fact]
    public void Label_IsStableForBothViews()
    {
        Assert.Equal("Modbus TCP", TrayToggleRules.Label(TrayToggleRules.LayerKind.Modbus));
        Assert.Equal("Сниффер ACE LAN", TrayToggleRules.Label(TrayToggleRules.LayerKind.LanSniffer));
        Assert.Equal("OPC DA (внешний — заглушка)", TrayToggleRules.Label(TrayToggleRules.LayerKind.OpcDa));
        // RESTORED 2026-08-27.
        Assert.Equal("Веб-панель", TrayToggleRules.Label(TrayToggleRules.LayerKind.Web));
    }
}
