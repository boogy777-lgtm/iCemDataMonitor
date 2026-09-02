// EmuSettings — XML persistence at
// %ProgramData%\Halliburton\iCem\DataMonitoring\emule_config.xml
// 2026-08-31 (owner decree «у нас всё живет в 1 папке datamonitor и в ней же
// должен лежать конфиг с понятным названием emule_config.xml»): the settings
// file MOVED from %LOCALAPPDATA%\Halliburton\iCem\iCemEmule\settings.xml to
// the SAME canonical folder as the host's config.ini. XML schema/keys and the
// root element <iCemEmule> are UNCHANGED — only path + filename changed.
// MIGRATION (law «copy, ¬ move», best-effort, never fatal): a legacy file is
// COPIED to the canonical path when the canonical one is absent; the legacy
// file is never deleted.
// Additive schema: missing elements → defaults. Thread-safe via lock.
// R1: NO MongoDB references.

using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;

namespace Halliburton.iCemEmule.Core;

public sealed class EmuSettings
{
    // Canonical folder law (2026-08-31): the SAME ProgramData directory the
    // host uses for config.ini / Data.dat (IniConfig.ProgramDataDirectory) —
    // CommonApplicationData, ¬ a hardcoded C:\ path. iCemEmule.Core cannot
    // reference iCemDataMonitor.Core, so the composition is mirrored verbatim.
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "Halliburton", "iCem", "DataMonitoring");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "emule_config.xml");

    // LEGACY (pre-2026-08-31): migration SOURCE only — read by
    // MigrateLegacyIfNeeded, never written.
    private static readonly string LegacySettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Halliburton", "iCem", "iCemEmule", "settings.xml");

    private static readonly object _lock = new();

    /// <summary>
    /// Test hook: when non-null, Load/Save use this path instead of the real
    /// ProgramData emule_config.xml (production = null). Unit tests that start
    /// and stop an EmuEngineHost must set it — Stop() saves settings, and a
    /// test must NEVER clobber the stand's (possibly transport-gate-patched)
    /// emule_config.xml. Legacy migration is SKIPPED while the override is
    /// set (a test never touches the operator's files).
    /// </summary>
    public static string? SettingsPathOverride { get; set; }

    private static string CurrentSettingsPath => SettingsPathOverride ?? SettingsPath;

    // ── LAN settings ────────────────────────────────────────────────────
    // 2026-08-31 (owner decree «дефолт у эмулятора — всё выключено»): EVERY
    // emulation toggle defaults OFF — a freshly created emule_config.xml is
    // INERT; the operator turns streams on explicitly (UI/CLI or gate patch).
    // (Carried over from the P2 2026-08-26 decree — verified unchanged.)
    public bool   LanEnabled          { get; set; }
    public string LanAdapter          { get; set; } = string.Empty;
    public uint   LanEquipment        { get; set; } = 0x00A86F46;
    // P2 2026-08-26 (owner decree «дефолт у эмулятора — всё выключено lan\com»):
    // emulation defaults OFF; the operator enables it explicitly via UI/CLI.
    public bool   LanEmuleEnabled     { get; set; } = false;   // ON = автогенерация; OFF = freeze, только ручная запись
    public bool   LanFirmwareSvEnabled { get; set; } = false;   // 14 firmware-таблиц в суперфрейме (off по умолчанию)
    public int    UnitPreset          { get; set; } = 1;   // 1 = LanNative (реальный cRIO шлёт нативные единицы)

    // ── Единицы измерений (глобально для COM и LAN) ─────────────────────
    // "" = следовать пресету (нет переопределения группы).
    public string UnitFlowOverride        { get; set; } = "";
    public string UnitTemperatureOverride { get; set; } = "";
    public string UnitDensityOverride     { get; set; } = "";
    public string UnitVolumeOverride      { get; set; } = "";
    public string UnitLevelOverride       { get; set; } = "";
    public string UnitPressureOverride    { get; set; } = "";

    // ── COM settings ────────────────────────────────────────────────────
    public bool   ComEnabled      { get; set; }
    // P2 2026-08-26 (owner decree): emulation defaults OFF (см. LanEmuleEnabled).
    public bool   ComEmuleEnabled { get; set; } = false;   // ON = симуляция; OFF = freeze + ручная запись
    public string ComPort         { get; set; } = string.Empty;
    public string ComDeviceType   { get; set; } = string.Empty;
    public string ComTemplateName { get; set; } = string.Empty;

    // ── Window placement ────────────────────────────────────────────────
    public int WindowX      { get; set; } = -1;   // -1 = not saved yet
    public int WindowY      { get; set; } = -1;
    public int WindowWidth  { get; set; }
    public int WindowHeight { get; set; }

    // ── Load ────────────────────────────────────────────────────────────
    public static EmuSettings Load()
    {
        lock (_lock)
        {
            MigrateLegacyIfNeeded();

            var s = new EmuSettings();
            if (!File.Exists(CurrentSettingsPath))
                return s;

            try
            {
                var doc = XDocument.Load(CurrentSettingsPath);
                var root = doc.Root;
                if (root is null) return s;

                // LAN
                s.LanEnabled          = ReadBool(root, "LanEnabled");
                s.LanAdapter          = ReadStr(root, "LanAdapter");
                s.LanEquipment        = ReadUInt(root, "LanEquipment", 0x00A86F46);
                s.LanEmuleEnabled     = ReadBool(root, "LanEmuleEnabled", false);   // P2: default OFF
                s.LanFirmwareSvEnabled = ReadBool(root, "LanFirmwareSvEnabled", false);
                s.UnitPreset          = ReadInt(root, "UnitPreset", 1);   // default = LanNative

                // Единицы измерений (глобально)
                s.UnitFlowOverride        = ReadStr(root, "UnitFlowOverride");
                s.UnitTemperatureOverride = ReadStr(root, "UnitTemperatureOverride");
                s.UnitDensityOverride     = ReadStr(root, "UnitDensityOverride");
                s.UnitVolumeOverride      = ReadStr(root, "UnitVolumeOverride");
                s.UnitLevelOverride       = ReadStr(root, "UnitLevelOverride");
                s.UnitPressureOverride    = ReadStr(root, "UnitPressureOverride");

                // COM
                s.ComEnabled      = ReadBool(root, "ComEnabled");
                s.ComEmuleEnabled = ReadBool(root, "ComEmuleEnabled", false);   // P2: default OFF
                s.ComPort         = ReadStr(root, "ComPort");
                s.ComDeviceType   = ReadStr(root, "ComDeviceType");
                s.ComTemplateName = ReadStr(root, "ComTemplateName");

                // Window
                s.WindowX      = ReadInt(root, "WindowX", -1);
                s.WindowY      = ReadInt(root, "WindowY", -1);
                s.WindowWidth  = ReadInt(root, "WindowWidth");
                s.WindowHeight = ReadInt(root, "WindowHeight");
            }
            catch
            {
                // Corrupt file → return defaults
            }

            return s;
        }
    }

    // ── Save ────────────────────────────────────────────────────────────
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                // On FIRST creation of the canonical ProgramData directory,
                // grant BUILTIN\Users Modify so non-elevated emulator/operator
                // processes can write emule_config.xml (same folder law as the
                // host's config.ini — precedent: IniConfig.InstallUsersModifyDacl).
                if (!Directory.Exists(SettingsDir))
                {
                    Directory.CreateDirectory(SettingsDir);
                    InstallUsersModifyDacl(SettingsDir);
                }

                var doc = new XDocument(
                    new XElement("iCemEmule",
                        new XElement("LanEnabled",         LanEnabled),
                        new XElement("LanAdapter",         LanAdapter),
                        new XElement("LanEquipment",       $"0x{LanEquipment:X8}"),
                        new XElement("LanEmuleEnabled",    LanEmuleEnabled),
                        new XElement("LanFirmwareSvEnabled", LanFirmwareSvEnabled),
                        new XElement("UnitPreset",         UnitPreset),

                        new XElement("UnitFlowOverride",        UnitFlowOverride),
                        new XElement("UnitTemperatureOverride", UnitTemperatureOverride),
                        new XElement("UnitDensityOverride",     UnitDensityOverride),
                        new XElement("UnitVolumeOverride",      UnitVolumeOverride),
                        new XElement("UnitLevelOverride",       UnitLevelOverride),
                        new XElement("UnitPressureOverride",    UnitPressureOverride),

                        new XElement("ComEnabled",      ComEnabled),
                        new XElement("ComEmuleEnabled", ComEmuleEnabled),
                        new XElement("ComPort",         ComPort),
                        new XElement("ComDeviceType",   ComDeviceType),
                        new XElement("ComTemplateName", ComTemplateName),

                        new XElement("WindowX",         WindowX),
                        new XElement("WindowY",         WindowY),
                        new XElement("WindowWidth",     WindowWidth),
                        new XElement("WindowHeight",    WindowHeight)
                    )
                );

                doc.Save(CurrentSettingsPath);
            }
            catch
            {
                // Best-effort save
            }
        }
    }

    // ── helpers (tolerant to missing elements) ──────────────────────────

    /// <summary>
    /// 2026-08-31 (owner decree): legacy → canonical migration, law «copy,
    /// ¬ move». When the canonical emule_config.xml is ABSENT and the legacy
    /// %LOCALAPPDATA%\...\settings.xml EXISTS, the legacy file is COPIED to
    /// the canonical path (overwrite:false — never clobbers) and kept in
    /// place. Best-effort and NEVER fatal: any IO failure simply means the
    /// load continues with defaults on the canonical path. Skipped entirely
    /// while <see cref="SettingsPathOverride"/> is set (tests never touch
    /// operator files). Caller holds <see cref="_lock"/>.
    /// </summary>
    private static void MigrateLegacyIfNeeded()
    {
        if (SettingsPathOverride is not null)
            return;
        if (File.Exists(SettingsPath) || !File.Exists(LegacySettingsPath))
            return;
        try
        {
            Directory.CreateDirectory(SettingsDir);
            InstallUsersModifyDacl(SettingsDir);
            File.Copy(LegacySettingsPath, SettingsPath, overwrite: false);
        }
        catch
        {
            // Best-effort migration — never fatal (decree: copy, ¬ move; the
            // legacy file stays, the load continues with defaults).
        }
    }

    /// <summary>
    /// BUILTIN\Users → Modify on the canonical ProgramData directory so the
    /// config inherits the grant (mirrors IniConfig.InstallUsersModifyDacl;
    /// the folder DACL law is the SAME for config.ini and emule_config.xml).
    /// </summary>
    private static void InstallUsersModifyDacl(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            var security = FileSystemAclExtensions.GetAccessControl(dirInfo);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(dirInfo, security);
        }
        catch
        {
            // Best-effort — a DACL failure must never break save/migration.
        }
    }

    private static string ReadStr(XElement root, string name, string def = "")
    {
        var el = root.Element(name);
        return el?.Value ?? def;
    }

    private static bool ReadBool(XElement root, string name, bool def = false)
    {
        var el = root.Element(name);
        if (el is null) return def;
        return bool.TryParse(el.Value, out var v) ? v : def;
    }

    private static int ReadInt(XElement root, string name, int def = 0)
    {
        var el = root.Element(name);
        if (el is null) return def;
        return int.TryParse(el.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    }

    private static uint ReadUInt(XElement root, string name, uint def = 0)
    {
        var el = root.Element(name);
        if (el is null) return def;
        var text = el.Value.Trim();
        // Accept "0x..." hex or plain decimal
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        if (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hv))
            return hv;
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv))
            return dv;
        return def;
    }
}
