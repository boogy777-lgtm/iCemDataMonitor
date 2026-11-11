using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Core.Tests;

/// <summary>
/// station.ini contract tests (TZ_ARCH_V2 §6, W5):
/// create-default-if-missing with exact IniOptions defaults, strict parse,
/// fail-fast listing every violation class, and the layer-off plan seam.
/// </summary>
public sealed class IniConfigTests : IDisposable
{
    private readonly string _dir;

    public IniConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ini_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void DefaultContent_EmitsLanExtZone_And_FullDictionaryTotal()
    {
        var content = IniConfig.DefaultContent;
        var d = new IniOptions(); // SDK defaults = single source of truth (full dictionary v3)

        Assert.Contains($"lan_ext_base={d.LanExtBase}", content);
        Assert.Contains($"lan_ext_len={d.LanExtLen}", content);
        Assert.Contains($"total={d.Total}", content);

        // Roundtrip: emitted default parses back to the exact defaults record.
        Assert.Equal(d, IniConfig.Parse(content));
    }

    [Fact]
    public void Parse_LegacyV2File_NoLanExtKeys_FailsFast()
    {
        // v2 operator file: no lan_ext keys (v2 total). Layout v3 is strict —
        // the file must fail listing EVERY schema gap (¬ silent migration).
        var d = new IniOptions();
        var text = IniConfig.DefaultContent
            .Replace($"lan_ext_base={d.LanExtBase}" + Environment.NewLine, "")
            .Replace($"lan_ext_len={d.LanExtLen}" + Environment.NewLine, "");

        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(text));

