using System.IO.MemoryMappedFiles;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// Test-side PRODUCER SURROGATE (INV-39, producer-owned bus — owner decree
/// 2026-08-27): production carries NO write machinery anymore, so the ONLY
/// write-capable code lives here, in the test project.
///
/// Two fixtures:
///   1. <see cref="BusTestFactory"/> — raw-byte file SEEDING: writes the exact
///      bus byte shapes (v5 identity header, CDAB body words, zone liveness
///      header blocks, legacy upgrade-source shapes) to the persisted file
///      BEFORE any reader opens. Plain FileStream(ReadWrite, ReadWrite) — no
///      mapping, no mutex (nothing else runs yet at seed time).
///   2. <see cref="SectionWriter"/> — a LIVE producer: opens the seeded file
///      ReadWrite and creates the named section with ReadWrite access. Must be
///      constructed BEFORE any BusReader opens (producer-first order, INV-26):
///      the section must be born ReadWrite — a section first created by a
///      READER is read-only and later writes would fail. All writes take the
///      bus mutex (contract §4 atomicity), mirroring the AddIn's write path.
/// </summary>
internal static class BusTestFactory
{
    /// <summary>
    /// Seed a VALID v5 bus file: identity header 'STNB', version 1,
    /// layoutVer 5, the given regCount, length 64 + 2×regCount, zero body.
    /// Default regCount = the current-default dictionary (10001 → 20066 B).
    /// </summary>
    public static void CreateFile(string path, ushort regCount = BusLayout.RegCount)
    {
        var buf = new byte[BusLayout.HeaderBytes + (long)regCount * BusLayout.BytesPerReg];
        BusLayout.MagicBytes.CopyTo(buf, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(buf, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(BusLayout.LayoutVersion).CopyTo(buf, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(regCount).CopyTo(buf, BusLayout.HeaderRegCountOffset);
        // Body: all zero (a fresh producer-created file shape).

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, buf);
    }

    /// <summary>
    /// Seed ANY legacy shape (upgrade source): same identity header but with
    /// the given layoutVer/regCount and length 64 + 2×regCount — e.g. the
    /// exact v2 650/1364 and v4-full 8404/16872 shapes.
    /// </summary>
    public static void CreateLegacyFile(string path, ushort layoutVer, ushort regCount)
    {
        var buf = new byte[BusLayout.HeaderBytes + (long)regCount * BusLayout.BytesPerReg];
        BusLayout.MagicBytes.CopyTo(buf, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(buf, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(layoutVer).CopyTo(buf, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(regCount).CopyTo(buf, BusLayout.HeaderRegCountOffset);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, buf);
    }

    /// <summary>Big-endian body word at BusLayout.BodyOffset + reg×2 (wire copy).</summary>
    public static void WriteWord(string path, int reg, ushort value)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Position = BusLayout.BodyOffset + reg * 2L;
        fs.WriteByte((byte)(value >> 8)); // body words are BIG-endian
        fs.WriteByte((byte)value);
    }

    /// <summary>CDAB float pair: 4 bytes [low.hi, low.lo, high.hi, high.lo] via CdabCodec.Split.</summary>
    public static void WriteFloat(string path, int reg, float value)
    {
        var (low, high) = CdabCodec.Split(value);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Position = BusLayout.BodyOffset + reg * 2L;
        fs.Write(new byte[] { (byte)(low >> 8), (byte)low, (byte)(high >> 8), (byte)high });
    }

    /// <summary>
    /// Zone liveness header block: LE u64 tick / u8 activeFlag / u32 write
    /// counter at the zone's header offsets (Lan block governs lan_stream_crio,
    /// D4 — the same block the AddIn producer writes).
    /// </summary>
    public static void WriteZoneHeader(string path, ZoneKind kind, long tick, bool active, uint count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Position = BusLayout.ZoneLastWriteTickOffset(kind);
        fs.Write(BitConverter.GetBytes(tick)); // u64 LE
        fs.Position = BusLayout.ZoneActiveFlagOffset(kind);
        fs.WriteByte(active ? (byte)1 : (byte)0);
        fs.Position = BusLayout.ZoneWriteCountOffset(kind);
        fs.Write(BitConverter.GetBytes(count)); // u32 LE
    }

    /// <summary>
    /// Fill body registers fromReg..toReg (inclusive) with a distinctive byte
    /// pattern — byte-identity probes for the read-only host (the reader must
    /// never rewrite a single seeded byte).
    /// </summary>
    public static void FillBody(string path, byte pattern, int fromReg, int toReg)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Position = BusLayout.BodyOffset + fromReg * 2L;
        var buf = new byte[(toReg - fromReg + 1) * 2];
        Array.Fill(buf, pattern);
        fs.Write(buf);
    }
}

/// <summary>
/// Test-side LIVE producer: holds a ReadWrite named section over the seeded
/// bus file so concurrent reader tests exercise the REAL production attach
/// path (BusMapping.OpenReadOnly → OpenExisting). Constructed BEFORE the
/// BusReader (producer-first order — a reader-created section is read-only
/// and later writes would fail). Every write is mutex-guarded.
/// </summary>
internal sealed class SectionWriter : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly Mutex _mutex;
    private readonly long _fileLength;
    private bool _disposed;

    public SectionWriter(string path, string mapName, string mutexName)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        _fileLength = fs.Length;
        _mmf = MemoryMappedFile.CreateFromFile(
            fs, mapName, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);
        _mutex = new Mutex(false, mutexName);
    }

