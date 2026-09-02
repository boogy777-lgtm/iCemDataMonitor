using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Sdk.Tests;

// W1 contract tests: station.ini schema defaults + validator (TZ_ARCH_V2 §6).
public class IniSchemaTests
{
    [Fact]
    public void Defaults_Match_Spec_Exactly()
    {
        var d = new IniOptions();
        Assert.Equal(1, d.ModbusOn);   // owner decree 2026-08-31: modbus ON by default
        Assert.Equal(0, d.OpcdaOn);
        Assert.Equal(1, d.LansnifferOn); // 2026-09-01 owner decree: sniffer ON by default (was 0)
        Assert.Equal(502, d.ModbusPort);
        Assert.Equal(8090, d.WebPort);
        Assert.Equal(@"Global\iCemDataMonitorBus_v1", d.MmfName);
        Assert.Equal(@"C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat", d.MmfPath);
        // 2026-08-31 (owner decree): com_port + com/lan/raw zone keys REMOVED
        // (dead — zero consumers; runtime geometry = compile-time BusLayout).
        Assert.Equal(600, d.WitsBase);
        Assert.Equal(0, d.WitsLen);           // v5: wits absorbed into ace_full reserve — off (inert by design)
        Assert.Equal(650, d.LanExtBase);
        Assert.Equal(0, d.LanExtLen);         // v5: lan_ext absorbed into ace_full reserve — off (inert by design)
        Assert.Equal(10001, d.Total);         // v5: D3 dictionary (ace_full 600..10000 physically exists)
    }

    [Fact]
    public void Validate_Accepts_Spec_Defaults()
    {
        Assert.Empty(IniOptions.Validate(new IniOptions()));
    }

    [Fact]
    public void Validate_Rejects_Zone_Overlap()
    {
        // 2026-08-31: only the v4-compat zones remain — wits [600..700) ∩ lan_ext [650..750)
        var o = new IniOptions(WitsLen: 100, LanExtLen: 100);
        var errors = IniOptions.Validate(o);
        Assert.Contains(errors, e => e.Contains("overlap"));
    }

    [Fact]
    public void Validate_Rejects_Wits_Overlap()
    {
        // wits defaults to len=0 (off); overlap requires an explicit length.
        // wits [640..690) ∩ lan_ext [650..700)
        var o = new IniOptions(WitsBase: 640, WitsLen: 50, LanExtLen: 50);
        var errors = IniOptions.Validate(o);
        Assert.Contains(errors, e => e.Contains("overlap"));
    }

    [Fact]
    public void Validate_Rejects_LanExt_Overlap()
    {
        // lan_ext defaults to len=0 (off); overlap requires an explicit length.
        // lan_ext [550..649] ∩ wits [600..649]
        Assert.Contains(IniOptions.Validate(new IniOptions(WitsLen: 100, LanExtBase: 550, LanExtLen: 100)),
                        e => e.Contains("overlap"));
        // lan_ext [600..799] ∩ wits [600..699]
        Assert.Contains(IniOptions.Validate(new IniOptions(WitsLen: 100, LanExtBase: 600, LanExtLen: 200)),
                        e => e.Contains("overlap"));
    }

    [Fact]
    public void Validate_Rejects_Zone_Base_At_Or_Past_Total_Even_When_Zone_Off()
    {
        // §7 fit law: the KEY (base) must live inside 0..total-1 even for len=0
        // (zone off). Regression pin: base == total with len=0 slipped through the
        // old `base + len - 1 > total - 1` formula (off-by-one on empty ranges).
        Assert.Contains(IniOptions.Validate(new IniOptions(Total: 650, LanExtBase: 650)),
                        e => e.Contains("out of range") && e.Contains("lan_ext"));
        Assert.Contains(IniOptions.Validate(new IniOptions(Total: 650, LanExtBase: 651)),
                        e => e.Contains("out of range"));
        // Positive controls: the last valid cell stays legal; the live-stand shape
        // (total=10001, off v4-compat zones at 600/650) stays legal.
        Assert.Empty(IniOptions.Validate(new IniOptions(Total: 650, LanExtBase: 649)));
        Assert.Empty(IniOptions.Validate(new IniOptions()));
    }

    [Fact]
    public void Validate_Rejects_Zone_Out_Of_Range()
    {
        // lan_ext 650..10049 exceeds 0..10000 (fit is checked against o.Total)
        Assert.Contains(IniOptions.Validate(new IniOptions(LanExtLen: 9400)),
                        e => e.Contains("out of range"));
        // wits 9999..10008 exceeds 0..10000
        Assert.Contains(IniOptions.Validate(new IniOptions(WitsBase: 9999, WitsLen: 10)),
                        e => e.Contains("out of range"));
    }

    [Fact]
    public void Validate_Zone_Fit_Is_Checked_Against_Total()
    {
        // total 1650 (valid per the dynamic law) with an explicit lan_ext length
        // ⇒ lan_ext 650..8403 exceeds it.
        var errors = IniOptions.Validate(new IniOptions(Total: 1650, LanExtLen: 7754));
        Assert.Contains(errors, e => e.Contains("out of range") && e.Contains("lan_ext"));
        Assert.DoesNotContain(errors, e => e.Contains("total"));
    }

