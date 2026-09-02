using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// PRODUCER-OWNED BUS falsifiers (INV-39, owner decree 2026-08-27): the host
/// BusReader is strictly READ-ONLY — it NEVER creates the file, NEVER migrates
/// legacy layouts (v2–v4 → v5 is producer-owned: the AddIn expands the file),
/// NEVER writes a single byte.
///
/// INV-13 (legacy-shape gate): every exact legacy upgrade-source shape
/// (v2 650/1364, v4-full 8404/16872) fails cleanly with the producer-migration
/// error, the file byte-identical and length-identical afterwards.
/// INV-25 (read-side immutability): open + snapshot + dispose across the whole
/// reader lifecycle leaves EVERY seeded byte untouched — including the
/// ace_full reserve (600..10000) and the AddIn heartbeat cell (reg 3).
/// Missing-file law: the reader must not create the producer's file.
///
/// Session-local map/mutex names per test — no cross-process interference.
/// </summary>
public class BusReaderReadOnlyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _mmfPath;
    private readonly string _mapName;
    private readonly string _mutexName;

    public BusReaderReadOnlyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bus_readonly_test", Guid.NewGuid().ToString("N"));
        _mmfPath = Path.Combine(_dir, "test_bus.dat");
        _mapName = $"iCemDataMonitorBusReadOnlyMap_{Guid.NewGuid():N}";
        _mutexName = $"iCemDataMonitorBusReadOnlyMutex_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ═══ 1. Legacy upgrade sources ⇒ honest producer-migration fail, file
    //        byte- and length-identical (the reader NEVER migrates). ═══

    [Fact]
    public void LegacyV2File_ReaderFailsClean_FileUntouched()
    {
        // Exact v2 upgrade source: layoutVer=2, regCount=650, 1364 B.
        BusTestFactory.CreateLegacyFile(_mmfPath, layoutVer: BusLayout.LayoutVersionV2, regCount: BusLayout.RegCountV2);
        BusTestFactory.FillBody(_mmfPath, pattern: 0xB5, fromReg: 0, toReg: BusLayout.RegCountV2 - 1);

        var before = File.ReadAllBytes(_mmfPath);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out var error));
        Assert.NotNull(error);
        Assert.Contains("producer migration", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start iCem once", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(reader.IsOpen);

        var after = File.ReadAllBytes(_mmfPath);
        Assert.Equal(BusLayout.TotalFileBytesV2, after.Length);      // length unchanged
        Assert.Equal(before, after);                                  // bytes unchanged
    }

    [Fact]
    public void LegacyV4FullFile_ReaderFailsClean_FileUntouched()
    {
        // Exact v4-full upgrade source: layoutVer=4, regCount=8404, 16872 B.
        BusTestFactory.CreateLegacyFile(_mmfPath, layoutVer: BusLayout.LayoutVersionV4, regCount: BusLayout.RegCountV4Full);
        BusTestFactory.FillBody(_mmfPath, pattern: 0xC7, fromReg: 0, toReg: BusLayout.RegCountV4Full - 1);

        var before = File.ReadAllBytes(_mmfPath);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out var error));
        Assert.NotNull(error);
        Assert.Contains("producer migration", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start iCem once", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(reader.IsOpen);

        var after = File.ReadAllBytes(_mmfPath);
        Assert.Equal(BusLayout.TotalFileBytesV4Full, after.Length);  // length unchanged
        Assert.Equal(before, after);                                  // bytes unchanged
    }

    // ═══ 2. Read-side immutability: open + snapshot + dispose ⇒ the file is
    //        byte-identical (the reader writes NOTHING, ever). ═══

    [Fact]
    public void OpenSnapshot_NeverModifiesFileBytes()
    {
        BusTestFactory.CreateFile(_mmfPath);
        // Every body byte non-zero and distinctive — any reader-side rewrite
        // (zero-fill, reshape, migration) would destroy this fingerprint.
        BusTestFactory.FillBody(_mmfPath, pattern: 0x7D, fromReg: 0, toReg: BusLayout.RegCount - 1);

        var before = File.ReadAllBytes(_mmfPath);

        using (var reader = new BusReader(_mmfPath, _mapName, _mutexName))
        {
            Assert.True(reader.TryOpen(out var err), err);
            var snap = reader.Snapshot();
            Assert.NotNull(snap);
            Assert.Equal(BusLayout.RegCount, snap.Words.Length);
        }

        var after = File.ReadAllBytes(_mmfPath);
        Assert.Equal(BusLayout.TotalFileBytes, after.Length); // 20066 — never reshaped
        Assert.Equal(before, after);                           // byte-identical
    }

    // ═══ 3. Missing-file law: the reader NEVER creates the producer's file. ═══

    [Fact]
    public void MissingFile_ReaderNeverCreatesIt()
    {
        Assert.False(File.Exists(_mmfPath));

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out _));

        Assert.False(File.Exists(_mmfPath), "a READ-ONLY consumer must never create the producer's bus file");
    }

    // ═══ 4. ace_full reserve + heartbeat: the host never wrote anything, ever —
    //        seeded cells survive the full open/snapshot/dispose/reopen cycle
    //        byte-exact (stale-hold law, read-side). ═══

    [Fact]
    public void AceFullReserve_NoHostWriter_NoWrites()
    {
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, BusLayout.AceFullBaseReg, 15.79f);  // 600 — reserve start
        BusTestFactory.WriteFloat(_mmfPath, 5500, -42.0f);                      // mid-reserve
        BusTestFactory.WriteFloat(_mmfPath, BusLayout.AceFullEndReg - 1, 1.0f); // 9999 — top pair
        BusTestFactory.WriteWord(_mmfPath, BusLayout.RegHeartbeat, 0x1234);     // AddIn heartbeat cell

        byte[] seeded600 = ReadBytes(BusLayout.AceFullBaseReg, 4);
        byte[] seeded5500 = ReadBytes(5500, 4);
        byte[] seeded9999 = ReadBytes(BusLayout.AceFullEndReg - 1, 4);
        byte[] seededHeartbeat = ReadBytes(BusLayout.RegHeartbeat, 2);

        using (var reader = new BusReader(_mmfPath, _mapName, _mutexName))
        {
            Assert.True(reader.TryOpen(out var err), err);
            var snap = reader.Snapshot();
            Assert.NotNull(snap);

            // Decode sanity: the seeded floats read back exactly.
            var reserved = snap.GetZoneFloats(BusLayout.AceFullBaseReg, BusLayout.AceFullRegCount);
            Assert.Equal(15.79f, reserved[0], 3);
            Assert.Equal(-42.0f, reserved[(5500 - BusLayout.AceFullBaseReg) / 2], 3);
        }

        // Reopen (repeat-open law) and snapshot again — still no writes.
        using (var reader = new BusReader(_mmfPath, _mapName, _mutexName))
        {
            Assert.True(reader.TryOpen(out var err), err);
            Assert.NotNull(reader.Snapshot());
        }

        Assert.Equal(seeded600, ReadBytes(BusLayout.AceFullBaseReg, 4));
        Assert.Equal(seeded5500, ReadBytes(5500, 4));
        Assert.Equal(seeded9999, ReadBytes(BusLayout.AceFullEndReg - 1, 4));
        Assert.Equal(seededHeartbeat, ReadBytes(BusLayout.RegHeartbeat, 2));
    }

    private byte[] ReadBytes(int reg, int count)
    {
        var buf = new byte[count];
        using var fs = new FileStream(_mmfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = BusLayout.BodyOffset + reg * 2L;
        fs.ReadExactly(buf);
        return buf;
    }
}
