using System.Globalization;
using Microsoft.Win32;

namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// 2026-08-27: HKCU Run-key autostart truth + toggle behind the SetAutostart
/// RPC. The host is a USER console process forever (rollback law §5 — Windows
/// Service REJECTED), so autostart = the HKCU Run key, value name
/// <see cref="DefaultValueName"/>, value data "<host exe path>" + the
/// autostart flag. The value name and the exact data shape are THE SAME as
/// scripts\add-autostart.ps1 and tools\install-autostart.ps1 — the RPC and the
/// scripts must never drift.
///
/// TASK MANAGER LAW (owner request 2026-08-28: «хочу видеть в диспетчере
/// задач, чтобы человек мог просто выключить из автозагрузки»): HKCU Run
/// entries appear in Task Manager → Startup apps and toggle WITHOUT admin.
/// Trap handled here: when a person disables the entry in Task Manager,
/// Windows keeps a 12-byte "disabled" flag under
/// HKCU\...\Explorer\StartupApproved\Run EVEN IF the Run value is later
/// recreated — the checkbox would then silently stay off. SetEnabled(true)
/// therefore CLEARS the stale flag: a fresh enable is always a REAL enable.
///
/// TRUTH LAW: <see cref="IsEnabled"/> reads the registry AT CALL TIME
/// (sensor-at-send) — GetStatus/SetAutostart responses always carry the real
/// current key state, never a cached projection.
///
/// Test seam (injectable, §15.7(a) data seam): run-key PATH + StartupApproved
/// key PATH + value NAME + exe path are ctor parameters. Unit tests use
/// throwaway HKCU subkeys and value names and NEVER touch the real
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run key.
/// </summary>
public sealed class AutostartManager
{
    /// <summary>HKCU Run key subpath (the canonical Windows logon-autostart key).</summary>
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>HKCU key where Task Manager stores per-entry enabled/disabled
    /// flags (12-byte binary: 0x02 = enabled, 0x03 = disabled). Absence of the
    /// value = enabled.</summary>
    public const string DefaultStartupApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Run-key value name — MUST match scripts\add-autostart.ps1 and
    /// tools\install-autostart.ps1 (pinned by contract test).</summary>
    public const string DefaultValueName = "iCemDataMonitorHost";

    private readonly string _runKeyPath;
    private readonly string _startupApprovedKeyPath;
    private readonly string _valueName;
    private readonly string _exePath;

    /// <param name="runKeyPath">HKCU-relative key path (tests inject a throwaway).</param>
    /// <param name="startupApprovedKeyPath">HKCU-relative Task Manager flag key
    /// (tests inject a throwaway).</param>
    /// <param name="valueName">Run value name (tests inject a throwaway).</param>
    /// <param name="exePath">Host exe path used inside the value data
    /// (tests inject a fake; production = Environment.ProcessPath).</param>
    public AutostartManager(
        string? runKeyPath = null,
        string? startupApprovedKeyPath = null,
        string? valueName = null,
        string? exePath = null)
    {
        _runKeyPath = runKeyPath ?? DefaultRunKeyPath;
        _startupApprovedKeyPath = startupApprovedKeyPath ?? DefaultStartupApprovedKeyPath;
        _valueName = valueName ?? DefaultValueName;
        _exePath = exePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is null — cannot build autostart value data");
    }

    /// <summary>The exact value data the Run key must carry:
    /// "<exe path>" --autostart (verbatim quote shape of both ps1 scripts).</summary>
    public string ExpectedValueData =>
        $"\"{_exePath}\" --autostart";

    /// <summary>TRUTH probe: the Run value exists AND carries the exact
    /// expected data. A stale value (old exe path after a re-deploy) is honest
    /// FALSE — it would not start this build of the host at logon.</summary>
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        var data = key?.GetValue(_valueName) as string;
        return string.Equals(data, ExpectedValueData, StringComparison.Ordinal);
    }

    /// <summary>Enable/disable logon autostart. Enable writes the exact
    /// "<exe>" + autostart-flag value AND clears any stale Task Manager
    /// "disabled" flag (StartupApproved — see the TASK MANAGER LAW above);
    /// disable removes the Run value (a leftover StartupApproved flag is
    /// harmless once the Run value is gone — the entry leaves the Task
    /// Manager list). The KEYS are never deleted (system keys; a throwaway
    /// test subkey is the test's own to clean up).</summary>
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(_valueName, ExpectedValueData, RegistryValueKind.String);
            key.Close();
            // Clear a stale Task Manager "disabled" flag, if any: the flag
            // SURVIVES Run-value recreation and would silently keep the
            // fresh enable off. No flag present = enabled — delete is a no-op.
            using var approved = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath, writable: true);
            approved?.DeleteValue(_valueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }
}
