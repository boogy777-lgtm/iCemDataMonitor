using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Core.Config;

/// <summary>
/// Fail-fast exception for config.ini violations. The message lists EVERY
/// violation (TZ_ARCH_V2 §6: "¬ молчаливый дефолт" — no silent defaults).
/// </summary>
public sealed class IniConfigException : Exception
{
    public IReadOnlyList<string> Violations { get; }

    public IniConfigException(IReadOnlyList<string> violations)
        : base(BuildMessage(violations))
        => Violations = violations;

    private static string BuildMessage(IReadOnlyList<string> violations) =>
        $"config.ini invalid — {violations.Count} violation(s):" +
        Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", violations);
}

/// <summary>
/// config.ini contract (TZ_ARCH_V2 §6, schema = iCemDataMonitor.Sdk.Bus.IniOptions).
/// BCL-only parser: sections [station]/[zones], line-based key=value, ';'/'#'
/// comments. The ini file is the projection source — NOT a serializer target.
///
/// Semantics (operator requirements, all pinned here):
///   • CANONICAL file (owner decree 2026-08-28, MSI migration to
///     C:\ProgramData\Halliburton\iCem\DataMonitoring — the exe folder and the
///     Data.dat folder CONVERGED there): ProgramData\...\config.ini.
///   • SEARCH ORDER (Load): 1) canonical config.ini; 2) LEGACY canonical-folder
///     station.ini (the owner's live file); 3) LEGACY next-to-exe config.ini;
///     4) LEGACY next-to-exe station.ini (dev); 5) nothing ⇒ CREATE canonical
///     with EXACT IniOptions defaults (single source of truth: DefaultContent
///     is rendered FROM the defaults record).
///   • MIGRATION (rename law): a file loaded from a LEGACY path is best-effort
///     COPIED to the canonical config.ini (File.Copy overwrite:false) and the
///     canonical copy continues as the active file. A failed copy (IO) is
///     NEVER fatal — the load honestly continues on the legacy path and the
///     event is recorded in <see cref="LastLoadNote"/> (evidence, ¬ молчание).
///   • read ONCE at startup — hot-reload FORBIDDEN (change ⇒ restart)
///   • invalid ⇒ fail-fast: strict parse (unknown key / duplicate key / missing
///     key / non-numeric value / malformed line are violations) + full
///     IniOptions.Validate() — every violation listed in ONE exception
/// </summary>
public static class IniConfig
{
    public const string FileName = "config.ini"; // canonical name (renamed from station.ini, owner decree 2026-08-28)
    public const string LegacyFileName = "station.ini";
    public const string SectionStation = "station";
    public const string SectionZones = "zones";

