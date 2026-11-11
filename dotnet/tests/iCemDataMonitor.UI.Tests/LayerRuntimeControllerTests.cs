using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Sdk.Bus;
using iCemDataMonitor.Host.Core;

namespace iCemDataMonitor.UI.Tests;

/// <summary>
/// LayerRuntimeController pins (W7): the shared engine behind the Status window
/// (the ONLY toggle view — the tray menu is a launcher). Fake actuator — no
/// real sockets, no sniffer.
/// 2026-08-27 RESTORED (corrected decree): the Web layer is BACK — persist-only
/// (Э7 pin «web persist-only (no actuator)»): the toggle flips the plan, writes
/// web_on through the SAME persist path (SetAsDefault-gated) and applies after
/// the next host start; the actuator is NEVER touched.
/// </summary>
public class LayerRuntimeControllerTests : IDisposable
{
    private sealed class FakeActuator : LayerRuntimeController.ILayerActuator
    {
        public int StartModbusCalls;
        public int StopModbusCalls;
        public int StartSnifferCalls;
        public int StopSnifferCalls;
        public Exception? ModbusStartThrows;

        // W22: ModbusStopThrows/SnifferThrows removed — no test ever assigned
        // them (CS0649); the fake stays minimal, failure injection covers only
        // the start path the controller actually rolls back.

        public Task StartModbusAsync()
        {
            StartModbusCalls++;
            if (ModbusStartThrows is not null) throw ModbusStartThrows;
            return Task.CompletedTask;
        }

        public Task StopModbusAsync()
        {
            StopModbusCalls++;
            return Task.CompletedTask;
        }

        public Task StartSnifferAsync()
        {
            StartSnifferCalls++;
            return Task.CompletedTask;
        }

        public Task StopSnifferAsync()
        {
            StopSnifferCalls++;
            return Task.CompletedTask;
        }
    }

    private readonly string _dir;

    public LayerRuntimeControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lrctrl_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string IniPath => Path.Combine(_dir, IniConfig.FileName);

    private LayerRuntimeController NewController(
        IniOptions ini, TrayToggleRules.Layers layers, FakeActuator act,
        bool snifferAvailable = true)
    {
        // Seed the file with the exact passed options (Save = fail-safe writer).
        IniConfig.Save(IniPath, ini);
        return new LayerRuntimeController(ini, layers, snifferAvailable, IniPath, act);
    }

    [Fact]
    public void ToggleModbus_OnOff_CallsActuatorAndFlipsState()
    {
        var ini = new IniOptions(ModbusOn: 0);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(false, false, false, false), act);

        var on = c.Toggle(TrayToggleRules.LayerKind.Modbus);
        Assert.Equal(1, act.StartModbusCalls);
        Assert.True(c.ModbusRunning);
        Assert.True(c.Layers.Modbus);
        Assert.DoesNotContain("failed", on);

