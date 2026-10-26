using System.IO.MemoryMappedFiles;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Data.Bus;

/// <summary>
/// BCL-only READ-ONLY open for the PERSISTED named bus mapping.
///
/// PRODUCER-OWNED BUS (owner decree 2026-08-27, INV-39): the DLL AddIn is the
/// ONLY writer of Data.dat. This library opens the file strictly READ-ONLY —
/// FileAccess.Read + Read views — and NEVER creates the backing file, NEVER
/// migrates legacy layouts (v2–v4 → v5 is producer-owned: AddIn BusWriter.Open
/// upgrades under the bus mutex before its first write), NEVER writes a byte.
/// The former OpenOrCreate/UpgradeIfNeeded paths are REMOVED.
///
/// SECTION-CREATION LAW (2026-08-28 stand incident, owner-confirmed edge
/// case «host запустился первым, чем iCem»): the reader NEVER creates the
/// named section. Windows births a named section with the protection of its
/// FIRST creator — a section created by this reader would be born READ-ONLY,
/// and the producer's later ReadWrite attach then dies with
/// ERROR_INVALID_PARAMETER ("The parameter is incorrect") forever — the
/// BusWriter "open failed — writer disabled" retry loop. Therefore:
///   1. attach to the PRODUCER's section via OpenExisting (Read rights on a
///      ReadWrite section are always legal);
///   2. while no section exists (host before iCem), fall back to an UNNAMED
///      file mapping — the name stays unclaimed, the producer creates its own
///      section whenever it starts, and the next host open attaches to it.
/// Startup order no longer matters; the producer-side section-capacity law
/// (INV-26) is untouched.
/// </summary>
internal static class BusMapping
{
    /// <summary>Header self-description of a bus file (dynamic-regCount law: shape = runtime data).</summary>
    public readonly record struct BusFileShape(
        uint Magic, ushort Version, ushort LayoutVer, ushort RegCount, long FileLength);

    /// <summary>
    /// Open the existing bus file READ-ONLY: attach to the producer's named
    /// section when it exists, otherwise map the file UNNAMED (never claiming
    /// the section name — see SECTION-CREATION LAW). The sized READ probe
    /// surfaces capacity-skew (a live section frozen at a smaller creation-time
    /// capacity denies views beyond it — Windows reports ACCESS DENIED) with
    /// the typed ordered-restart remediation. A file-level read denial surfaces
    /// the typed file-ACL error. Never creates a named section, never creates
    /// the file, never writes a byte.
    /// </summary>
    public static MemoryMappedFile OpenReadOnly(string path, string mapName, long requiredBytes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "the station bus mapping requires Windows named sections");

        MemoryMappedFile mmf;
        var attached = TryAttachToProducerSection(mapName, out mmf);
        if (!attached)
        {
            FileStream fs;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (UnauthorizedAccessException ex)
            {
                // File-level read denial: the DACL (installed by a producer in a
                // foreign security context/account) does not grant read. Typed,
                // actionable — once, at open.
                throw BusAccessDeniedException.ForPath(path, ex);
            }

            // mapName: NULL — the UNNAMED fallback. Creating the named section
            // here would birth it READ-ONLY and poison the name for the
            // producer's future ReadWrite attach (SECTION-CREATION LAW).
            mmf = MemoryMappedFile.CreateFromFile(
                fs, mapName: null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: false);
        }

        // Sized READ probe, sized to the CALLER's required view: a live named
        // section keeps its creation-time capacity until EVERY handle closes,
        // and Windows reports ACCESS DENIED for views beyond that capacity.
        // Probing ONCE here surfaces the failure at open with the
        // ordered-restart remediation, instead of per-snapshot inside the poll
        // loop. (For READ views a failure here is capacity-skew: a section
        // whose DACL denied read would already have failed OpenExisting.)
        try
        {
            using var probe = mmf.CreateViewAccessor(0, requiredBytes, MemoryMappedFileAccess.Read);
            return mmf;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
        {
            mmf.Dispose();
            throw BusAccessDeniedException.ForSectionCapacity(path, requiredBytes, ex);
        }
    }

    /// <summary>Attach to the producer-owned named section with Read rights.
    /// Returns false when the section does not exist YET (host started before
    /// iCem — the producer creates its own section on its schedule). A section
    /// whose DACL denies read surfaces the typed file-ACL error.</summary>
    private static bool TryAttachToProducerSection(string mapName, out MemoryMappedFile mmf)
    {
        // 2026-09-02 (warning hygiene, CA1416): OpenExisting with rights is
        // Windows-only; the analyzer cannot see the product-wide Windows-only
        // law from here. An explicit guard silences CA1416 WITHOUT a cascading
        // [SupportedOSPlatform] attribute up the call chain.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Named MMF sections with ACL rights are Windows-only.");
        }
        try
        {
            mmf = MemoryMappedFile.OpenExisting(
                mapName, MemoryMappedFileRights.Read, HandleInheritability.None);
            return true;
        }
        catch (FileNotFoundException)
        {
            // No producer section yet — the unnamed fallback owns the read.
            mmf = null!;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Section exists but its DACL denies read → typed, actionable.
            throw BusAccessDeniedException.ForPath(mapName, ex);
        }
    }

    /// <summary>
    /// Read the header self-description (magic, version, layoutVer, regCount)
    /// plus the on-disk length. regCount is RUNTIME DATA — layers size their
    /// views from it (dynamic-regCount law v1.3.0), ¬ from compile-time consts.
    /// Validation-only (INV-39): the host never migrates.
    /// </summary>
    public static BusFileShape ReadShape(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long len = fs.Length;
        Span<byte> head = stackalloc byte[10];
        fs.ReadExactly(head);
        return new BusFileShape(
            Magic: BitConverter.ToUInt32(head),
            Version: BitConverter.ToUInt16(head[4..]),
            LayoutVer: BitConverter.ToUInt16(head[6..]),
            RegCount: BitConverter.ToUInt16(head[8..]),
            FileLength: len);
    }
}