        Assert.Contains("missing key 'lan_ext_base'", ex.Message);
        Assert.Contains("missing key 'lan_ext_len'", ex.Message);
    }

    [Fact]
    public void Parse_V2Total650_IsValidUnderV5_DynamicRegCountLaw()
    {
        // v5 schema: lan_ext is ABSORBED into the ace_full reserve (len=0, zone
        // off) — total=650 is a valid bus shape under the dynamic-regCount law
        // [650..65535] and no zone overflows.
        // W22 reconciliation with the Sdk §7 fit law (pinned by
        // Sdk.Tests.Validate_Rejects_Zone_Base_At_Or_Past_Total_Even_When_Zone_Off):
        // a zone KEY must live inside 0..total−1 even when len=0, so the legacy
        // lan_ext_base=650 no longer fits a 650-reg dictionary — the operator
        // file moves the OFF zone's key inside (649).
        var d = new IniOptions();
        var text = IniConfig.DefaultContent
            .Replace($"total={d.Total}", "total=650")
            .Replace($"lan_ext_base={d.LanExtBase}", "lan_ext_base=649");

        var ini = IniConfig.Parse(text);
        Assert.Equal(650, ini.Total);
        Assert.Equal(0, ini.LanExtLen);
    }

    // ═══ create-default ═══

    [Fact]
    public void Load_FileMissing_EmitsUtf8Bom_AndGlobalMmfName()
    {
        var path = Path.Combine(_dir, IniConfig.FileName);
        _ = IniConfig.Load(path);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "created station.ini must carry a UTF-8 BOM — em-dash header comments render as " +
            "mojibake in ANSI editors when emitted without one");

        Assert.Contains("mmf_name=Global\\iCemDataMonitorBus_v1", IniConfig.DefaultContent);
    }

    [Fact]
    public void Load_FileMissing_CreatesExactDefaults()
    {
        var path = Path.Combine(_dir, IniConfig.FileName);
        var ini = IniConfig.Load(path);

        Assert.True(File.Exists(path), "station.ini must be created when missing (AC6)");
        Assert.Equal(IniConfig.DefaultContent, File.ReadAllText(path));
        Assert.Equal(new IniOptions(), ini);
    }

    [Fact]
    public void Load_FileMissing_CreatesParentDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deeper", IniConfig.FileName);
        _ = IniConfig.Load(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_ExistingValidFile_ParsesOverrides()
    {
        var path = Path.Combine(_dir, IniConfig.FileName);
        var text = DefaultWith("opcda_on", "1");
        File.WriteAllText(path, text);

        var ini = IniConfig.Load(path);

        Assert.Equal(1, ini.OpcdaOn);
        Assert.Equal(1, ini.ModbusOn); // untouched key keeps its default
    }

    // ═══ roundtrip ═══

    [Fact]
    public void Parse_DefaultContent_RoundTripsToDefaults()
    {
        var ini = IniConfig.Parse(IniConfig.DefaultContent);
        Assert.Equal(new IniOptions(), ini);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAndSkipsComments()
    {
        var text = IniConfig.DefaultContent
            .Replace("modbus_on=1", "  modbus_on = 1  ")
            + "\n# trailing comment line\n";
        var ini = IniConfig.Parse(text);
        Assert.Equal(new IniOptions(), ini);
    }

    // ═══ fail-fast: every violation class ═══

    [Theory]
    [InlineData("modbus_on", "5", "modbus_on must be 0 or 1")]
    [InlineData("opcda_on", "2", "opcda_on must be 0 or 1")]
    [InlineData("lansniffer_on", "9", "lansniffer_on must be 0 or 1")]
    public void Parse_FlagOutOfRange_FailsFast(string key, string value, string fragment)
    {
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(DefaultWith(key, value)));
        Assert.Contains(fragment, ex.Message);
    }

    // ── 2026-08-27 web layer RESTORED (corrected decree «Web - удали
    //    (контрол sim)»): web_on/web_remote_on are schema keys again —
    //    OPTIONAL for migration (see below); emule_on stays RETIRED. ──
    // MIGRATION LAW (coordinator, 2026-08-27): a config file written by the
    // tombstone build (deployed 2026-08-27 18:57) does NOT carry web keys —
    // strict "missing key" would make the live stand file fatal on the next
    // start. web_on/web_remote_on are therefore OPTIONAL.
    // 2026-08-28 OWNER DECREE: web_on absent ⇒ 1 («по дефолту web должен быть
    // включён» — an old file GAINS the dashboard, never loses it; the old
    // «absent ⇒ 0» rule is superseded). web_remote_on absent ⇒ 0 (loopback).
    // Any OTHER unknown key remains a hard violation (Parse_UnknownKey_FailsFast).

    [Fact]
    public void Parse_RetiredEmuleKey_AcceptedAndIgnored()
    {
        var text = IniConfig.DefaultContent.Replace(
            "[station]" + Environment.NewLine,
            "[station]" + Environment.NewLine + "emule_on=1" + Environment.NewLine);
        var ini = IniConfig.Parse(text); // must NOT throw — retired key is ignored
        Assert.NotNull(ini);
    }

    [Fact]
    public void Parse_RetiredKeys_DoNotLeakIntoOptions()
    {
        // emule_on stays retired; 2026-08-31: com_port + the com/lan/raw zone
        // keys are retired too (dead, owner decree) — none may reappear in the
        // written default. web_on/web_remote_on are live schema keys — they DO.
        Assert.DoesNotContain("emule_on", IniConfig.DefaultContent);
        Assert.DoesNotContain("com_port", IniConfig.DefaultContent);
        Assert.DoesNotContain("com_base", IniConfig.DefaultContent);
        Assert.DoesNotContain("com_len", IniConfig.DefaultContent);
        Assert.DoesNotContain("lan_base", IniConfig.DefaultContent);
        Assert.DoesNotContain("lan_len", IniConfig.DefaultContent);
        Assert.DoesNotContain("raw_base", IniConfig.DefaultContent);
        Assert.DoesNotContain("raw_len", IniConfig.DefaultContent);
        Assert.Contains("web_on=1", IniConfig.DefaultContent);
        Assert.Contains("web_remote_on=0", IniConfig.DefaultContent);
    }

    [Fact]
    public void Parse_RetiredDeadKeys_AcceptedAndIgnored_LiveFileKeepsLoading()
    {
        // 2026-08-31 (owner decree — dead keys removed): the live stand
        // config.ini still carries com_port and the [zones] com/lan/raw
        // base+len keys. Migration law: such a file must NOT become fatal —
        // the keys are accepted-and-ignored (RETIRED), values dropped.
        var text = IniConfig.DefaultContent
            .Replace("[station]" + Environment.NewLine,
                     "[station]" + Environment.NewLine + "com_port=3" + Environment.NewLine)
            .Replace("[zones]" + Environment.NewLine,
                     "[zones]" + Environment.NewLine +
                     "com_base=4" + Environment.NewLine + "com_len=196" + Environment.NewLine +
                     "lan_base=200" + Environment.NewLine + "lan_len=200" + Environment.NewLine +
                     "raw_base=400" + Environment.NewLine + "raw_len=200" + Environment.NewLine);
        var ini = IniConfig.Parse(text); // must NOT throw — retired keys are ignored
        Assert.Equal(new IniOptions(), ini);
    }

    [Fact]
    public void Parse_FileWithoutWebKeys_TombstoneMigration_GainsWebDefault()
    {
        // The tombstone build (2026-08-27 18:57) wrote config WITHOUT the web
        // keys. Such a file MUST load (optional keys — never fatal), and under
        // the 2026-08-28 decree the absent web_on resolves to 1 (dashboard
        // enabled out of the box): the old file GAINS the web layer.
        var text = IniConfig.DefaultContent
            .Replace("web_on=1" + Environment.NewLine, "")
            .Replace("web_remote_on=0" + Environment.NewLine, "");
        var ini = IniConfig.Parse(text); // must NOT throw — optional keys

        Assert.Equal(IniOptions.DefaultWebOn, ini.WebOn);
        Assert.Equal(0, ini.WebRemoteOn);
    }

    [Theory]
    [InlineData("web_on", "1", "web_remote_on", "0", 1, 0)]
    [InlineData("web_on", "0", "web_remote_on", "1", 0, 1)]
    [InlineData("web_on", "1", "web_remote_on", "1", 1, 1)]
    public void Parse_WebKeysPresent_ParseToGivenValues(
        string keyA, string valueA, string keyB, string valueB, int expectedA, int expectedB)
    {
        // STRICT law preserved: a PRESENT web_on=0 still means OFF — the
        // optional default only fills ABSENCE, it never overrides an operator.
        var text = IniConfig.DefaultContent
            .Replace("web_on=1", $"{keyA}={valueA}")
            .Replace("web_remote_on=0", $"{keyB}={valueB}");
        var ini = IniConfig.Parse(text);

        Assert.Equal(expectedA, keyA == "web_on" ? ini.WebOn : ini.WebRemoteOn);
        Assert.Equal(expectedB, keyB == "web_remote_on" ? ini.WebRemoteOn : ini.WebOn);
    }

    [Fact]
    public void Parse_WebKeyOutOfRange_FailsFast()
    {
        // OPTIONAL ≠ unvalidated: a PRESENT key still goes through the full
        // IniOptions.Validate contract (flag 0..1).
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(DefaultWith("web_on", "2")));
        Assert.Contains("web_on must be 0 or 1", ex.Message);
    }

    [Fact]
    public void Save_RoundTripsWebKeys_AndEmitsThem()
    {
        var path = Path.Combine(_dir, "webkeys.ini");
        // Start from a tombstone-era file WITHOUT the web keys (as deployed).
        var legacy = IniConfig.DefaultContent
            .Replace("web_on=1" + Environment.NewLine, "")
            .Replace("web_remote_on=0" + Environment.NewLine, "");
        File.WriteAllText(path, legacy);

        var loaded = IniConfig.Load(path);
        Assert.Equal(IniOptions.DefaultWebOn, loaded.WebOn); // absent ⇒ default 1 (decree 2026-08-28)
        Assert.Equal(0, loaded.WebRemoteOn);

        var toggled = loaded with { WebOn = 0, WebRemoteOn = 1 }; // explicit operator override survives the round trip
        IniConfig.Save(path, toggled);

        var text = File.ReadAllText(path);
        Assert.Contains("web_on=0", text);        // writer always emits the keys
        Assert.Contains("web_remote_on=1", text);
        Assert.Equal(toggled, IniConfig.Load(path));
    }

    [Fact]
    public void DefaultContent_WebKeys_OrderMatchesBackup()
    {
        // [station] order pinned per the live-backup layout: modbus_on, web_on,
        // opcda_on, lansniffer_on, ui_set_as_default, web_remote_on,
        // modbus_port… (2026-08-31: the dead com_port key is REMOVED).
        var station = IniConfig.DefaultContent[IniConfig.DefaultContent.IndexOf("[station]")..];
        var end = station.IndexOf("[zones]", StringComparison.Ordinal);
        station = station[..end];
        int at(string key) => station.IndexOf(key + "=", StringComparison.Ordinal);
        int[] order = { at("modbus_on"), at("web_on"), at("opcda_on"), at("lansniffer_on"),
                        at("ui_set_as_default"), at("web_remote_on"), at("modbus_port") };
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Theory]
    [InlineData("modbus_port", "70000", "modbus_port out of range [1..65535]")]
    [InlineData("web_port", "0", "web_port out of range [1..65535]")]
    [InlineData("lan_ext_len", "20000", "lan_ext out of range")]
    public void Parse_ContractViolation_FailsFast(string key, string value, string fragment)
    {
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(DefaultWith(key, value)));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void Parse_ZoneOverlap_FailsFast()
    {
        // 2026-08-31: com/lan/raw zone keys are gone — the overlap law now
        // covers the v4-compat zones: wits [600..700) ∩ lan_ext (base 650).
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(DefaultWith("wits_len", "100")));
        Assert.Contains("zone overlap", ex.Message);
    }

    [Fact]
    public void Parse_UnknownKey_FailsFast()
    {
        var text = IniConfig.DefaultContent.Replace("modbus_on=1", "modbus_on=1\nmodbus_on_typo=1");
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(text));
        Assert.Contains("unknown key 'modbus_on_typo'", ex.Message);
    }

    [Fact]
    public void Parse_UnknownSection_FailsFast()
    {
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(IniConfig.DefaultContent + "\n[mystery]\nx=1\n"));
        Assert.Contains("unknown section [mystery]", ex.Message);
    }

    [Fact]
    public void Parse_MissingKey_FailsFast()
    {
        var text = IniConfig.DefaultContent.Replace("web_port=8090" + Environment.NewLine, "");
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(text));
        Assert.Contains("missing key 'web_port'", ex.Message);
    }

    [Fact]
    public void Parse_DuplicateKey_FailsFast()
    {
        var text = IniConfig.DefaultContent.Replace("modbus_on=1", "modbus_on=1\nmodbus_on=1");
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(text));
        Assert.Contains("duplicate key 'modbus_on'", ex.Message);
    }

    [Fact]
    public void Parse_NonNumericValue_FailsFast()
    {
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(DefaultWith("modbus_port", "abc")));
        Assert.Contains("modbus_port", ex.Message);
        Assert.Contains("not an integer", ex.Message);
    }

    [Fact]
    public void Parse_MalformedLine_FailsFast()
    {
        var text = IniConfig.DefaultContent.Replace("modbus_on=1", "modbus_on 1");
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(text));
        Assert.Contains("malformed line", ex.Message);
    }

    [Fact]
    public void Parse_MultipleViolations_AllListedInOneException()
    {
        var d = new IniOptions();
        var text = DefaultWith("modbus_on", "9");                       // flag violation
        text = text.Replace($"wits_len={d.WitsLen}", "wits_len=100");   // wits [600..700) ∩ lan_ext (base 650)

        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(text));

        Assert.Contains("modbus_on must be 0 or 1", ex.Message);
        Assert.Contains("zone overlap", ex.Message);
        Assert.Equal(2, ex.Violations.Count);
    }

    [Fact]
    public void Parse_EmptyMmfName_FailsFast()
    {
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(DefaultWith("mmf_name", "")));
        Assert.Contains("mmf_name must be non-empty", ex.Message);
    }

    // ═══ layer plan seam (off ⇒ not started) ═══

    [Fact]
    public void LayerFlags_DefaultIni_ModbusWebSnifferOn()
    {
        // 2026-08-28 decree: web_on default 1; 2026-09-01 OWNER DECREE: sniffer
        // ON out of the box («проверь дефолтные настройки config.ini —
        // sniffer/modbus должны быть включены») — the DEFAULTS record starts
        // with Modbus, Web AND LanSniffer ON.
        var f = LayerFlags.From(new IniOptions());
        Assert.True(f.Modbus);
        Assert.True(f.LanSniffer);
        Assert.False(f.OpcDa);
        Assert.True(f.Web); // defaults: web_on=1 (IniOptions.DefaultWebOn)
    }

    [Fact]
    public void LayerFlags_AllOn_AllTrue()
    {
        var f = LayerFlags.From(new IniOptions(ModbusOn: 1, OpcdaOn: 1, LansnifferOn: 1, WebOn: 1));
        Assert.True(f.Modbus && f.LanSniffer && f.OpcDa && f.Web);
    }

    [Fact]
    public void LayerFlags_AllOff_AllFalse()
    {
        var f = LayerFlags.From(new IniOptions(ModbusOn: 0, OpcdaOn: 0, LansnifferOn: 0, WebOn: 0));
        Assert.False(f.Modbus || f.LanSniffer || f.OpcDa || f.Web);
    }

    [Fact]
    public void LayerFlags_SnifferOnly_OnlySniffer()
    {
        // 2026-08-28: web_on default is 1 — the sniffer-only plan must pin it
        // OFF explicitly (the DEFAULTS record carries Web=true, see
        // LayerFlags_DefaultIni_ModbusOnAndWebOn).
        var f = LayerFlags.From(new IniOptions(ModbusOn: 0, LansnifferOn: 1, WebOn: 0));
        Assert.False(f.Modbus);
        Assert.True(f.LanSniffer);
        Assert.False(f.OpcDa);
        Assert.False(f.Web);
    }

    [Fact]
    public void LayerFlags_WebOn_OnlyWebGatesTheLayer()
    {
        // RESTORED 2026-08-27: web_on=1 alone must light exactly the Web arm.
        // 2026-09-01: lansniffer_on defaults to 1 — pin it OFF explicitly here.
        var f = LayerFlags.From(new IniOptions(ModbusOn: 0, LansnifferOn: 0, WebOn: 1));
        Assert.False(f.Modbus);
        Assert.False(f.LanSniffer);
        Assert.False(f.OpcDa);
        Assert.True(f.Web);
    }

    // ═══ runtime save / round-trip ═══

    [Fact]
    public void Save_TogglesFlags_PreservesCommentsAndUnknownLines()
    {
        var path = Path.Combine(_dir, "runtime.ini");
        var original = IniConfig.DefaultContent;
        File.WriteAllText(path, original);

        var loaded = IniConfig.Load(path);
        Assert.Equal(0, loaded.OpcdaOn);

        var toggled = loaded with { OpcdaOn = 1 };
        IniConfig.Save(path, toggled);

        var text = File.ReadAllText(path);
        Assert.Contains("opcda_on=1", text);
        Assert.Contains("; config.ini — iCemDataMonitor", text); // header comment survives
        Assert.DoesNotContain("opcda_on=0", text);

        var reparsed = IniConfig.Load(path);
        Assert.Equal(toggled, reparsed);
    }

    [Fact]
    public void Save_InvalidOptions_ThrowsWithoutTouchingFile()
    {
        var path = Path.Combine(_dir, "protected.ini");
        File.WriteAllText(path, IniConfig.DefaultContent);
        var original = File.ReadAllText(path);

        var bad = new IniOptions(ModbusOn: 2);
        var ex = Assert.Throws<InvalidOperationException>(() => IniConfig.Save(path, bad));
        Assert.Contains("Refusing to save invalid", ex.Message);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Save_ProducesFileThatPassesStrictReparse()
    {
        var path = Path.Combine(_dir, "rewrite.ini");
        File.WriteAllText(path, IniConfig.DefaultContent);
        var desired = new IniOptions(ModbusOn: 0, LansnifferOn: 1, ModbusPort: 1502);
        IniConfig.Save(path, desired);
        var reparsed = IniConfig.Load(path);
        Assert.Equal(desired, reparsed);
    }

    // ═══ ui_set_as_default (W7) — the ONLY optional schema key ═══

    [Fact]
    public void Parse_FileWithoutUiSetAsDefault_DefaultsToOne()
    {
        // Operator v2 files predate the key — they must still load (optional),
        // and the default must be 1 (persist-on-toggle ON).
        var text = IniConfig.DefaultContent.Replace("ui_set_as_default=1" + Environment.NewLine, "");
        var ini = IniConfig.Parse(text);
        Assert.Equal(1, ini.UiSetAsDefault);
    }

    [Fact]
    public void Parse_UiSetAsDefaultOutOfRange_FailsFast()
    {
        var ex = Assert.Throws<IniConfigException>(() => IniConfig.Parse(DefaultWith("ui_set_as_default", "5")));
        Assert.Contains("ui_set_as_default must be 0 or 1", ex.Message);
    }

    [Fact]
    public void Save_RoundTripsUiSetAsDefault_AndEmitsTheKey()
    {
        var path = Path.Combine(_dir, "setdefault.ini");
        // Start from an OLD v2 file WITHOUT the key (as deployed on the stand).
        File.WriteAllText(path, IniConfig.DefaultContent.Replace("ui_set_as_default=1" + Environment.NewLine, ""));

        var loaded = IniConfig.Load(path);
        Assert.Equal(1, loaded.UiSetAsDefault); // absent ⇒ default

        var toggled = loaded with { UiSetAsDefault = 0 };
        IniConfig.Save(path, toggled);

        var text = File.ReadAllText(path);
        Assert.Contains("ui_set_as_default=0", text); // writer always emits the key
        Assert.Equal(toggled, IniConfig.Load(path));
    }

    // ── helper: rewrite one key's line inside the default content ──
    private static string DefaultWith(string key, string value)
    {
        var lines = IniConfig.DefaultContent.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(key + "=", StringComparison.Ordinal))
            {
                lines[i] = key + "=" + value;
                break;
            }
        }
        return string.Join('\n', lines);
    }
}
