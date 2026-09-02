using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

/// <summary>
/// Operator-class DACL sovereignty regression (production root cause,
/// DESKTOP-Q429S0I): the bus is a cross-process contract — the CREATOR owns
/// the birth rights of Data.dat. An elevated producer must NOT pass its
/// ambient ACL to the bus file: without BUILTIN\Users → Modify every
/// non-elevated consumer is denied ReadWrite views forever. The writer must
/// (a) grant Users→Modify on freshly created files, (b) repair the grant on
/// every successful open (self-heal of already-poisoned files), and
/// (c) install an inherited Users→Modify ACE on the backing directory so an
/// out-of-band creator can no longer poison the bus. Mirrors the server-dotnet
/// BusAccessDeniedTests pattern (RestrictToReadExecute). Static overrides ⇒
/// sequential execution (see TestAssembly).
/// </summary>
public class BusWriterDaclTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public BusWriterDaclTests()
    {
        // Unique SUBDIRECTORY per test: the writer installs the inherited
        // Users→Modify ACE on the parent dir — never touch shared %TEMP%.
        // Unique file + unique SECTION/MUTEX names per test: Windows section
        // names are process-global (same law as BusWriterTests).
        string id = Guid.NewGuid().ToString("N");
        _dir = Path.Combine(Path.GetTempPath(), "icemdatamonitorbus_dacl_" + id);
        _path = Path.Combine(_dir, "Data.dat");
        BusWriter.BackingPathOverride = _path;
        BusWriter.BusNameOverride = @"Global\iCemDataMonitorBus_test_" + id;
        BusWriter.MutexNameOverride = @"Global\iCemDataMonitorBusMutex_test_" + id;
    }

    public void Dispose()
    {
        BusWriter.BackingPathOverride = null;
        BusWriter.BusNameOverride = null;
        BusWriter.MutexNameOverride = null;
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch
        {
            // temp cleanup best-effort
        }
    }

    // ═══ 1. Birth contract: a FRESHLY CREATED bus file carries Users→Modify ═══

    [Fact]
    public void Open_CreatesFile_WithUsersModifyDacl()
    {
        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk, "fresh create must open OK");
        }

        Assert.True(HasUsersModify(_path),
            "freshly created bus file must carry BUILTIN\\Users→Modify " +
            "(the named section inherits the FILE's DACL at CreateFromFile time)");
    }

    // ═══ 2. Self-heal: an open on a file MISSING the grant repairs it ═══

    [Fact]
    public void Open_FileWithoutUsersGrant_RepairsUsersModifyDacl()
    {
        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk);
        }
        StripUsersRules(_path);                       // simulate legacy poisoning
        Assert.False(HasUsersModify(_path));          // poisoned precondition

        using (var w2 = BusWriter.Open())
        {
            Assert.True(w2.IsOk, "writer must still open after repair");
        }
        Assert.True(HasUsersModify(_path), "every successful open must repair Users→Modify");
    }

    // ═══ 3. Full production reproduction: Users=ReadAndExecute ONLY (elevated
    //        creator's ambient ACL). The repair runs BEFORE the ReadWrite stream
    //        open, so the writer must STILL open OK and leave the file healed. ═══

    [Fact]
    public void Open_ProductionPoisonedFile_HealsAndOpens()
    {
        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk);
        }
        FileSecurity original = RestrictToReadExecute(_path);
        try
        {
            Assert.False(HasUsersModify(_path));      // exact production ACL shape

            using (var w2 = BusWriter.Open())
            {
                Assert.True(w2.IsOk,
                    "DACL repair precedes the ReadWrite stream open ⇒ writer opens despite poisoned file");
            }
            Assert.True(HasUsersModify(_path), "poisoned file must be healed by the open");
        }
        finally
        {
            RestoreAcl(_path, original);
        }
    }

    // ═══ 4. Defense-in-depth: the backing DIRECTORY gets an INHERITABLE
    //        Users→Modify ACE — any out-of-band creator inherits the grant. ═══

    [Fact]
    public void Open_InstallsInheritedUsersModifyOnDirectory()
    {
        using (var w = BusWriter.Open())
        {
            Assert.True(w.IsOk);
        }

        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        DirectorySecurity security = Directory.GetAccessControl(_dir);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(usersSid) &&
                (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify &&
                rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit))
            {
                return; // found: inheritable operator-class grant present
            }
        }
        Assert.Fail("bus directory must carry an inheritable BUILTIN\\Users→Modify ACE");
    }

    // ═══ helpers ═══

    /// <summary>True when an Allow ACE grants BUILTIN\Users Modify (or more).</summary>
    private static bool HasUsersModify(string path)
    {
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        FileSecurity security = new FileInfo(path).GetAccessControl();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(usersSid) &&
                (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Replace the DACL with a protected owner-only grant (no Users
    /// rules, explicit OR inherited) — simulates a file created outside the
    /// operator-class contract while keeping the test process able to open it.</summary>
    private static void StripUsersRules(string path)
    {
        var fileInfo = new FileInfo(path);
        var locked = new FileSecurity();
        locked.SetAccessRuleProtection(true, false);
        locked.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        fileInfo.SetAccessControl(locked);
    }

    /// <summary>
    /// Reproduce the EXACT production ACL: FRESH protected DACL (existing rules
    /// discarded — incl. any Users→Modify a previous open installed), current
    /// identity gets ReadAndExecute+Synchronize ONLY. Returns the original ACL
    /// for restore (the test process owns the file ⇒ WRITE_DAC is implicit and
    /// restore always succeeds).
    /// </summary>
    private static FileSecurity RestrictToReadExecute(string path)
    {
        var fileInfo = new FileInfo(path);
        FileSecurity original = fileInfo.GetAccessControl();
        var restricted = new FileSecurity();
        restricted.SetAccessRuleProtection(true, false);
        restricted.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            AccessControlType.Allow));
        fileInfo.SetAccessControl(restricted);
        return original;
    }

    private static void RestoreAcl(string path, FileSecurity original)
    {
        try { new FileInfo(path).SetAccessControl(original); } catch { /* best-effort */ }
    }
}