    [Fact]
    public void Validate_Rejects_Zone_Base_Below_ZoneMap_Floor()
    {
        // Floor = ZoneBaseMin (4) — v5: the header state block is regs 0..3 only
        // and the floor follows HeaderRegCount. 2026-08-31: the com/lan/raw keys
        // are gone — the floor still governs the v4-compat zone keys.
        Assert.Contains(IniOptions.Validate(new IniOptions(WitsBase: 2)),
                        e => e.Contains("base must be >="));
        Assert.Contains(IniOptions.Validate(new IniOptions(LanExtBase: 3)),
                        e => e.Contains("base must be >="));
        Assert.Empty(IniOptions.Validate(new IniOptions()));
    }

    [Fact]
    public void Validate_Rejects_Odd_Zone_Length()
    {
        Assert.Contains(IniOptions.Validate(new IniOptions(WitsLen: 149)),
                        e => e.Contains("even"));
    }

    [Fact]
    public void Validate_Rejects_Bad_Ports()
    {
        Assert.Contains(IniOptions.Validate(new IniOptions(ModbusPort: 0)),
                        e => e.Contains("modbus_port"));
        Assert.Contains(IniOptions.Validate(new IniOptions(ModbusPort: 70000)),
                        e => e.Contains("modbus_port"));
        Assert.Contains(IniOptions.Validate(new IniOptions(WebPort: 65536)),
                        e => e.Contains("web_port"));
    }

    [Fact]
    public void Validate_Rejects_Bad_Flags()
    {
        Assert.Contains(IniOptions.Validate(new IniOptions(ModbusOn: 2)),
                        e => e.Contains("modbus_on"));
        Assert.Contains(IniOptions.Validate(new IniOptions(LansnifferOn: -1)),
                        e => e.Contains("lansniffer_on"));
    }

    [Fact]
    public void Defaults_WebOn_And_WebRemoteOn_Match_Spec()
    {
        // Web layer RESTORED 2026-08-27 (owner decree «Web - удали (контрол sim)»
        // meant removing the sim control from the page, not the page); the keys
        // returned to the schema. 2026-08-28 OWNER DECREE: web_on default 1 —
        // «по дефолту web должен быть включён» (dashboard enabled out of the
        // box; Э5 row web_on). web_remote_on stays 0 = bind loopback-only
        // (authorization = the closed contour, not IP-guards).
        var d = new IniOptions();
        Assert.Equal(1, d.WebOn);
        Assert.Equal(0, d.WebRemoteOn);
    }

    [Fact]
    public void Defaults_WebOn_SingleSourceConstant()
    {
        // The OPTIONAL-key default is consumed at THREE sites: the record
        // default, the strict parser's absent-key fallback (Core IniConfig →
        // GetIntOrDefault) and the spec (Э5). The record cannot reference its
        // own const inside the primary-ctor parameter list, so the literal is
        // pinned EQUIVALENT to IniOptions.DefaultWebOn here — the const is the
        // single source (Parse uses it; drift = this test fails).
        var d = new IniOptions();
        Assert.Equal(IniOptions.DefaultWebOn, d.WebOn);
        Assert.Equal(1, IniOptions.DefaultWebOn);
    }

    [Fact]
    public void Validate_Rejects_Bad_Web_Flags()
    {
        Assert.Contains(IniOptions.Validate(new IniOptions(WebOn: 2)),
                        e => e.Contains("web_on must be 0 or 1"));
        Assert.Contains(IniOptions.Validate(new IniOptions(WebRemoteOn: -1)),
                        e => e.Contains("web_remote_on must be 0 or 1"));
    }

    [Fact]
    public void Validate_Rejects_Total_Out_Of_Law_Range()
    {
        // Dynamic-regCount law: total ∈ [650..65535] — RUNTIME DATA, ¬ a pinned const.
        Assert.Contains(IniOptions.Validate(new IniOptions(Total: 500)),
                        e => e.Contains("total"));
        Assert.Contains(IniOptions.Validate(new IniOptions(Total: 649)),
                        e => e.Contains("total"));
        Assert.Contains(IniOptions.Validate(new IniOptions(Total: 65536)),
                        e => e.Contains("total"));
        Assert.Contains(IniOptions.Validate(new IniOptions(Total: 0)),
                        e => e.Contains("total"));
    }

    [Fact]
    public void Validate_Accepts_Any_Lawful_Total()
    {
        // Any total in [650..65535] is accepted (zone-fit errors are a different axis).
        foreach (var total in new[] { 650, 1650, 8404, 65535 })
        {
            var errors = IniOptions.Validate(new IniOptions(Total: total));
            Assert.DoesNotContain(errors, e => e.Contains("total"));
        }
    }

    [Fact]
    public void Validate_Rejects_Empty_Mapping_Names()
    {
        Assert.Contains(IniOptions.Validate(new IniOptions(MmfName: "")),
                        e => e.Contains("mmf_name"));
        Assert.Contains(IniOptions.Validate(new IniOptions(MmfPath: "  ")),
                        e => e.Contains("mmf_path"));
    }

    [Fact]
    public void Validate_Rejects_Null_Options()
    {
        Assert.Throws<ArgumentNullException>(() => IniOptions.Validate(null!));
    }

    [Fact]
    public void Record_Is_Immutable_And_Defaults_Independent_Per_Instance()
    {
        var a = new IniOptions();
        var b = new IniOptions(ModbusOn: 0, OpcdaOn: 1, ModbusPort: 1502);
        Assert.Equal(1, a.ModbusOn);          // a untouched
        Assert.Equal(502, a.ModbusPort);
        Assert.Equal(0, b.ModbusOn);
        Assert.Equal(1, b.OpcdaOn);
        Assert.Empty(IniOptions.Validate(b)); // valid non-default config accepted
    }
}