    /// <summary>Big-endian body word at BusLayout.BodyOffset + reg×2, under the mutex.</summary>
    public void WriteWord(int reg, ushort value)
    {
        WithAccessor(acc =>
        {
            int off = BusLayout.BodyOffset + reg * 2;
            acc.Write(off, (byte)(value >> 8));
            acc.Write(off + 1, (byte)value);
        });
    }

    /// <summary>CDAB float pair (4 bytes), under the mutex.</summary>
    public void WriteFloat(int reg, float value)
    {
        var (low, high) = CdabCodec.Split(value);
        WithAccessor(acc =>
        {
            int off = BusLayout.BodyOffset + reg * 2;
            acc.Write(off, (byte)(low >> 8));
            acc.Write(off + 1, (byte)low);
            acc.Write(off + 2, (byte)(high >> 8));
            acc.Write(off + 3, (byte)high);
        });
    }

    /// <summary>Raw u16 words at absolute body addresses, one mutex round.</summary>
    public void WriteRawWords(params (int Addr, ushort Value)[] writes)
    {
        WithAccessor(acc =>
        {
            foreach (var (addr, value) in writes)
            {
                int off = BusLayout.BodyOffset + addr * 2;
                acc.Write(off, (byte)(value >> 8));
                acc.Write(off + 1, (byte)(value & 0xFF));
            }
        });
    }

    /// <summary>Zone liveness header block (tick u64 LE / flag u8 / counter u32 LE), under the mutex.</summary>
    public void WriteZoneHeader(ZoneKind kind, long tick, bool active, uint count)
    {
        WithAccessor(acc =>
        {
            acc.Write(BusLayout.ZoneLastWriteTickOffset(kind), tick);
            acc.Write(BusLayout.ZoneActiveFlagOffset(kind), active ? (byte)1 : (byte)0);
            acc.Write(BusLayout.ZoneWriteCountOffset(kind), count);
        });
    }

    private void WithAccessor(Action<MemoryMappedViewAccessor> write)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool acquired;
        try
        {
            acquired = _mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // previous owner died mid-write — proceed
        }

        try
        {
            using var acc = _mmf.CreateViewAccessor(0, _fileLength, MemoryMappedFileAccess.ReadWrite);
            write(acc);
        }
        finally
        {
            if (acquired)
                _mutex.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _mmf.Dispose();
        _mutex.Dispose();
    }
}
