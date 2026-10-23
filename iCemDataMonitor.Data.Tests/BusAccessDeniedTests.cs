using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// Bus file DACL sovereignty regression under the PRODUCER-OWNED BUS doctrine
/// (INV-39): the host reader opens the persisted mapping strictly READ-ONLY
/// (FileAccess.Read + Read views). DACL laws on the READ path:
///
///   1. INC-02 UAC win: the production Users=ReadAndExecute DACL (read allowed,
///      write denied) NO LONGER blocks the host — a READ-only open succeeds
///      under it.
///   2. TRUE read-denial (SYSTEM-only DACL) throws BusAccessDeniedException
///      ONCE — carrying path + ACL summary + remediation — then the reader
///      stays disabled (no 5 s watchdog re-throw loop).
///   3. CAPACITY-SKEW: a LIVE named section frozen at a smaller creation-time
///      capacity denies the sized READ probe with ACCESS DENIED (Windows
///      semantics) — the probe must fail TYPED with the ordered-restart
///      runbook, ¬ the file-ACL wording.
///
/// Limitation: the deep "existing section with clipped rights" probe path
/// (production stack: UnauthorizedAccessException inside MemoryMappedView.
/// CreateView) requires a section born under a DIFFERENT elevation, which a
/// single test process cannot manufacture. These tests reproduce the exact
/// PRODUCTION FILE ACL on the open-existing path, which deterministically
/// exercises the same open with the same typed exception.
/// </summary>
public class BusAccessDeniedTests : IDisposable
{
    private readonly string _dir;
    private readonly string _mmfPath;
    private readonly string _mapName;
    private readonly string _mutexName;

    public BusAccessDeniedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bus_acl_test", Guid.NewGuid().ToString("N"));
        _mmfPath = Path.Combine(_dir, "test_bus.dat");
        _mapName = $"iCemDataMonitorBusAclTestMap_{Guid.NewGuid():N}";
        _mutexName = $"iCemDataMonitorBusAclTestMutex_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ═══ 1. INC-02 UAC win: the production Users=ReadAndExecute DACL grants
    //        READ (write denied) — the READ-ONLY host reader opens and reads
    //        fine under it (the old writer paths were the ones that failed). ═══

    [Fact]
    public void Reader_OpenExisting_ReadExecuteAcl_OpensAndReadsFine()
    {
        if (!OperatingSystem.IsWindows())
            return; // DACL manipulation is Windows-only

        CreateBusFile(_mmfPath, regCount: BusLayout.RegCount);
        var original = RestrictToReadExecute(_mmfPath);

        try
        {
            using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
            Assert.True(reader.TryOpen(out var err), err);

            var snap = reader.Snapshot();
            Assert.NotNull(snap);
            Assert.Equal(BusLayout.Magic, snap.Magic);
            Assert.Equal(BusLayout.LayoutVersion, snap.LayoutVersion);
            Assert.Equal(BusLayout.RegCount, snap.RegCount);
        }
        finally
        {
            RestoreAcl(_mmfPath, original);
        }
    }

    // ═══ 2. TRUE read-denial (SYSTEM-only DACL) on the READER surface:
    //        throws typed ONCE, then clean-fails (Snapshot ⇒ null, no
    //        exception per poll tick). ═══

    [Fact]
    public void Reader_OpenExisting_DenyReadAcl_ThrowsTypedOnce_ThenCleanFail()
    {
        if (!OperatingSystem.IsWindows())
            return; // DACL manipulation is Windows-only

        CreateBusFile(_mmfPath, regCount: BusLayout.RegCount);
        var original = RestrictToSystemOnly(_mmfPath);

        try
        {
            using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
            var ex = Assert.Throws<BusAccessDeniedException>(() => reader.TryOpen(out _));

            Assert.Contains(_mmfPath, ex.Message);                       // full path
            Assert.Contains(WindowsIdentity.GetCurrent().Name, ex.Message); // current identity
            Assert.Contains("File ACL", ex.Message);                     // ACL summary present
            Assert.Contains("READ mapping", ex.Message);                 // read-denial wording (INV-39)
            Assert.Contains("Remediation", ex.Message);

            // Fail-fast: sticky disabled — no re-throw on later attempts.
            Assert.False(reader.TryOpen(out _));
            Assert.False(reader.IsOpen);
            Assert.Null(reader.Snapshot()); // clean fail, no exception per tick
        }
        finally
        {
            RestoreAcl(_mmfPath, original);
        }
    }

    [Fact]
    public void Acquisition_DenyReadAcl_PublishesStickyDiagnostic_WithoutThrowingHost()
    {
        if (!OperatingSystem.IsWindows()) return;
        CreateBusFile(_mmfPath, regCount: BusLayout.RegCount);
        var original = RestrictToSystemOnly(_mmfPath);
        try
        {
            using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
            using var acquisition = new BusAcquisitionService(reader, TimeSpan.FromMilliseconds(200));
            acquisition.CaptureOnce();
            var first = acquisition.Current;
            Assert.False(first.IsOpen);
            Assert.Null(first.Snapshot);
            Assert.Contains("Remediation", first.LastError!);
            Assert.Equal(0, first.SuccessfulCaptureSequence);
            acquisition.CaptureOnce();
            Assert.Equal(first.LastError, acquisition.Current.LastError);
            Assert.False(acquisition.Current.IsOpen);
            Assert.Equal(0, acquisition.Current.SuccessfulCaptureSequence);
        }
        finally { RestoreAcl(_mmfPath, original); }
    }

