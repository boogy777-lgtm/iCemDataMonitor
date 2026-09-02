using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// BusReader contract tests against a REAL temp persisted MMF: CDAB roundtrip,
/// snapshot consistency under a concurrent LIVE producer (SectionWriter — the
/// test-side surrogate for the AddIn), stale-hold, clean-fail on missing file,
/// mutex-timeout last-good semantics.
///
/// PRODUCER-OWNED BUS (INV-39): the reader is strictly READ-ONLY. The bus file
/// is seeded raw-byte via BusTestFactory (test-side PRODUCER SURROGATE), and
/// live-write tests construct the SectionWriter BEFORE the reader opens
/// (producer-first order — the named section must be born ReadWrite).
/// Session-local map/mutex names per test — no cross-process interference.
/// </summary>
public class BusReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _mmfPath;
    private readonly string _mapName;
    private readonly string _mutexName;

    public BusReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bus_test", Guid.NewGuid().ToString("N"));
        _mmfPath = Path.Combine(_dir, "test_bus.dat");
        _mapName = $"iCemDataMonitorBusTestMap_{Guid.NewGuid():N}";
        _mutexName = $"iCemDataMonitorBusTestMutex_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ═══ 1. Producer-surrogate seeds the file → reader round-trips CDAB floats ═══

    [Fact]
    public void SeededFile_ReaderRoundtripsCdabFloats()
    {
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 1.0f);     // lan zone base — pins [0x0000, 0x3F80]
        BusTestFactory.WriteFloat(_mmfPath, 202, 15.79f);   // pins [0xA3D7, 0x417C]
        BusTestFactory.WriteFloat(_mmfPath, 204, -42.0f);
        var tick = DateTime.UtcNow.Ticks;
        BusTestFactory.WriteZoneHeader(_mmfPath, ZoneKind.Lan, tick, active: true, count: 1);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var rerr), rerr);

        var snap = reader.Snapshot();
        Assert.NotNull(snap);
        Assert.False(snap.Stale);

        // Header identity (LE parse).
        Assert.Equal(BusLayout.Magic, snap.Magic);
        Assert.Equal(BusLayout.Version, snap.Version);
        Assert.Equal(BusLayout.RegCount, snap.RegCount);

        // Body words big-endian — CDAB pin (CONTRACT_BusV2 §4).
        Assert.Equal(0x0000, snap.Words[200]);
        Assert.Equal(0x3F80, snap.Words[201]);
        Assert.Equal(0xA3D7, snap.Words[202]);
        Assert.Equal(0x417C, snap.Words[203]);

        // Zone decode via CdabCodec.
        var lan = snap.GetZoneFloats(ZoneKind.Lan);
        Assert.Equal(125, lan.Length);
        Assert.Equal(1.0f, lan[0]);
        Assert.Equal(15.79f, lan[1], 3);
        Assert.Equal(-42.0f, lan[2]);

        // Zone header block: active + tick (byte-exact) + counter.
        var zone = snap.Lan;
        Assert.True(zone.Active);
        Assert.Equal(tick, zone.LastWriteTicks);
        Assert.Equal(1u, zone.WriteCount);

        // Reader accessors.
        Assert.Equal(0x0000, reader.GetRegister(200));
        var floats = reader.GetZoneFloats(ZoneKind.Lan);
        Assert.NotNull(floats);
        Assert.Equal(15.79f, floats[1], 3);

        // LIVE state regs (v5 map, 4 regs) are AddIn-owned — a plain
        // float seeder never touches them; all read zero on a fresh file.
        Assert.Equal(0, snap.ComPort);
        Assert.Equal(0, snap.CaseState);
        Assert.Equal(0, snap.CaseLanState);
        Assert.Equal(0, snap.Heartbeat);

        // FROZEN v4 surface, honestly pinned: reg 8 is a com_stream float-pair
        // low word in v5 — the RAW word reads zero on a fresh file and is NEVER
        // surfaced as LAN state (case_lan_state = header reg 2, test below).
        Assert.Equal(0, snap.GetRegister(BusLayout.RegLanStatus));
    }

    // ═══ 1b. case_lan_state = header reg 2 (BusLayout.RegCaseLanState) — NOT ═══
    //        the v4 reg-8 surface (a com_stream float-pair low word in v5). ═══

    [Fact]
    public void CaseLanState_ReadsHeaderReg2_NotTheReg8FloatPair()
    {
        BusTestFactory.CreateFile(_mmfPath);
        // Header state reg 2 = caseLanState (tap-exposed value, e.g. "Streaming").
        BusTestFactory.WriteWord(_mmfPath, BusLayout.RegCaseLanState, 0x0102);
        // regs 8..9 = com_stream float pair idx 2 — the v4 LanStatus surface;
        // in v5 this is a FLOAT payload whose low word must never leak as state.
        var (low, high) = CdabCodec.Split(123.5f);
        Assert.NotEqual(0x0102, low); // the pair genuinely disagrees with reg 2
        BusTestFactory.WriteWord(_mmfPath, 8, low);
        BusTestFactory.WriteWord(_mmfPath, 9, high);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var err), err);
        var snap = reader.Snapshot();
        Assert.NotNull(snap);

        // THE HONEST PIN: case_lan_state comes from reg 2.
        Assert.Equal(0x0102, snap.CaseLanState);
        // Reg 8 stays a raw float-pair word — a data address, not a state surface.
        Assert.Equal(low, snap.GetRegister(8));
        Assert.Equal(high, snap.GetRegister(9));
        // And the pair decodes as the float it is (state-law view: garbage as LAN state).
        Assert.Equal(123.5f, snap.GetZoneFloats(ZoneKind.ComMain)[2], 3); // com pair idx 2 = regs 8..9
    }

    // ═══ 2. Missing file ⇒ clean fail (CLI exit-code-1 path, AC8) ═══

    [Fact]
    public void MissingFile_TryOpenFailsClean()
    {
        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out var error));
        Assert.NotNull(error);
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(reader.IsOpen);
        Assert.Null(reader.Snapshot()); // no last-good ever taken
    }

    // ═══ 3. Stale-hold: producer dead ⇒ values persist in the file ═══

    [Fact]
    public void StaleHold_ValuesPersistAfterWriterDisposed()
    {
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 12.5f);
        BusTestFactory.WriteZoneHeader(_mmfPath, ZoneKind.Lan, DateTime.UtcNow.Ticks, active: true, count: 1);

        // A NEW reader instance maps the same persisted file (POC restart, AC3/AC4).
        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var err), err);
        var snap = reader.Snapshot();
        Assert.NotNull(snap);

        var lan = snap.GetZoneFloats(ZoneKind.Lan);
        Assert.Equal(12.5f, lan[0]);
        Assert.True(snap.Lan.Active); // flag persists — zone data is stale but intact

        // Age of the lan zone is measurable and growing (stale metric, UTC ticks).
        var age1 = snap.Lan.AgeMs();
        Thread.Sleep(50);
        var age2 = snap.Lan.AgeMs();
        Assert.True(age2 >= age1);
    }

    // ═══ 4. Hammer: concurrent LIVE producer + 1000 snapshots ⇒ zero torn floats ═══

    [Fact]
    public async Task SnapshotConsistency_ConcurrentWriter_NoTornFloats()
    {
        BusTestFactory.CreateFile(_mmfPath);
        // PRODUCER-FIRST (INV-26): the named section must be born ReadWrite —
        // create the live writer BEFORE the reader attaches (read-only).
        using var writer = new SectionWriter(_mmfPath, _mapName, _mutexName);

        var written = new HashSet<float> { 1.0f, 2.5f, 15.79f, -42.0f, 123456.7f };
        var values = written.ToArray();

        using var cts = new CancellationTokenSource();
        var writerTask = Task.Run(() =>
        {
            int i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                writer.WriteFloat(200, values[i % values.Length]);
                i++;
            }
        });

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out _));

        try
        {
            // Warm-up: wait until the writer's first pair lands (file starts all-zero;
            // a pre-write snapshot would decode 0.0f and false-fail the torn check).
            var warmDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < warmDeadline)
            {
                var warm = reader.Snapshot();
                if (warm is not null && warm.GetZoneFloats(ZoneKind.Lan)[0] != 0f)
                    break;
                Thread.Sleep(1);
            }

            for (int n = 0; n < 1000; n++)
            {
                var snap = reader.Snapshot();
                Assert.NotNull(snap);
                float f = snap.GetZoneFloats(ZoneKind.Lan)[0];
                Assert.True(written.Contains(f),
                    $"Torn float decoded: {f} (writer never wrote it)");
            }
        }
        finally
        {
            cts.Cancel();
            await writerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    // ═══ 4b. Dispose-race law: concurrent Snapshot × Dispose ⇒ no exception
    //         ever escapes; every call returns last-good/stale/null. ═══

    [Fact]
    public async Task Dispose_ConcurrentSnapshots_NoExceptionEscapes()
    {
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 9.5f);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var rerr), rerr);
        Assert.NotNull(reader.Snapshot()); // seed last-good

        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var cts = new CancellationTokenSource();
        var pollers = Enumerable.Range(0, 4).Select(__ => Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                    _ = reader.Snapshot(); // must NEVER throw
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        })).ToArray();

        // Dispose while pollers are mid-Snapshot, then re-open (repeat-open law)
        // and dispose again — both transitions race the pollers.
        for (int round = 0; round < 20 && errors.IsEmpty; round++)
        {
            reader.Dispose();
            if (!reader.TryOpen(out _))
                break;
        }
        reader.Dispose();
        cts.Cancel();
        await Task.WhenAll(pollers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(errors.IsEmpty,
            "Snapshot must degrade to last-good under a concurrent Dispose, got: " +
            string.Join(" | ", errors.Select(e => e.Message)));
    }

    // ═══ 4c. Open-gate law: concurrent FIRST opens on one instance must not
    //         leak a duplicate MMF/mutex pair — all callers see one open reader. ═══

    [Fact]
    public async Task ConcurrentFirstOpen_SingleInstanceOpen_AllSnapshotsValid()
    {
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 4.25f);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);

        var results = Enumerable.Range(0, 8).Select(__ => Task.Run(
            () => reader.Snapshot())).ToArray();

        foreach (var t in await Task.WhenAll(results).WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.NotNull(t);
            Assert.Equal(4.25f, t.GetZoneFloats(ZoneKind.Lan)[0]);
        }
        Assert.True(reader.IsOpen);
    }

    // ═══ 5. Mutex timeout ⇒ last-good snapshot + Stale flag ═══

    [Fact]
    public async Task MutexTimeout_ReturnsLastGood_StaleFlag()
    {
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 7.0f);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out _));

        // Seed the last-good snapshot.
        var good = reader.Snapshot();
        Assert.NotNull(good);
        Assert.False(good.Stale);
        Assert.Equal(7.0f, good.GetZoneFloats(ZoneKind.Lan)[0]);

        // Hold the mutex longer than the reader's 500 ms timeout from a DEDICATED
        // holder thread (Mutex ownership is per-thread ⇒ the snapshot thread is
        // guaranteed not to be the owner).
        using var gate = new ManualResetEventSlim(false);
        var holderTask = Task.Run(() =>
        {
            using var holder = new Mutex(false, _mutexName);
            holder.WaitOne();
            gate.Set();
            Thread.Sleep(1500); // hold > 500 ms
            holder.ReleaseMutex(); // REQUIRED: Dispose without Release leaves the
                                   // kernel mutex owned by the (alive) pool thread forever
        });
        gate.Wait();

        try
        {
            var stale = reader.Snapshot(); // test thread ≠ owner ⇒ WaitOne(500) must time out
            Assert.NotNull(stale);
            Assert.True(stale.Stale, "Snapshot must be flagged stale when the mutex times out");
            Assert.Equal(good.CapturedAtTickMs, stale.CapturedAtTickMs); // last-good copy, not fresh
            Assert.Equal(7.0f, stale.GetZoneFloats(ZoneKind.Lan)[0]);
        }
        finally
        {
            await holderTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Mutex released ⇒ fresh snapshot again.
        var fresh = reader.Snapshot();
        Assert.NotNull(fresh);
        Assert.False(fresh.Stale);
    }

    // ═══ 6. Shape gate (dynamic-regCount law): v1 DEAD ⇒ fail-safe, no upgrade ═══

    [Fact]
    public void DeadV1File_ReaderFailsClean_FileUntouched()
    {
        // Craft an EXACT v1 file with LITERAL shape values (layoutVer=1, 600
        // regs, 1264 B — the BusLayout v1 consts are DEAD/frozen-history and
        // are being removed by the Sdk agent; this test must not reference
        // them). v1 is OUT of the v1.3.0 acceptance law (layoutVer∈{2,3} ∧
        // regCount ≥ 650) ⇒ fail-safe, NO upgrade path.
        const int v1FileBytes = 1264;
        const ushort v1LayoutVer = 1;
        const ushort v1RegCount = 600;
        var v1 = new byte[v1FileBytes];
        BusLayout.MagicBytes.CopyTo(v1, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(v1, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(v1LayoutVer).CopyTo(v1, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(v1RegCount).CopyTo(v1, BusLayout.HeaderRegCountOffset);
        for (int i = BusLayout.BodyOffset; i < v1FileBytes; i++)
            v1[i] = (byte)(i * 31 % 256); // distinctive body pattern
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_mmfPath, v1);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out var error));
        Assert.NotNull(error);
        Assert.Contains("fail-safe", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(v1FileBytes, new FileInfo(_mmfPath).Length); // untouched
        Assert.False(reader.IsOpen);
    }

    // ═══ 7. Foreign/future layout ⇒ clean fail, file untouched ═══

    [Fact]
    public void FutureLayout_File_ReaderFailsClean_FileUntouched()
    {
        var buf = new byte[BusLayout.TotalFileBytes];
        BusLayout.MagicBytes.CopyTo(buf, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(buf, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes((ushort)6).CopyTo(buf, BusLayout.HeaderLayoutVersionOffset); // future layout (6 — v5 is current)
        BitConverter.GetBytes(BusLayout.RegCount).CopyTo(buf, BusLayout.HeaderRegCountOffset);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_mmfPath, buf);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out var error));
        Assert.NotNull(error);
        Assert.Contains("unsupported", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-safe", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BusLayout.TotalFileBytes, new FileInfo(_mmfPath).Length); // untouched
        Assert.False(reader.IsOpen);
    }

    // ═══ 8. Reserved wits range (layout v3): header reserved-zero; external
    //        A5 Modbus writes to 600..649 persist and decode; lan_ext tail zero. ═══

    [Fact]
    public void WitsZoneReserved_ExternalWritesPersist_HeaderReservedZero()
    {
        var buf = new byte[BusLayout.TotalFileBytes];
        BusLayout.MagicBytes.CopyTo(buf, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(buf, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(BusLayout.LayoutVersion).CopyTo(buf, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCount).CopyTo(buf, BusLayout.HeaderRegCountOffset);

        // External Modbus writes land in the RESERVED range (A5: inactive zones
        // accept external writes that persist). No header field is touched.
        WriteWord(buf, 600, 0xA3D7); WriteWord(buf, 601, 0x417C); // 15.79
        var (l10, h10) = CdabCodec.Split(1.0f);
        WriteWord(buf, 602, l10); WriteWord(buf, 603, h10);       // 1.0

        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_mmfPath, buf);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var err), err);
        var snap = reader.Snapshot();
        Assert.NotNull(snap);

        // v3 law: former wits header block reads reserved-zero ⇒ never-written.
        Assert.False(snap.Wits.Active);
        Assert.Equal(0u, snap.Wits.WriteCount);
        Assert.Equal(0, snap.Wits.LastWriteTicks);

        // Reserved-zone words decode: external writes persist (A5).
        var reserved = snap.GetZoneFloats(ZoneKind.Wits);
        Assert.Equal(25, reserved.Length); // 50 regs = 25 floats
        Assert.Equal(15.79f, reserved[0], 3);
        Assert.Equal(1.0f, reserved[1]);
        Assert.Equal(0f, reserved[24]);

        // WitsSlots and the slot table are GONE from BusLayout (wits fully
        // removed) — the reserved range contract is positional only: 600..649
        // stays in the layout, never written by any writer.
        const int witsReservedFrom = 610; // first register after the former 5 pinned slots

        // Reserved tail 610..649 + lan_ext 650..8403 stay zero.
        for (int i = witsReservedFrom; i < BusLayout.RegCount; i++)
            Assert.Equal(0, snap.Words[i]);

        // lan_ext decodes 3877 floats, all zero; other zones unaffected.
        Assert.Equal(3877, snap.GetZoneFloats(ZoneKind.LanExt).Length);
        Assert.False(snap.Lan.Active);
        Assert.Equal(125, snap.GetZoneFloats(ZoneKind.Lan).Length);

        // Shared liveness: LanExt zone info mirrors the Lan block (v3 law).
        Assert.Equal(snap.Lan, snap.ZoneInfo(ZoneKind.LanExt));
    }

    private static void WriteWord(byte[] buf, int reg, ushort value)
    {
        int off = BusLayout.BodyOffset + reg * 2;
        buf[off] = (byte)(value >> 8);   // body words big-endian (wire copy)
        buf[off + 1] = (byte)value;
    }
}