        var off = c.Toggle(TrayToggleRules.LayerKind.Modbus);
        Assert.Equal(1, act.StopModbusCalls);
        Assert.False(c.ModbusRunning);
        Assert.False(c.Layers.Modbus);
    }

    [Fact]
    public void ToggleModbus_StartFails_RollsBackAndReportsError()
    {
        var ini = new IniOptions(ModbusOn: 0);
        var act = new FakeActuator { ModbusStartThrows = new InvalidOperationException("port busy") };
        var c = NewController(ini, new TrayToggleRules.Layers(false, false, false, false), act);

        var msg = c.Toggle(TrayToggleRules.LayerKind.Modbus);

        Assert.False(c.ModbusRunning);            // state rolled back — no lying checkbox
        Assert.False(c.Layers.Modbus);
        Assert.Equal("port busy", c.LastError);
        Assert.Contains("port busy", msg);
        Assert.Equal(1, act.StopModbusCalls);     // rollback best-effort stop attempted
    }

    [Fact]
    public void ToggleModbus_SetAsDefault_PersistsToIni()
    {
        var ini = new IniOptions(ModbusOn: 0, UiSetAsDefault: 1);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(false, false, false, false), act);

        c.Toggle(TrayToggleRules.LayerKind.Modbus);

        var saved = IniConfig.Load(IniPath);
        Assert.Equal(1, saved.ModbusOn);          // persisted as the new startup default
    }

    [Fact]
    public void ToggleModbus_SetAsDefaultOff_SessionOnlyIniUntouched()
    {
        var ini = new IniOptions(ModbusOn: 0, UiSetAsDefault: 0);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(false, false, false, false), act);

        c.Toggle(TrayToggleRules.LayerKind.Modbus);

        Assert.True(c.ModbusRunning);             // runtime state changed...
        Assert.Equal(0, IniConfig.Load(IniPath).ModbusOn); // ...but ini untouched
    }

    [Fact]
    public void SetSetAsDefault_On_PersistsCheckboxAndCurrentStates()
    {
        var ini = new IniOptions(ModbusOn: 0, LansnifferOn: 0, UiSetAsDefault: 0);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(false, true, false, false), act);

        var msg = c.SetSetAsDefault(true);

        var saved = IniConfig.Load(IniPath);
        Assert.Equal(1, saved.UiSetAsDefault);    // checkbox state persisted
        Assert.Equal(1, saved.LansnifferOn);      // CURRENT runtime states became the default
        Assert.Equal(0, saved.ModbusOn);
        Assert.True(c.SetAsDefault);
        Assert.DoesNotContain("failed", msg);
    }

    [Fact]
    public void SetSetAsDefault_Off_PersistsCheckboxOnly_LeavesLayerFlags()
    {
        var ini = new IniOptions(ModbusOn: 0, LansnifferOn: 1, UiSetAsDefault: 1);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(true, false, false, false), act);

        c.SetSetAsDefault(false);

        var saved = IniConfig.Load(IniPath);
        Assert.Equal(0, saved.UiSetAsDefault);
        Assert.Equal(0, saved.ModbusOn);          // ini layer flags NOT overwritten by runtime state
        Assert.Equal(1, saved.LansnifferOn);
    }

    [Fact]
    public void ToggleSniffer_CallsActuator()
    {
        var ini = new IniOptions(LansnifferOn: 1);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(modbus: false, lanSniffer: true, opcDa: false, web: false), act);

        c.Toggle(TrayToggleRules.LayerKind.LanSniffer);
        Assert.Equal(1, act.StopSnifferCalls);
        Assert.False(c.LanSnifferRunning);

        c.Toggle(TrayToggleRules.LayerKind.LanSniffer);
        Assert.Equal(1, act.StartSnifferCalls);
        Assert.True(c.LanSnifferRunning);
    }

    [Fact]
    public void ToggleOpcDa_NeverActuated()
    {
        var ini = new IniOptions(OpcdaOn: 1);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(modbus: false, lanSniffer: false, opcDa: true, web: false), act);

        var msg = c.Toggle(TrayToggleRules.LayerKind.OpcDa);

        Assert.Contains("внешний", msg);
        Assert.Equal(0, act.StartModbusCalls + act.StartSnifferCalls);
    }

    // ── 2026-08-27 RESTORED: web arm — PERSIST-ONLY (Э7 «web persist-only
    //    (no actuator)») ─────────────────────────────────────────────────────

    [Fact]
    public void ToggleWeb_PersistOnly_NeverTouchesActuator_FlipsPlan()
    {
        var ini = new IniOptions(WebOn: 0);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(false, false, false, false), act);

        var msg = c.Toggle(TrayToggleRules.LayerKind.Web);

        Assert.True(c.Layers.Web);                       // plan flipped
        Assert.Equal(0, act.StartModbusCalls);           // NO actuator member for web —
        Assert.Equal(0, act.StopModbusCalls);            // the fake counters stay at zero
        Assert.Equal(0, act.StartSnifferCalls);
        Assert.Equal(0, act.StopSnifferCalls);
        Assert.Contains("после перезапуска", msg);       // applies after restart
        Assert.DoesNotContain("failed", msg);
    }

    [Fact]
    public void ToggleWeb_SetAsDefault_PersistsWebOnToIni()
    {
        // Production shape: Layers mirror the ini flags at startup (both modbus
        // and web derive from the SAME ini), so the toggle-save rewrites the
        // runtime flags to their CURRENT plan values, not to garbage.
        var ini = new IniOptions(ModbusOn: 1, WebOn: 0, UiSetAsDefault: 1);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(true, false, false, false), act);

        c.Toggle(TrayToggleRules.LayerKind.Web);

        var saved = IniConfig.Load(IniPath);
        Assert.Equal(1, saved.WebOn);                    // persisted as the next-start default
        Assert.Equal(1, saved.ModbusOn);                 // current plan values preserved
    }

    [Fact]
    public void ToggleWeb_SetAsDefaultOff_SessionOnlyIniUntouched()
    {
        var ini = new IniOptions(WebOn: 0, UiSetAsDefault: 0);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(false, false, false, false), act);

        var msg = c.Toggle(TrayToggleRules.LayerKind.Web);

        Assert.True(c.Layers.Web);                       // session plan changed...
        Assert.Equal(0, IniConfig.Load(IniPath).WebOn);  // ...but ini untouched
        Assert.Contains("только на сеанс", msg);
    }

    [Fact]
    public void ToggleWeb_SecondToggle_FlipsBack()
    {
        var ini = new IniOptions(WebOn: 1, UiSetAsDefault: 1);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(true, false, false, true), act);

        // ON → OFF: the ini now carries web_on=0 (persist-only toggle math).
        c.Toggle(TrayToggleRules.LayerKind.Web);
        Assert.False(c.Layers.Web);
        Assert.Equal(0, IniConfig.Load(IniPath).WebOn);

        // OFF → ON: back to the original plan.
        c.Toggle(TrayToggleRules.LayerKind.Web);
        Assert.True(c.Layers.Web);
        Assert.Equal(1, IniConfig.Load(IniPath).WebOn);
    }

    [Fact]
    public void FailedToggle_ReleasesBusy()
    {
        var ini = new IniOptions(ModbusOn: 0);
        var act = new FakeActuator { ModbusStartThrows = new Exception("x") };
        var c = NewController(ini, new TrayToggleRules.Layers(false, false, false, false), act);
        // Busy is observation only; failure must release the command gate.
        c.Toggle(TrayToggleRules.LayerKind.Modbus);
        Assert.False(c.Busy);
    }

    [Fact]
    public void ActualRuntimeStates_FollowProbes_NotIniFlags()
    {
        var ini = new IniOptions(ModbusOn: 1, LansnifferOn: 1);
        var act = new FakeActuator();
        var c = NewController(ini, new TrayToggleRules.Layers(modbus: true, lanSniffer: true, opcDa: false, web: false), act,
            snifferAvailable: true);

        // No probes ⇒ truth view falls back to the ini-flag view.
        Assert.True(c.ActualModbusRunning);
        Assert.True(c.ActualLanSnifferRunning);

        // Startup bind failure / stopped service ⇒ probes report OFF while the
        // flags stay unchanged (toggle logic keeps working on the flag view).
        c.ModbusBoundProbe = () => false;
        c.SnifferRunningProbe = () => false;
        Assert.False(c.ActualModbusRunning);
        Assert.False(c.ActualLanSnifferRunning);
        Assert.True(c.ModbusRunning);
        Assert.True(c.LanSnifferRunning);
    }
}