    // ═══ 3. CAPACITY-SKEW regression (production live proof, core-architect
    //        section probes): the LIVE named section keeps its creation-time
    //        capacity until EVERY handle closes. A producer that created the
    //        section at a smaller dictionary size outlives the file's on-disk
    //        dictionary, and Windows returns ACCESS DENIED (¬ "view too large")
    //        for views beyond the section capacity — so the sized READ probe
    //        must fail TYPED with the capacity-skew remediation (¬ the
    //        file-ACL wording, ¬ the raw exception). ═══

    [Fact]
    public void OpenReadOnly_LiveSectionSmallerCapacity_ThrowsCapacitySkewTyped()
    {
        if (!OperatingSystem.IsWindows())
            return; // named-section capacity semantics are Windows-only

        using var liveSection = CreateLiveSection(_mapName, sectionBytes: 1024);

        // The bus FILE on disk self-describes a larger dictionary than the
        // live section: regCount=1000 ⇒ 64 + 2×1000 = 2064 B — a VALID v5
        // shape (IsValidShape: 650 ≤ 1000 ≤ 65535, len == 64+2N), so the
        // read path reaches OpenReadOnly with requiredBytes=2064.
        CreateBusFile(_mmfPath, regCount: 1000);

        var ex = Assert.Throws<BusAccessDeniedException>(
            () => BusMapping.OpenReadOnly(_mmfPath, _mapName, requiredBytes: 2064));

        // Capacity-skew wording, NOT the file-ACL wording.
        Assert.Contains("file ACL is fine", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capacity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2064", ex.Message); // required size surfaced for the operator
        Assert.Contains("iCem.exe", ex.Message);
        Assert.Contains("start the producer FIRST", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("then the consumers", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete the file", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BUILTIN\\Users", ex.Message);
    }

    // ── Helpers ──

    /// <summary>
    /// A LIVE named section born from a small backing file: capacity is fixed
    /// at creation (sectionBytes) and survives the test's on-disk dictionary
    /// growth — the exact production skew (older-dictionary producer holding
    /// the section). Windows reports ACCESS DENIED for views beyond the
    /// section capacity, which reproduces the measured failure in-process.
    /// The caller keeps the returned handle alive for the test duration.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private MemoryMappedFile CreateLiveSection(string mapName, int sectionBytes)
    {
        Directory.CreateDirectory(_dir);
        var smallPath = Path.Combine(_dir, "small.dat");
        File.WriteAllBytes(smallPath, new byte[sectionBytes]);
        var fs = new FileStream(smallPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return MemoryMappedFile.CreateFromFile(
            fs, mapName, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);
    }

    /// <summary>A valid v5 bus file header self-describing the given regCount.</summary>
    private static void CreateBusFile(string path, ushort regCount)
    {
        var buf = new byte[BusLayout.HeaderBytes + (long)regCount * BusLayout.BytesPerReg];
        BusLayout.MagicBytes.CopyTo(buf, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(buf, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(BusLayout.LayoutVersion).CopyTo(buf, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(regCount).CopyTo(buf, BusLayout.HeaderRegCountOffset);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, buf);
    }

    /// <summary>
    /// Reproduce the EXACT production ACL (file ACL proof): the current identity
    /// gets ReadAndExecute+Synchronize ONLY — reads allowed, writes denied.
    /// Returns the original ACL for restore (the test process owns the file ⇒
    /// WRITE_DAC is implicit and restore always succeeds).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSecurity RestrictToReadExecute(string path)
    {
        var fileInfo = new FileInfo(path);
        var original = fileInfo.GetAccessControl();
        var restricted = fileInfo.GetAccessControl();
        restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        restricted.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            AccessControlType.Allow));
        fileInfo.SetAccessControl(restricted);
        return original;
    }

    /// <summary>
    /// TRUE read-denial: protected DACL with ONLY SYSTEM (FullControl) —
    /// every other identity (incl. the test process owner) is denied read.
    /// Returns the original ACL for restore (WRITE_DAC stays implicit for the
    /// owner, so restore and cleanup always succeed).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSecurity RestrictToSystemOnly(string path)
    {
        var fileInfo = new FileInfo(path);
        var original = fileInfo.GetAccessControl();
        var restricted = fileInfo.GetAccessControl();
        restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        restricted.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        fileInfo.SetAccessControl(restricted);
        return original;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestoreAcl(string path, FileSecurity original)
    {
        try { new FileInfo(path).SetAccessControl(original); } catch { /* best-effort */ }
    }
}
