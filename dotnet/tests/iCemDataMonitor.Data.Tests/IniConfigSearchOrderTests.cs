using System.Security.AccessControl;
using System.Security.Principal;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// 2026-08-28 config search-order + rename-migration decision table
/// (owner decree: the program migrates to %ProgramData%\Halliburton\iCem\
/// DataMonitoring — the config file is RENAMED config.ini and lives next to
/// the binary/Data.dat, which CONVERGED into that same folder).
///
/// SEARCH ORDER (Load): 1) canonical config.ini; 2) LEGACY canonical-folder
/// station.ini (the owner's live file); 3) LEGACY next-to-exe config.ini;
/// 4) LEGACY next-to-exe station.ini (dev); 5) none ⇒ CREATE canonical.
/// MIGRATION (rename law): a LEGACY winner is best-effort File.Copy'd
/// (overwrite:false) to the canonical path; a FAILED copy is NEVER fatal —
/// the load continues on the legacy path with an honest evidence note.
///
/// The REAL search order is exercised as a pure function over injected
/// candidate paths (<see cref="IniConfig.SelectIniPath"/> — §15.7 whitelist
/// (a) data-seam: paths are DATA, the branch table is the production code
/// under test); the migration consequences run through
/// <see cref="IniConfig.MigrateToCanonical(string,string,out string?)"/>,
/// also over injected paths. Unit tests therefore never touch the real
/// ProgramData location. The production entry point <c>IniConfig.Load()</c>
/// pins the real constants in <see cref="ProductionPaths_ConstantsMatchTheContract"/>.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // DACL assertions are Windows-only
public sealed class IniConfigSearchOrderTests : IDisposable
{
    private readonly string _root;
    private readonly string _canonicalDir;
    private readonly string _canonicalIni;      // #1: ProgramData\...\config.ini
    private readonly string _legacyPdStationIni; // #2: ProgramData\...\station.ini
    private readonly string _exeDir;
    private readonly string _exeConfigIni;       // #3: next-to-exe config.ini
    private readonly string _exeStationIni;      // #4: next-to-exe station.ini

