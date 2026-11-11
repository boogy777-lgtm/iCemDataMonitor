using iCemDataMonitor.Host.Tray;
using Microsoft.Win32;

namespace iCemDataMonitor.Host.Tests;

// ═══════════════════════════════════════════════════════════════════════════
// 2026-08-27 SetAutostart RPC backing store: HKCU Run-key truth + toggle.
// EVERY test uses a THROWAWAY HKCU subkey + value name (injectable seam) —
// the real HKCU\Software\Microsoft\Windows\CurrentVersion\Run key is NEVER
// touched. A contract fact additionally pins the value name/format against
// the ps1 scripts the installer/operator use.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class AutostartManagerTests : IDisposable
{
    private readonly string _keyPath =
        $@"Software\iCemDataMonitor.Host.Tests\autostart_{Guid.NewGuid():N}";
    private readonly string _approvedKeyPath =
        $@"Software\iCemDataMonitor.Host.Tests\approved_{Guid.NewGuid():N}";
    private readonly string _valueName = $"val_{Guid.NewGuid():N}";

    private AutostartManager NewManager(string? exePath = null) =>
        new(runKeyPath: _keyPath, startupApprovedKeyPath: _approvedKeyPath,
            valueName: _valueName,
            exePath: exePath ?? @"C:\deploy\iCemDataMonitor.Host.exe");

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false); }
        catch { /* best effort */ }
        try { Registry.CurrentUser.DeleteSubKeyTree(_approvedKeyPath, throwOnMissingSubKey: false); }
        catch { /* best effort */ }
    }

    [Fact]
    public void ExpectedValueData_MatchesScripts_ExactQuoteShape()
    {
        // The RPC writes the SAME value data as scripts\add-autostart.ps1 /
        // tools\install-autostart.ps1: '"' + <exe> + '" --autostart'.
        var m = NewManager();
        Assert.Equal("\"C:\\deploy\\iCemDataMonitor.Host.exe\" --autostart", m.ExpectedValueData);
    }

    [Fact]
    public void DefaultValueName_IsICemDataMonitorHost()
    {
        // Value name MUST match both ps1 scripts (pinned here as a contract).
        Assert.Equal("iCemDataMonitorHost", AutostartManager.DefaultValueName);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", AutostartManager.DefaultRunKeyPath);
    }

    [Fact]
    public void IsEnabled_MissingValue_IsFalse()
    {
        Assert.False(NewManager().IsEnabled());
    }

    [Fact]
    public void SetEnabled_True_WritesValue_IsEnabled_BecomesTrue()
    {
        var m = NewManager();
        m.SetEnabled(true);

        Assert.True(m.IsEnabled());
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.Equal(m.ExpectedValueData, key?.GetValue(_valueName) as string);
    }

    [Fact]
    public void SetEnabled_False_RemovesValue_IsEnabled_BecomesFalse()
    {
        var m = NewManager();
        m.SetEnabled(true);
        m.SetEnabled(false);

        Assert.False(m.IsEnabled());
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.Null(key?.GetValue(_valueName));
    }

    [Fact]
    public void SetEnabled_False_WhenAbsent_IsNoOp()
    {
        var m = NewManager();
        m.SetEnabled(false); // must NOT throw
        Assert.False(m.IsEnabled());
    }

    [Fact]
    public void IsEnabled_StaleValueData_IsHonestFalse()
    {
        // A value that exists but points at a DIFFERENT exe (re-deployed
        // build) must report false — logon would not start THIS host.
        var m = NewManager();
        m.SetEnabled(true);
        var redeployed = NewManager(@"C:\deploy\v2\iCemDataMonitor.Host.exe");

        Assert.False(redeployed.IsEnabled());
    }

    [Fact]
    public void IsEnabled_ReadsRegistryAtCallTime_NoCache()
    {
        // Sensor-at-send: toggling the registry externally must be visible
        // on the NEXT probe (no cached projection inside the manager).
        var m = NewManager();
        Assert.False(m.IsEnabled());
        m.SetEnabled(true);
        Assert.True(m.IsEnabled());
        m.SetEnabled(false);
        Assert.False(m.IsEnabled());
    }

    // ── 2026-08-28 TASK MANAGER LAW (owner: «выключил в диспетчере задач —
    // галочка в WinUI должна снова РЕАЛЬНО включить») ─────────────────────────

    [Fact]
    public void SetEnabled_True_ClearsStaleTaskManagerDisabledFlag()
    {
        // A person disabled the entry in Task Manager: Windows keeps a
        // 12-byte "disabled" flag (03 ...) under StartupApproved\Run. That
        // flag SURVIVES Run-value recreation — without the cleanup the
        // checkbox would silently stay off.
        var disabledFlag = new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        using (var k = Registry.CurrentUser.CreateSubKey(_approvedKeyPath, writable: true))
        {
            k.SetValue(_valueName, disabledFlag, RegistryValueKind.Binary);
        }

        var m = NewManager();
        m.SetEnabled(true);

        Assert.True(m.IsEnabled());
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath);
        Assert.Null(approved?.GetValue(_valueName)); // stale flag CLEARED
    }

    [Fact]
    public void SetEnabled_True_NoFlagPresent_IsCleanNoOp()
    {
        var m = NewManager();
        m.SetEnabled(true); // must NOT throw when StartupApproved has no flag
        Assert.True(m.IsEnabled());
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath);
        Assert.Null(approved?.GetValue(_valueName));
    }

    [Fact]
    public void SetEnabled_False_LeavesStartupApprovedFlag_Unchanged()
    {
        // Disable removes ONLY the Run value; the flag (if any) is inert
        // once the Run value is gone — the entry leaves the Task Manager
        // list. The MSI RemoveRegistryValue handles the uninstall cleanup.
        // (The flag is created AFTER the enable: SetEnabled(true) legitimately
        // clears any PRE-EXISTING flag — that is the stale-flag cleanup.)
        var m = NewManager();
        m.SetEnabled(true); // clean slate — clears any stale flag

        var enabledFlag = new byte[] { 0x02, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        using (var k = Registry.CurrentUser.CreateSubKey(_approvedKeyPath, writable: true))
        {
            k.SetValue(_valueName, enabledFlag, RegistryValueKind.Binary);
        }

        m.SetEnabled(false);

        Assert.False(m.IsEnabled());
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath);
        var kept = approved?.GetValue(_valueName) as byte[];
        Assert.NotNull(kept); // NOT touched by the disable path
        Assert.Equal(0x02, kept![0]);
    }
}