    // OPTIONAL keys: absent ⇒ default, never a "missing key" violation.
    //   • ui_set_as_default — operator v2 files predate it (default 1).
    //   • web_on / web_remote_on — web layer RESTORED 2026-08-27 (the owner
    //     decree «Web - удали (контрол sim)» meant removing the sim control from
    //     the dashboard page, NOT the page). The tombstone build deployed
    //     2026-08-27 18:57 wrote config files WITHOUT these keys; the strict
    //     "missing key" rule would make such a live file fatal on the next
    //     start — migration law (a live stand file must never become fatal) ⇒
    //     both are optional. web_on absent ⇒ 1 (owner decree 2026-08-28:
    //     «по дефолту web должен быть включён» — the old «absent ⇒ 0» rule is
    //     superseded; the dashboard may only be GAINED by an old file, never
    //     lost); web_remote_on absent ⇒ 0 (loopback fail-safe).
    // Every other key stays strict (missing ⇒ violation).
    // 2026-08-27: emule_on remains RETIRED (the emulator is an independent
    // app) — it is ACCEPTED but IGNORED so the operator's existing station.ini
    // keeps loading across the upgrade. Any OTHER unknown key remains a
    // fail-fast violation.
    // 2026-08-31 (owner decree): com_port and the [zones] com/lan/raw base+len
    // keys are DEAD — parsed/validated/re-rendered with ZERO consumers (the
    // AddIn never reads the ini; runtime zone geometry is compile-time
    // BusLayout.*BaseReg — TelemetryServiceImpl.cs / RegisterDecoder.cs).
    // They left the schema entirely and are RETIRED here: accepted-and-ignored
    // so the live stand config.ini keeps loading (migration law — a live
    // stand file must never become fatal). The writer never re-emits them.
    private static readonly HashSet<string> OptionalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        $"{SectionStation}/ui_set_as_default",
        $"{SectionStation}/web_on",
        $"{SectionStation}/web_remote_on",
    };

    private static readonly HashSet<string> RetiredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        $"{SectionStation}/emule_on",
        $"{SectionStation}/com_port",
        $"{SectionZones}/com_base",
        $"{SectionZones}/com_len",
        $"{SectionZones}/lan_base",
        $"{SectionZones}/lan_len",
        $"{SectionZones}/raw_base",
        $"{SectionZones}/raw_len",
    };

    private enum ValueKind { Int, Text }

    // schema: section → (key → kind). Closed set — new key = SchemaΔ + spec update.
    private static readonly Dictionary<string, Dictionary<string, ValueKind>> Schema = new(StringComparer.OrdinalIgnoreCase)
    {
        [SectionStation] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["modbus_on"] = ValueKind.Int,
            ["web_on"] = ValueKind.Int,
            ["opcda_on"] = ValueKind.Int,
            ["lansniffer_on"] = ValueKind.Int,
            ["ui_set_as_default"] = ValueKind.Int,
            ["web_remote_on"] = ValueKind.Int,
            ["modbus_port"] = ValueKind.Int,
            ["modbus_tick_ms"] = ValueKind.Int,
            ["web_port"] = ValueKind.Int,
            ["mmf_name"] = ValueKind.Text,
            ["mmf_path"] = ValueKind.Text,
        },
        [SectionZones] = new(StringComparer.OrdinalIgnoreCase)
        {
            // 2026-08-31: com/lan/raw base+len REMOVED (dead keys, owner
            // decree) — wits/lan_ext stay (v4-compat inert BY DESIGN), total stays.
            ["wits_base"] = ValueKind.Int,
            ["wits_len"] = ValueKind.Int,
            ["lan_ext_base"] = ValueKind.Int,
            ["lan_ext_len"] = ValueKind.Int,
            ["total"] = ValueKind.Int,
        },
    };

    // ── Path law (owner decree 2026-08-28: MSI migrates to ProgramData, the
    //    station file is RENAMED config.ini and lives next to the binary —
    //    after the migration the exe folder, the Data.dat folder and the
    //    canonical ini folder are THE SAME ProgramData directory). ──────────

    /// <summary>Canonical directory: %ProgramData%\Halliburton\iCem\DataMonitoring
    /// (CommonApplicationData, ¬ a hardcoded C:\ — the standard folder is the law;
    /// on a default Windows install this resolves to C:\ProgramData).</summary>
    public static string ProgramDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Halliburton", "iCem", "DataMonitoring");

    /// <summary>CANONICAL file: config.ini in the canonical directory — the same
    /// folder as Data.dat and (post-MSI-migration) the binary. Load order #1,
    /// the creation location, and the write target of the save-after-load law.</summary>
    public static string ProgramDataPath => Path.Combine(ProgramDataDirectory, FileName);

    /// <summary>LEGACY #2: the owner's live station.ini in the canonical folder
    /// (predates the rename). Auto-migrated to <see cref="ProgramDataPath"/> on load.</summary>
    public static string LegacyProgramDataStationIniPath => Path.Combine(ProgramDataDirectory, LegacyFileName);

    /// <summary>LEGACY #3: next-to-exe config.ini (pre-migration deployments that
    /// already carried the new name next to the binary).</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>LEGACY #4: next-to-exe station.ini (the original dev-layout file).</summary>
    public static string LegacyExeStationIniPath => Path.Combine(AppContext.BaseDirectory, LegacyFileName);

    /// <summary>Ordered legacy candidate list (search positions 2..4) — for UI-shell
    /// diagnostics; the canonical path is <see cref="ProgramDataPath"/>.</summary>
    public static IReadOnlyList<string> LegacyPaths => new[]
    {
        LegacyProgramDataStationIniPath, // #2 canonical-folder station.ini
        DefaultPath,                     // #3 next-to-exe config.ini
        LegacyExeStationIniPath,         // #4 next-to-exe station.ini (dev)
    };

    // Tracks the path that Load() actually used so Save(IniOptions) writes back
    // to the file it was read from (write-to-read-source law, D.4.3).
    private static string? _lastLoadedPath;

    /// <summary>The ACTIVE file: the path the last Load() actually read from /
    /// migrated to (canonical after a successful migration, legacy on a failed
    /// copy). LayerRuntimeController's runtime persists MUST target this path —
    /// writing anywhere else silently loses toggles (P2 lesson, 2026-08-28:
    /// the old code persisted to next-to-exe while the load came from ProgramData).
    /// Falls back to the canonical path before any load happened.</summary>
    public static string ActivePath => _lastLoadedPath ?? ProgramDataPath;

    /// <summary>
    /// Non-fatal migration evidence of the last <see cref="Load()"/>: a rename
    /// copy result («legacy → canonical») or an honest failure note («copy
    /// FAILED ... continuing on the legacy path»). Null when the last load used
    /// the canonical file directly. The host emits this on the startup log.
    /// </summary>
    public static string? LastLoadNote { get; private set; }

    // ═══════════════════════════════════════════════════════════════════════════
    // RUNTIME SAVE (W6 tray toggles — 2026-08-18). The ini stays the projection
    // source: the writer is a LINE-REWRITER, not a serializer. It replaces ONLY
    // schema-known key=value lines in-place (comments, ordering, section layout
    // and any unrelated lines survive verbatim) and verifies the result through
    // the SAME strict parser the startup path uses — a written file can never
    // break the fail-fast re-parse guarantee.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Save options back to the file they were last loaded from
    /// (<see cref="ActivePath"/>). Falls back to the canonical
    /// <see cref="ProgramDataPath"/> when no load has happened in this process
    /// (D.4.3 write-to-read-source law).
    /// </summary>
    public static void Save(IniOptions options)
    {
        string path = _lastLoadedPath ?? ProgramDataPath;
        Save(path, options);
    }

    /// <summary>Save options to an explicit path (fail-safe, temp-then-replace).</summary>
    public static void Save(string path, IniOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        // 1. Contract gate: refuse to persist a record that would not pass the
        //    startup validator (fail-fast semantics unchanged).
        var violations = IniOptions.Validate(options);
        if (violations.Count > 0)
            throw new InvalidOperationException(
                "Refusing to save invalid config.ini options: " + string.Join("; ", violations));

        // 2. Line-rewrite over the CURRENT file text (or the default template).
        string existing = File.Exists(path) ? File.ReadAllText(path) : DefaultContent;
        string rewritten = Rewrite(existing, options);

        // 3. Strict re-parse gate: the exact parser the startup path uses must
        //    accept the result and yield the identical options record. On any
        //    mismatch the original file remains untouched.
        IniOptions reparsed;
        try
        {
            reparsed = Parse(rewritten);
        }
        catch (IniConfigException ex)
        {
            throw new InvalidOperationException(
                "Ini writer produced a file that fails strict re-parse — original untouched: " + ex.Message, ex);
        }
        if (reparsed != options)
            throw new InvalidOperationException(
                "Ini writer produced a file whose re-parsed values differ from the requested options — original untouched.");

        // 4. Temp-write + atomic replace (never truncate the target in place).
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string tmp = path + ".tmp" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(tmp, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); } // NTFS atomic replace
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    File.Move(tmp, path, overwrite: true); // same-volume atomic fallback
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Rewrite schema-known key=value lines in-place; everything else (comments,
    /// blank lines, section headers, unknown lines) survives verbatim. Keys the
    /// file does not carry (hand-edited file that still passed strict parse is
    /// impossible by definition — defensive) are appended to their section.
    /// </summary>
    private static string Rewrite(string text, IniOptions o)
    {
        var render = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["station/modbus_on"] = o.ModbusOn.ToString(CultureInfo.InvariantCulture),
            ["station/web_on"] = o.WebOn.ToString(CultureInfo.InvariantCulture),
            ["station/opcda_on"] = o.OpcdaOn.ToString(CultureInfo.InvariantCulture),
            ["station/lansniffer_on"] = o.LansnifferOn.ToString(CultureInfo.InvariantCulture),
            ["station/ui_set_as_default"] = o.UiSetAsDefault.ToString(CultureInfo.InvariantCulture),
            ["station/web_remote_on"] = o.WebRemoteOn.ToString(CultureInfo.InvariantCulture),
            ["station/modbus_port"] = o.ModbusPort.ToString(CultureInfo.InvariantCulture),
            ["station/modbus_tick_ms"] = o.ModbusTickMs.ToString(CultureInfo.InvariantCulture),
            ["station/web_port"] = o.WebPort.ToString(CultureInfo.InvariantCulture),
            ["station/mmf_name"] = o.MmfName,
            ["station/mmf_path"] = o.MmfPath,
            ["zones/wits_base"] = o.WitsBase.ToString(CultureInfo.InvariantCulture),
            ["zones/wits_len"] = o.WitsLen.ToString(CultureInfo.InvariantCulture),
            ["zones/lan_ext_base"] = o.LanExtBase.ToString(CultureInfo.InvariantCulture),
            ["zones/lan_ext_len"] = o.LanExtLen.ToString(CultureInfo.InvariantCulture),
            ["zones/total"] = o.Total.ToString(CultureInfo.InvariantCulture),
        };

        var sb = new StringBuilder(text.Length + 64);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string currentSection = "";

        var rawLines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < rawLines.Length; i++)
        {
            var raw = rawLines[i];
            bool last = i == rawLines.Length - 1;
            var line = raw.Trim();
            int prefixLen = raw.Length - raw.TrimStart().Length;

            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                sb.Append(raw);
                if (!last) sb.AppendLine();
                continue;
            }

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close >= 0)
                    currentSection = line.Substring(1, close - 1).Trim();
                sb.Append(raw);
                if (!last) sb.AppendLine();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                var key = line.Substring(0, eq).Trim();
                var fullKey = $"{currentSection}/{key}";
                if (render.TryGetValue(fullKey, out var value))
                {
                    seen.Add(fullKey);
                    sb.Append(raw, 0, prefixLen).Append(key).Append('=').Append(value);
                    if (!last) sb.AppendLine();
                    continue;
                }
            }

            sb.Append(raw);
            if (!last) sb.AppendLine();
        }

        // Defensive append: any schema key the file did not carry lands at the
        // end of its section (schema order). A strict-parseable file always
        // carries every key, so this path is inert in practice.
        string? appendedSection = null;
        foreach (var (section, sectionSchema) in Schema)
        {
            foreach (var key in sectionSchema.Keys)
            {
                var fullKey = $"{section}/{key}";
                if (seen.Contains(fullKey) || !render.TryGetValue(fullKey, out var value))
                    continue;
                if (appendedSection != section)
                {
                    sb.AppendLine().Append('[').Append(section).Append(']').AppendLine();
                    appendedSection = section;
                }
                sb.Append(key).Append('=').Append(value).AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Explicit DACL on a newly created ProgramData directory: BUILTIN\Users → Modify
    /// with object/container inheritance so the config.ini file and any future
    /// contents inherit the grant. Matches the bus-file ACL precedent
    /// (precedent: the producer AddIn's BusWriter.InstallUsersModifyDacl).
    /// </summary>
    private static void InstallUsersModifyDacl(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
            return;

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

    /// <summary>
    /// Exact default file content, rendered from the IniOptions defaults record —
    /// the record is the single source of truth; the file mirrors it verbatim.
    /// </summary>
    public static string DefaultContent { get; } = RenderDefault();

    /// <summary>
    /// Load using the 2026-08-28 search order + rename-migration law:
    ///   1. <see cref="ProgramDataPath"/> (canonical config.ini)
    ///   2. <see cref="LegacyProgramDataStationIniPath"/> (canonical-folder station.ini)
    ///   3. <see cref="DefaultPath"/> (next-to-exe config.ini)
    ///   4. <see cref="LegacyExeStationIniPath"/> (next-to-exe station.ini, dev)
    ///   5. none ⇒ create the canonical config.ini with defaults (web_on=1).
    /// A LEGACY winner is best-effort copied to the canonical path (rename law)
    /// and the canonical copy becomes the active file; a FAILED copy (IO) is
    /// never fatal — the legacy path stays active and <see cref="LastLoadNote"/>
    /// carries the evidence line.
    /// </summary>
    public static IniOptions Load()
    {
        string winner = SelectIniPath(ProgramDataPath, LegacyProgramDataStationIniPath, DefaultPath, LegacyExeStationIniPath);
        string active = MigrateToCanonical(winner, ProgramDataPath, out var note);
        LastLoadNote = note;
        _lastLoadedPath = active;
        return Load(active);
    }

    /// <summary>
    /// The search-order + migration decision table as a pure function over
    /// injected candidate paths: canonical wins when present; then the three
    /// legacy candidates in order; when NOTHING exists the canonical path is
    /// the creation location. Internal = data-seam for tests (§15.7 whitelist
    /// (a)): paths are DATA, the branch table is the production code under
    /// test — unit tests never touch the real ProgramData.
    /// </summary>
    internal static string SelectIniPath(
        string canonicalPath,
        string legacyCanonicalStationIni,
        string legacyExeConfigIni,
        string legacyExeStationIni)
    {
        if (File.Exists(canonicalPath))
            return canonicalPath;
        if (File.Exists(legacyCanonicalStationIni))
            return legacyCanonicalStationIni;
        if (File.Exists(legacyExeConfigIni))
            return legacyExeConfigIni;
        if (File.Exists(legacyExeStationIni))
            return legacyExeStationIni;
        return canonicalPath; // creation location
    }

    /// <summary>
    /// Rename-migration step (owner decree 2026-08-28): a LEGACY winner is
    /// copied to the canonical config.ini (File.Copy overwrite:false — never
    /// clobbers) and the CANONICAL copy becomes the active path. A canonical
    /// winner (or the nothing-exists creation location) passes through
    /// unchanged. A failed copy (IO/unauthorized) NEVER throws: the legacy
    /// path stays active and the note records the honest evidence.
    /// Internal test seam (§15.7(a)): both paths are injected data — unit
    /// tests never touch the real ProgramData.
    /// </summary>
    internal static string MigrateToCanonical(string winnerPath, string canonicalPath, out string? note)
    {
        note = null;
        if (winnerPath == canonicalPath)
            return winnerPath; // canonical winner / creation location — nothing to migrate

        // Race guard: if a canonical file appeared between the search and the
        // migration, the canonical file wins (search order re-evaluated).
        if (File.Exists(canonicalPath))
            return canonicalPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath)!);
            File.Copy(winnerPath, canonicalPath, overwrite: false);
            note = $"config migration: {winnerPath} → {canonicalPath} (legacy file kept in place)";
            return canonicalPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // NEVER fatal (migration law): the legacy file keeps serving and the
            // failure is EVIDENCE, not silence — the host logs LastLoadNote.
            note = $"config migration FAILED ({ex.Message}) — continuing on the legacy path {winnerPath}";
            return winnerPath;
        }
    }

    /// <summary>Internal test seam for the search order: load from an explicit candidate path.</summary>
    internal static IniOptions LoadWithPath(string path) => Load(path);

    /// <summary>
    /// Load from an explicit path. Absent file ⇒ create with DefaultContent and
    /// return the default record (valid by construction, pinned in iCemDataMonitor.Sdk.Tests).
    /// Present file ⇒ strict parse + validate; any violation ⇒ IniConfigException.
    /// </summary>
    public static IniOptions Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                // On first creation of the ProgramData directory, grant BUILTIN\Users
                // Modify so operator-class non-elevated processes can write the ini.
                // (Precedent: producer BusWriter.InstallUsersModifyDacl, D.4.3)
                InstallUsersModifyDacl(dir);
            }
            // UTF-8 WITH BOM: the header comments carry em-dash (—) characters; no-BOM
            // emission renders as mojibake in ANSI editors. Parser stays tolerant of both
            // (File.ReadAllText auto-detects BOM).
            File.WriteAllText(path, DefaultContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _lastLoadedPath = path;
            return new IniOptions();
        }

        _lastLoadedPath = path;
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Strict parse + full validation. Throws IniConfigException listing every
    /// violation: syntax, unknown/duplicate/missing keys, non-numeric values,
    /// then IniOptions.Validate (flags, ports, zone geometry, overlap, total).
    /// </summary>
    public static IniOptions Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var violations = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string currentSection = "";
        using var reader = new StringReader(text);
        string? raw;
        int lineNo = 0;

        while ((raw = reader.ReadLine()) != null)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close < 0)
                {
                    violations.Add($"line {lineNo}: malformed section header '{line}'");
                    currentSection = "";
                    continue;
                }
                currentSection = line.Substring(1, close - 1).Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                violations.Add($"line {lineNo}: malformed line '{line}' (expected key=value)");
                continue;
            }

            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();

            if (currentSection.Length == 0)
            {
                violations.Add($"line {lineNo}: key '{key}' outside any section");
                continue;
            }
            if (!Schema.TryGetValue(currentSection, out var sectionSchema))
            {
                violations.Add($"line {lineNo}: unknown section [{currentSection}]");
                continue;
            }
            if (!sectionSchema.TryGetValue(key, out var kind))
            {
                if (RetiredKeys.Contains($"{currentSection}/{key}"))
                    continue; // retired key — accepted and ignored (see RetiredKeys)
                violations.Add($"line {lineNo}: unknown key '{key}' in [{currentSection}]");
                continue;
            }

            var fullKey = $"{currentSection}/{key}";
            if (values.ContainsKey(fullKey))
                violations.Add($"line {lineNo}: duplicate key '{key}' in [{currentSection}]");
            values[fullKey] = value;
        }

        // Missing keys (strict: a PRESENT file must carry the full schema —
        // except the OPTIONAL keys, which default when absent: ui_set_as_default
        // (operator v2 files predate it), web_on/web_remote_on (files written
        // by the tombstone build deployed 2026-08-27 18:57 predate the restored
        // web layer — migration law: a live stand file must never become fatal;
        // web_on absent ⇒ 1 per the 2026-08-28 decree «web включён из коробки»).
        foreach (var (section, sectionSchema) in Schema)
            foreach (var key in sectionSchema.Keys)
                if (!values.ContainsKey($"{section}/{key}") && !OptionalKeys.Contains($"{section}/{key}"))
                    violations.Add($"missing key '{key}' in [{section}]");

        var options = new IniOptions(
            ModbusOn: GetInt(SectionStation, "modbus_on", values, violations),
            OpcdaOn: GetInt(SectionStation, "opcda_on", values, violations),
            LansnifferOn: GetInt(SectionStation, "lansniffer_on", values, violations),
            ModbusPort: GetInt(SectionStation, "modbus_port", values, violations),
            ModbusTickMs: GetInt(SectionStation, "modbus_tick_ms", values, violations),
            WebPort: GetInt(SectionStation, "web_port", values, violations),
            MmfName: GetText(SectionStation, "mmf_name", values),
            MmfPath: GetText(SectionStation, "mmf_path", values),
            WitsBase: GetInt(SectionZones, "wits_base", values, violations),
            WitsLen: GetInt(SectionZones, "wits_len", values, violations),
            LanExtBase: GetInt(SectionZones, "lan_ext_base", values, violations),
            LanExtLen: GetInt(SectionZones, "lan_ext_len", values, violations),
            Total: GetInt(SectionZones, "total", values, violations),
            UiSetAsDefault: GetIntOrDefault(SectionStation, "ui_set_as_default", values, violations, 1),
            WebOn: GetIntOrDefault(SectionStation, "web_on", values, violations, IniOptions.DefaultWebOn),
            WebRemoteOn: GetIntOrDefault(SectionStation, "web_remote_on", values, violations, 0));

        if (violations.Count == 0)
        {
            var contractViolations = IniOptions.Validate(options);
            if (contractViolations.Count > 0)
                violations.AddRange(contractViolations);
        }

        if (violations.Count > 0)
            throw new IniConfigException(violations);

        return options;
    }

    private static int GetInt(string section, string key, Dictionary<string, string> values, List<string> violations)
    {
        if (!values.TryGetValue($"{section}/{key}", out var raw))
            return 0; // missing — already reported
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;
        violations.Add($"[{section}] {key}: not an integer: '{raw}'");
        return 0;
    }

    private static int GetIntOrDefault(string section, string key, Dictionary<string, string> values,
        List<string> violations, int fallback)
    {
        if (!values.TryGetValue($"{section}/{key}", out var raw))
            return fallback; // optional key absent — no violation
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;
        violations.Add($"[{section}] {key}: not an integer: '{raw}'");
        return fallback;
    }

    private static string GetText(string section, string key, Dictionary<string, string> values) =>
        values.TryGetValue($"{section}/{key}", out var raw) ? raw : "";

    private static string RenderDefault()
    {
        var d = new IniOptions();
        var sb = new StringBuilder();
        sb.AppendLine("; config.ini — iCemDataMonitor station configuration (TZ_ARCH_V2 §6)");
        sb.AppendLine("; Canonical name config.ini (renamed from station.ini, owner decree 2026-08-28).");
        sb.AppendLine("; Legacy station.ini files are auto-migrated (copied) here on load — never deleted.");
        sb.AppendLine("; Read ONCE at startup — edit ⇒ restart the POC. Hot-reload FORBIDDEN (operator requirement).");
        sb.AppendLine("; Invalid values ⇒ fail-fast at startup, every violation logged (¬ silent defaults).");
        sb.AppendLine("; Runtime zone geometry is compile-time BusLayout (2026-08-31: the dead com/lan/raw");
        sb.AppendLine("; ini zone keys were removed — only wits/lan_ext (v4-compat inert) + total remain).");
        sb.AppendLine();
        sb.AppendLine($"[{SectionStation}]");
        sb.AppendLine($"modbus_on={d.ModbusOn}");
        sb.AppendLine($"web_on={d.WebOn}");
        sb.AppendLine($"opcda_on={d.OpcdaOn}");
        sb.AppendLine($"lansniffer_on={d.LansnifferOn}");
        sb.AppendLine($"ui_set_as_default={d.UiSetAsDefault}");
        sb.AppendLine($"web_remote_on={d.WebRemoteOn}");
        sb.AppendLine($"modbus_port={d.ModbusPort}");
        sb.AppendLine($"modbus_tick_ms={d.ModbusTickMs}");
        sb.AppendLine($"web_port={d.WebPort}");
        sb.AppendLine($"mmf_name={d.MmfName}");
        sb.AppendLine($"mmf_path={d.MmfPath}");
        sb.AppendLine();
        sb.AppendLine($"[{SectionZones}]");
        sb.AppendLine("; 2026-08-31 (owner decree): com/lan/raw base+len REMOVED — dead keys,");
        sb.AppendLine("; runtime zone geometry is compile-time BusLayout. wits/lan_ext stay");
        sb.AppendLine("; (v4-compat inert BY DESIGN); total stays (dynamic-regCount law).");
        sb.AppendLine($"wits_base={d.WitsBase}");
        sb.AppendLine($"wits_len={d.WitsLen}");
        sb.AppendLine($"lan_ext_base={d.LanExtBase}");
        sb.AppendLine($"lan_ext_len={d.LanExtLen}");
        sb.AppendLine($"total={d.Total}");
        return sb.ToString();
    }
}