    public IniConfigSearchOrderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "icem_ini_search_test", Guid.NewGuid().ToString("N"));
        _canonicalDir = Path.Combine(_root, "ProgramData", "Halliburton", "iCem", "DataMonitoring");
        _canonicalIni = Path.Combine(_canonicalDir, "config.ini");
        _legacyPdStationIni = Path.Combine(_canonicalDir, "station.ini");
        _exeDir = Path.Combine(_root, "ExeDir");
        _exeConfigIni = Path.Combine(_exeDir, "config.ini");
        _exeStationIni = Path.Combine(_exeDir, "station.ini");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static string WithModbusOn(string iniPath, int modbusOn)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
        var text = IniConfig.DefaultContent.Replace("modbus_on=1", $"modbus_on={modbusOn}");
        File.WriteAllText(iniPath, text);
        return iniPath;
    }

    private string Select() => IniConfig.SelectIniPath(_canonicalIni, _legacyPdStationIni, _exeConfigIni, _exeStationIni);

    // ═══ Decision table: which candidate does the search order select ═══

    [Fact]
    public void SearchOrder_CanonicalConfigIni_Wins_WhenPresent()
    {
        WithModbusOn(_canonicalIni, modbusOn: 0);
        WithModbusOn(_legacyPdStationIni, modbusOn: 1);
        WithModbusOn(_exeStationIni, modbusOn: 1);

        Assert.Equal(_canonicalIni, Select());
        Assert.Equal(0, IniConfig.LoadWithPath(Select()).ModbusOn); // canonical content, ¬ legacy copies
    }

    [Fact]
    public void SearchOrder_LegacyCanonicalStationIni_IsCandidateTwo()
    {
        WithModbusOn(_legacyPdStationIni, modbusOn: 0); // the OWNER's live file wins over anything exe-dir
        WithModbusOn(_exeStationIni, modbusOn: 1);

        Assert.Equal(_legacyPdStationIni, Select());
    }

    [Fact]
    public void SearchOrder_NextToExeConfigIni_IsCandidateThree()
    {
        WithModbusOn(_exeConfigIni, modbusOn: 0);
        WithModbusOn(_exeStationIni, modbusOn: 1);

        Assert.Equal(_exeConfigIni, Select());
    }

    [Fact]
    public void SearchOrder_NextToExeStationIni_IsCandidateFour_DevFallback()
    {
        WithModbusOn(_exeStationIni, modbusOn: 0);

        Assert.Equal(_exeStationIni, Select());
    }

    [Fact]
    public void SearchOrder_BothAbsent_CreationLocationIsCanonical()
    {
        Assert.False(File.Exists(_canonicalIni));
        Assert.False(File.Exists(_exeStationIni));

        Assert.Equal(_canonicalIni, Select());
        var ini = IniConfig.LoadWithPath(Select());
        Assert.True(File.Exists(_canonicalIni), "missing config must be created at the canonical location");
        Assert.Equal(new IniOptions(), ini); // exact defaults record (web_on=1 per the 2026-08-28 decree)
        Assert.Equal(IniConfig.DefaultContent, File.ReadAllText(_canonicalIni));
    }

    // ═══ Production path constants ═══

    [Fact]
    public void ProductionPaths_ConstantsMatchTheContract()
    {
        var expectedCanonical = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Halliburton", "iCem", "DataMonitoring", "config.ini");
        Assert.Equal(expectedCanonical, IniConfig.ProgramDataPath);
        Assert.EndsWith("config.ini", IniConfig.ProgramDataPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.Combine(IniConfig.ProgramDataDirectory, "station.ini"),
            IniConfig.LegacyProgramDataStationIniPath);
        Assert.EndsWith("station.ini", IniConfig.LegacyExeStationIniPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("config.ini", IniConfig.DefaultPath, StringComparison.OrdinalIgnoreCase);
        // Ordered legacy list for UI-shell diagnostics: #2/#3/#4.
        Assert.Equal(
            new[] { IniConfig.LegacyProgramDataStationIniPath, IniConfig.DefaultPath, IniConfig.LegacyExeStationIniPath },
            IniConfig.LegacyPaths);
    }

    // ═══ MIGRATION (rename law): pins (a)-(d) of the 2026-08-28 decree ═══

    [Fact]
    public void Migration_LegacyStationIniInCanonicalFolder_CopiesToConfigIni()
    {
        // (a) the owner's live file: legacy station.ini in the CANONICAL folder
        //     migrates to config.ini on load; the legacy file is KEPT (copy, ¬ move).
        WithModbusOn(_legacyPdStationIni, modbusOn: 1);

        var selected = Select();
        Assert.Equal(_legacyPdStationIni, selected);

        var active = IniConfig.MigrateToCanonical(selected, _canonicalIni, out var note);
        Assert.Equal(_canonicalIni, active);
        Assert.True(File.Exists(_canonicalIni), "the canonical copy must exist after migration");
        Assert.True(File.Exists(_legacyPdStationIni), "the legacy file is never deleted (copy law)");
        Assert.Equal(1, IniConfig.Parse(File.ReadAllText(_canonicalIni)).ModbusOn); // content carried over
        Assert.NotNull(note);
        Assert.Contains("config migration", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_NextToExeStationIni_CopiesToCanonical()
    {
        // (c) the dev layout: next-to-exe station.ini migrates to canonical too.
        WithModbusOn(_exeStationIni, modbusOn: 0);

        var active = IniConfig.MigrateToCanonical(Select(), _canonicalIni, out _);
        Assert.Equal(_canonicalIni, active);
        Assert.True(File.Exists(_canonicalIni));
    }

    [Fact]
    public void Migration_CopyFails_ContinuesOnLegacyPath_NeverFatal()
    {
        // (b) an IO failure must degrade honestly: active = legacy path, an
        //     evidence note is produced, NO exception — the stand keeps serving.
        WithModbusOn(_legacyPdStationIni, modbusOn: 1);
        // Poison the target: a DIRECTORY at the canonical path makes File.Copy throw IOException.
        Directory.CreateDirectory(_canonicalIni);

        var active = IniConfig.MigrateToCanonical(_legacyPdStationIni, _canonicalIni, out var note);

        Assert.Equal(_legacyPdStationIni, active);
        Assert.NotNull(note);
        Assert.Contains("migration FAILED", note, StringComparison.Ordinal);
        Assert.Contains(_legacyPdStationIni, note, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_CanonicalWinner_NoCopy_NoNote()
    {
        // A canonical winner (or the creation location) passes through —
        // no copy, no evidence noise.
        WithModbusOn(_canonicalIni, modbusOn: 0);

        var active = IniConfig.MigrateToCanonical(_canonicalIni, _canonicalIni, out var note);
        Assert.Equal(_canonicalIni, active);
        Assert.Null(note);
    }

    [Fact]
    public void Migration_Race_CanonicalAppearedLate_Wins()
    {
        // A canonical file appearing between search and migration wins —
        // overwrite:false never clobbers.
        WithModbusOn(_legacyPdStationIni, modbusOn: 1);
        WithModbusOn(_canonicalIni, modbusOn: 0);

        var active = IniConfig.MigrateToCanonical(_legacyPdStationIni, _canonicalIni, out var note);
        Assert.Equal(_canonicalIni, active);
        Assert.Equal(0, IniConfig.Parse(File.ReadAllText(_canonicalIni)).ModbusOn); // canonical content untouched
        Assert.Null(note);
    }

    [Fact]
    public void Migration_NothingExists_CreationLocationIsCanonical_WithWebOnDefault()
    {
        // (d) nothing anywhere ⇒ canonical config.ini is CREATED with the exact
        //     defaults — web_on=1 (dashboard enabled out of the box, decree).
        var ini = IniConfig.LoadWithPath(Select());
        Assert.True(File.Exists(_canonicalIni));
        Assert.Contains("web_on=1", File.ReadAllText(_canonicalIni));
        Assert.Equal(IniOptions.DefaultWebOn, ini.WebOn);
    }

    // ═══ ACL on first creation (BUILTIN\Users → Modify, producer BusWriter (AddIn) precedent) ═══

    [Fact]
    public void FirstCreation_InstallsUsersModifyDacl_OnCreatedDirectory()
    {
        Assert.False(Directory.Exists(_canonicalDir));

        _ = IniConfig.LoadWithPath(_canonicalIni);

        Assert.True(Directory.Exists(_canonicalDir));

        if (!OperatingSystem.IsWindows())
            return; // DACLs are a Windows concept

        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(_canonicalDir));
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;
        var rule = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .FirstOrDefault(r => r.IdentityReference.Value == usersSid
                                 && r.AccessControlType == AccessControlType.Allow
                                 && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);

        Assert.NotNull(rule);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
    }

    // ═══ Write-to-read-source law ═══

    [Fact]
    public void Save_AfterLoadFromLegacyExeDir_WritesBackToExeDir()
    {
        WithModbusOn(_exeStationIni, modbusOn: 1);

        var loaded = IniConfig.LoadWithPath(_exeStationIni); // legacy dev deployment keeps working
        IniConfig.Save(loaded with { ModbusOn = 0 });        // no explicit path — must follow the load source

        Assert.Equal(0, IniConfig.Parse(File.ReadAllText(_exeStationIni)).ModbusOn);
        Assert.False(File.Exists(_canonicalIni), "save must NOT scatter copies into the canonical dir");
    }

    [Fact]
    public void Save_AfterLoadFromCanonical_WritesBackToCanonical()
    {
        WithModbusOn(_canonicalIni, modbusOn: 1);

        var loaded = IniConfig.LoadWithPath(_canonicalIni);
        IniConfig.Save(loaded with { ModbusOn = 0 });

        Assert.Equal(0, IniConfig.Parse(File.ReadAllText(_canonicalIni)).ModbusOn);
        Assert.False(File.Exists(_exeStationIni));
    }

    // ═══ 2026-08-27/28 web-key migration (tombstone-build legacy file) ═══

    [Fact]
    public void SearchOrder_TombstoneFileWithoutWebKeys_LoadsWithWebOnDefault()
    {
        // The tombstone build (deployed 2026-08-27 18:57) wrote config files
        // WITHOUT web_on/web_remote_on. They are OPTIONAL keys: the live stand
        // file must load — and under the 2026-08-28 decree the absent web_on
        // resolves to 1 (the old file GAINS the dashboard, never loses it).
        Directory.CreateDirectory(_canonicalDir);
        var text = IniConfig.DefaultContent
            .Replace("web_on=1" + Environment.NewLine, "")
            .Replace("web_remote_on=0" + Environment.NewLine, "");
        File.WriteAllText(_canonicalIni, text);
        _ = IniConfig.Parse(text); // strict-parseable (optional keys accepted)

        var ini = IniConfig.LoadWithPath(_canonicalIni);
        Assert.Equal(IniOptions.DefaultWebOn, ini.WebOn);
        Assert.Equal(0, ini.WebRemoteOn);

        // And a runtime save EMITS the keys for the next generation of files.
        IniConfig.Save(ini with { WebOn = 0 });
        var rewritten = File.ReadAllText(_canonicalIni);
        Assert.Contains("web_on=0", rewritten); // the operator's explicit OFF persists
        Assert.Contains("web_remote_on=0", rewritten);
    }
}
