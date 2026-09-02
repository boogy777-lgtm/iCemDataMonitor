using System.IO.MemoryMappedFiles;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// BUS-LIFECYCLE TRUTH TABLE pins (the full normative 12-row table lives in
/// the BusReader class doc-comment) + the owner-confirmed 2026-08-28 stand-
/// incident falsification pin. Session-local map/mutex names per test — the
/// REAL Global\iCemDataMonitorBus_v1 / Global\iCemDataMonitorBusMutex_v1 are
/// NEVER touched; all files live in per-test temp directories.
///
/// Row mapping (this file): #1 (absent file, honest error, nothing claimed),
/// #2 (file appears ⇒ live reading without restart), #3 (SECTION-CREATION LAW
/// regression pin: after a reader open OpenExisting still throws
/// FileNotFoundException — the name is never claimed by a reader; unnamed
/// file-backed read works), #4 (producer-section attach + live flow), #5
/// (legacy honest error ⇒ producer migration ⇒ reader recovers), #6 (foreign
/// fail-safe ⇒ file replaced ⇒ recovery), #8 (capacity-skew typed fatal,
/// sticky — at the BusReader surface), #10 (producer stopped ⇒ last-written
/// bytes persist, zones age), #11 (mid-life name removal while the reader is
/// section-attached — the exact production orphaned-pages state ⇒ mapping
/// closed, Stale=true degrade, then the NEW file is re-opened), #12 (a live
/// file-backed orphan over a removed name is REFUSED — file-absent beats a
/// live orphan), and the INCIDENT PIN: reader-opens-first ⇒ producer ReadWrite
/// attach SUCCEEDS (pre-fix it died with ERROR_INVALID_PARAMETER «The
/// parameter is incorrect» — BusWriter disabled forever).
///
/// Mid-life name-removal mechanism: Win32 DeleteFile hard-fails with
/// ERROR_ACCESS_DENIED on any file with an active section reference (the
/// kernel additionally denies delete-disposition on files with a writable
/// data section — POSIX-semantics FileDispositionInfoEx returns error 5), so
/// the tests reach the production state (path absent, pages alive) via
/// RENAME-AWAY: File.Move detaches the NAME from the file object while the
/// section keeps the data pages — the mechanism real deploy/installer tools
/// use, allowed by the kernel even with a writable section alive. The reader
/// cannot distinguish a rename-away from a deletion — both leave File.Exists
/// false with the mapping alive, which is exactly what the probe law pins.
/// The section surrogate is born from a FILE_SHARE_DELETE stream so the
/// surviving file object shares DELETE and the rename-away passes the
/// sharing check.
///
/// Row #7 (poisoned RO section) is DOC-ONLY: a section born READ-ONLY can only
/// be manufactured by the PRE-FIX reader binary (a section's protection is
/// frozen at birth by its FIRST creator) — honestly not reproducible in one
/// process; producer-side remediation (restart/replace the old reader) is
/// documented in the truth table.
/// Row #9 (file ACL deny) stays pinned in BusAccessDeniedTests (typed ForPath,
/// sticky fatal).
/// </summary>
[Collection("DataBusLogState")] // serialized against DataBusLogTests: the
// shared static DataBusLog channel (dedup gate, dir override) is per-test
// state there — parallel Error lines would race its File.ReadAllText asserts.
public class BusOpenTruthTableTests : IDisposable
{
    private readonly string _dir;
    private readonly string _mmfPath;
    private readonly string _mapName;
    private readonly string _mutexName;

    public BusOpenTruthTableTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bus_truth_test", Guid.NewGuid().ToString("N"));
        _mmfPath = Path.Combine(_dir, "test_bus.dat");
        _mapName = $"iCemDataMonitorBusTruthMap_{Guid.NewGuid():N}";
        _mutexName = $"iCemDataMonitorBusTruthMutex_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ═══ ROW #1 — D1 absent, D2 absent, producer stopped ⇒ DEGRADED-LIVE:
    //        TryOpen=false + honest error (logged once), no blocking-wait,
    //        and NEITHER the file NOR the section name is claimed. ═══

    [Fact]
    public void Row1_FileAbsent_HonestError_NoBlocking_NothingClaimed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.False(File.Exists(_mmfPath));

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out var error));
        Assert.NotNull(error);
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("producer AddIn has not created it yet", error);
        Assert.False(reader.IsOpen);
        Assert.Null(reader.Snapshot()); // degraded, honest — never blocks, no data yet

        // D2 untouched: the reader did not claim the section name either.
        Assert.Null(TryOpenSectionRead(_mapName));
    }

    // ═══ ROW #2 — D1 absent→present, producer starts ⇒ TRANSITION: the next
    //        Snapshot's re-open probe succeeds ⇒ live reading WITHOUT restart;
    //        LastError cleared (Success re-armed the dedup gate). ═══

    [Fact]
    public void Row2_FileAppears_SnapshotSelfHeals_NoRestart()
    {
        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out _));
        Assert.Null(reader.Snapshot()); // degraded-live: nothing yet

        // The producer creates the file (CreateNew under the bus mutex — factory
        // surrogate) whenever it starts; the reader does NOTHING meanwhile.
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 3.25f);

        // Same reader instance, no restart — Snapshot's own probe adopts it.
        var snap = reader.Snapshot();
        Assert.NotNull(snap);
        Assert.True(reader.IsOpen);
        Assert.False(snap.Stale);
        Assert.Equal(3.25f, snap.GetZoneFloats(ZoneKind.Lan)[0]);
        Assert.Null(reader.LastError); // the honest-error state is cleared on success
    }

    // ═══ ROW #3 — D1 present_valid, D2 absent, producer stopped ⇒ UNNAMED
    //        mapping reads the last written bytes, AND THE SECTION NAME IS
    //        NEVER CLAIMED — the key SECTION-CREATION LAW regression pin
    //        (pre-fix: the reader birthed a READ-ONLY named section here). ═══

    [Fact]
    public void Row3_ValidFileNoSection_UnnamedReadWorks_SectionNameNeverClaimed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 9.75f);

        // Baseline: the name is unclaimed before any open.
        Assert.Null(TryOpenSectionRead(_mapName));

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var err), err);
        var snap = reader.Snapshot();
        Assert.NotNull(snap);
        Assert.Equal(9.75f, snap.GetZoneFloats(ZoneKind.Lan)[0]); // unnamed file-backed read

        // THE PIN: after the reader OPENED, OpenExisting still fails with
        // FileNotFoundException — the reader NEVER created the named section.
        Assert.Null(TryOpenSectionRead(_mapName));

        // The producer's later ReadWrite attach on the SAME name succeeds, and
        // its writes are visible to the (still unnamed) reader — file-backed
        // mappings of one file are coherent.
        using var producer = new SectionWriter(_mmfPath, _mapName, _mutexName);
        producer.WriteFloat(200, 4.5f);
        var live = reader.Snapshot();
        Assert.NotNull(live);
        Assert.Equal(4.5f, live.GetZoneFloats(ZoneKind.Lan)[0]);
    }

    // ═══ ROW #4 — D1 present_valid, D2 exists_RW, producer running ⇒ NORMAL:
    //        the reader attaches OpenExisting(Read) and live data flows.
    //        (Attach-vs-unnamed discrimination is pinned by ROW #8 below:
    //        ForSectionCapacity is reachable ONLY through a successful
    //        OpenExisting attach — an unnamed fallback would have succeeded.) ═══

    [Fact]
    public void Row4_ProducerSectionAlive_ReaderAttaches_LiveDataFlows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        BusTestFactory.CreateFile(_mmfPath);

        // Producer FIRST — the section must be born ReadWrite.
        using var producer = new SectionWriter(_mmfPath, _mapName, _mutexName);
        producer.WriteFloat(200, 6.25f);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var err), err); // attaches the live RW section

        var snap = reader.Snapshot();
        Assert.NotNull(snap);
        Assert.False(snap.Stale);
        Assert.Equal(6.25f, snap.GetZoneFloats(ZoneKind.Lan)[0]);

        producer.WriteFloat(202, 21.5f); // live flow AFTER the attach
        var live = reader.Snapshot();
        Assert.NotNull(live);
        Assert.Equal(21.5f, live.GetZoneFloats(ZoneKind.Lan)[1]);
    }

    // ═══ ROW #5 — D1 present_legacy (v2), any D2 ⇒ honest «producer migration
    //        required» error, the file NOT touched, degraded; the producer's
    //        FULL MIGRATION (AddIn-owned) expands it ⇒ the next TryOpen probe
    //        succeeds — no restart. ═══

    [Fact]
    public void Row5_LegacyFile_HonestError_ProducerMigrates_ReaderRecovers()
    {
        // Exact v2 upgrade source: layoutVer=2, regCount=650, 1364 B.
        BusTestFactory.CreateLegacyFile(_mmfPath, layoutVer: BusLayout.LayoutVersionV2, regCount: BusLayout.RegCountV2);
        BusTestFactory.FillBody(_mmfPath, pattern: 0xB5, fromReg: 0, toReg: BusLayout.RegCountV2 - 1);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out var error));
        Assert.NotNull(error);
        Assert.Contains("producer migration", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start iCem once", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(reader.IsOpen);
        Assert.Equal(BusLayout.TotalFileBytesV2, new FileInfo(_mmfPath).Length); // file untouched

        // PRODUCER-side FULL MIGRATION (surrogate): the AddIn expands the file
        // in place to the v5 final shape — the reader never touches it.
        BusTestFactory.CreateFile(_mmfPath); // v5 final shape (10001/20066)
        BusTestFactory.WriteFloat(_mmfPath, 200, 8.5f);

        var snap = reader.Snapshot(); // retry probe — no restart
        Assert.NotNull(snap);
        Assert.True(reader.IsOpen);
        Assert.Equal(8.5f, snap.GetZoneFloats(ZoneKind.Lan)[0]);
    }

    // ═══ ROW #6 — D1 present_foreign ⇒ fail-safe typed error, degraded, retry;
    //        if the file is REPLACED with a valid one, the reader recovers. ═══

    [Fact]
    public void Row6_ForeignFile_FailSafe_FileReplaced_ReaderRecovers()
    {
        // Future layout (layoutVer 6 — v5 is current) with a valid identity:
        // fail-safe, no migration path, file untouched.
        var buf = new byte[BusLayout.TotalFileBytes];
        BusLayout.MagicBytes.CopyTo(buf, BusLayout.HeaderMagicOffset);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(buf, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes((ushort)6).CopyTo(buf, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCount).CopyTo(buf, BusLayout.HeaderRegCountOffset);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_mmfPath, buf);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.False(reader.TryOpen(out var error));
        Assert.NotNull(error);
        Assert.Contains("unsupported", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-safe", error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(reader.Snapshot()); // degraded — no data

        // The file is replaced with a valid v5 shape ⇒ the next probe adopts it.
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 5.5f);

        var snap = reader.Snapshot(); // recovery — no restart
        Assert.NotNull(snap);
        Assert.True(reader.IsOpen);
        Assert.Equal(5.5f, snap.GetZoneFloats(ZoneKind.Lan)[0]);
    }

    // ═══ ROW #8 — D1 present_valid, D2 exists_RW smaller than the required
    //        view ⇒ typed BusAccessDeniedException.ForSectionCapacity at the
    //        BusReader surface (this ALSO pins the ROW #4 attach law: the
    //        typed capacity error is reachable ONLY via a successful
    //        OpenExisting attach) ⇒ _accessDeniedFatal — sticky, no per-tick
    //        re-throw (INV-20). ═══

    [Fact]
    public void Row8_CapacitySkewSection_TypedAccessDenied_Sticky()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var liveSection = CreateLiveSection(_mapName, sectionBytes: 1024);

        // A VALID v5 file (regCount=1000 ⇒ 2064 B) larger than the live
        // section's frozen capacity (1024 B) — the exact production skew.
        BusTestFactory.CreateFile(_mmfPath, regCount: 1000);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        var ex = Assert.Throws<BusAccessDeniedException>(() => reader.TryOpen(out _));
        Assert.Contains("capacity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2064", ex.Message); // required bytes surfaced for the operator
        Assert.Contains("start the producer FIRST", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Sticky fatal: pollers fail-fast with the stored error, never re-throw.
        Assert.False(reader.TryOpen(out var err2));
        Assert.False(reader.IsOpen);
        Assert.Null(reader.Snapshot());
    }

    // ═══ ROW #10 — D1 present_valid, D2 exists_RW, producer STOPPED mid-life
    //        ⇒ keep reading the file (last written bytes); zones age ⇒
    //        stale-hold (existing law). ═══

    [Fact]
    public void Row10_ProducerStopped_LastWrittenBytesPersist_ZoneAges()
    {
        if (!OperatingSystem.IsWindows())
            return;

        BusTestFactory.CreateFile(_mmfPath);

        using var producer = new SectionWriter(_mmfPath, _mapName, _mutexName);
        producer.WriteFloat(200, 33.25f);
        producer.WriteZoneHeader(ZoneKind.Lan, DateTime.UtcNow.Ticks, active: true, count: 5);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var err), err);
        Assert.Equal(33.25f, reader.Snapshot()!.GetZoneFloats(ZoneKind.Lan)[0]);

        producer.Dispose(); // ПРОИЗВОДИТЕЛЬ ОСТАНОВИЛСЯ — the file persists (last written)

        var snap = reader.Snapshot();
        Assert.NotNull(snap);
        Assert.True(reader.IsOpen);                       // still reading the persisted file
        Assert.Equal(33.25f, snap.GetZoneFloats(ZoneKind.Lan)[0]); // last written bytes survive
        Assert.True(snap.Lan.Active);                     // flag persists — data is stale but intact

        var age1 = snap.Lan.AgeMs();
        Thread.Sleep(30);
        var age2 = reader.Snapshot()!.Lan.AgeMs();
        Assert.True(age2 >= age1); // zone age grows honestly (stale metric)
    }

    // ═══ ROW #11 — D1 deleted mid-life while the reader is SECTION-ATTACHED
    //        (the exact production state: the name deletes cleanly, the
    //        file-backed mapping keeps serving ORPHANED pages) ⇒ FILE-ABSENT
    //        DEGRADE: the probe closes the mapping+mutex, IsOpen=false, the
    //        last-good copy stale-holds (Stale=true), and when the producer
    //        creates a NEW file the next Snapshot re-opens ALREADY THE NEW
    //        file — no restart. ═══

    [Fact]
    public void Row11_FileDeletedMidLife_MappingClosed_Degrades_ReopensNewFile()
    {
        if (!OperatingSystem.IsWindows())
            return;

        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 11.5f);

        // Producer section surrogate (share-delete stream — see class docs).
        var producerSection = CreateSectionShareDelete(_mmfPath, _mapName);
        try
        {
            // Reader opens while the producer lives ⇒ attaches the named
            // section (OpenExisting — NO regular file handle of its own).
            using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
            Assert.True(reader.TryOpen(out var err), err);
            var live = reader.Snapshot();
            Assert.NotNull(live);
            Assert.False(live.Stale);
            Assert.Equal(11.5f, live.GetZoneFloats(ZoneKind.Lan)[0]);

            producerSection.Dispose(); // producer stops — the reader's attach handle
                                       // alone keeps the section (and the file object) alive
            RenameAwayName(_mmfPath);   // the exact production mid-life name removal: the
                                        // path goes absent, the data pages live via the reader's section
            Assert.False(File.Exists(_mmfPath));

            // Falsification: WITHOUT the probe the reader would keep serving
            // orphaned pages (11.5) forever with IsOpen=true. WITH the probe:
            var degraded = reader.Snapshot();
            Assert.False(reader.IsOpen);                  // mapping + mutex closed
            Assert.NotNull(degraded);                     // last-good stale-hold
            Assert.True(degraded.Stale);                  // degraded — honest surface
            Assert.Equal(11.5f, degraded.GetZoneFloats(ZoneKind.Lan)[0]); // last-written copy
            Assert.NotNull(reader.LastError);
            Assert.Contains("vanished", reader.LastError, StringComparison.Ordinal);

            // The producer recreates a NEW file (CreateNew under its mutex).
            BusTestFactory.CreateFile(_mmfPath);
            BusTestFactory.WriteFloat(_mmfPath, 200, 77.7f);

            var reborn = reader.Snapshot(); // NO restart — the probe re-opens
            Assert.NotNull(reborn);
            Assert.True(reader.IsOpen);
            Assert.False(reborn.Stale);
            Assert.Equal(77.7f, reborn.GetZoneFloats(ZoneKind.Lan)[0]); // the NEW file,
            Assert.Null(reader.LastError);                              // ¬ orphaned pages
        }
        finally
        {
            producerSection.Dispose();
        }
    }

    // ═══ ROW #12 — D1 absent, D2 exists_RW (the section outlived the file
    //        deletion) ⇒ the file-absent law takes PRIORITY: the reader
    //        REFUSES to attach the live orphan (fresh opens refuse at the
    //        File.Exists gate), degrades, and adopts the NEW file when it
    //        appears — never the orphan's dead bytes. ═══

    [Fact]
    public void Row12_FileAbsentLiveOrphan_ReaderRefusesOrphan_DegradeLiftsOnNewFile()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Manufacture the orphan: a file-backed named ReadWrite section whose
        // backing file NAME is already removed — the data pages live via the
        // section (the production #11/#12 state).
        BusTestFactory.CreateFile(_mmfPath); // seed a valid v5 shape first (the helper opens, not creates)
        var orphan = CreateSectionShareDelete(_mmfPath, _mapName);
        try
        {
            RenameAwayName(_mmfPath); // name gone; the pages live via the orphan section
            Assert.False(File.Exists(_mmfPath)); // D1 absent

            // The orphan IS attachable — a naive open would read its dead bytes.
            using (var probe = TryOpenSectionRead(_mapName))
                Assert.NotNull(probe);

            // LAW: file-absent beats a live orphan — clean row-#1 fail, no attach.
            using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
            Assert.False(reader.TryOpen(out var error));
            Assert.NotNull(error);
            Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(reader.IsOpen);
            Assert.Null(reader.Snapshot());

            // Ordered world: the orphan's last handle closes — the name frees.
            orphan.Dispose();

            // The producer creates a NEW file ⇒ the degrade lifts onto the
            // NEW file (the orphan is never adopted).
            BusTestFactory.CreateFile(_mmfPath);
            BusTestFactory.WriteFloat(_mmfPath, 200, 44.25f);

            var snap = reader.Snapshot();
            Assert.NotNull(snap);
            Assert.True(reader.IsOpen);
            Assert.False(snap.Stale);
            Assert.Equal(44.25f, snap.GetZoneFloats(ZoneKind.Lan)[0]);
        }
        finally
        {
            orphan.Dispose();
        }
    }

    // ═══ STALE-DECAY ROWS (2026-08-28, health-verdict law over the frozen
    //        copies of rows #10/#11): a STALE snapshot (mutex-timeout last-good
    //        / file-absent degrade) still carries REAL zone ticks, so the
    //        age-gated liveness verdict (BusZoneInfo.IsActive) is RECOMPUTED —
    //        a zone whose tick ages past 5 s decays to inactive even though
    //        the frozen copy's activeFlag stays 1. Pre-fix the health verdict
    //        froze wholesale ⇒ LAN stayed «active» forever after Data.dat
    //        vanished. The raw producer flag and the snapshot surface
    //        (Stale=true, last-good copy) are pinned UNCHANGED. ═══

    [Fact]
    public async Task StaleDecay_ZoneAgeOver5s_VerdictInactive()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Producer wrote the LAN zone 4.0 s ago and died (flag frozen at 1).
        // (4.0 s ⇒ fresh probe has ~1 s of margin under scheduler stalls; the
        // 1.5 s hold then pushes the stale probe past the 5 s gate with margin.)
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteZoneHeader(_mmfPath, ZoneKind.Lan,
            DateTime.UtcNow.AddMilliseconds(-4_000).Ticks, active: true, count: 7);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var err), err);

        var good = reader.Snapshot(); // fresh copy: age ≈ 4.5 s < 5 s ⇒ still active
        Assert.NotNull(good);
        Assert.False(good.Stale);
        Assert.True(good.Lan.Active);
        Assert.True(good.Lan.IsActive);

        // Hold the mutex > 500 ms from a DEDICATED thread (ownership is
        // per-thread; precedent BusReaderTests.MutexTimeout_ReturnsLastGood_StaleFlag)
        // ⇒ every further Snapshot serves the FROZEN last-good with Stale=true.
        using var gate = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var m = new Mutex(false, _mutexName);
            m.WaitOne();
            gate.Set();
            Thread.Sleep(3_500); // hold outlasts the in-try sleep + the 500 ms wait timeout
            m.ReleaseMutex();
        });
        gate.Wait();
        try
        {
            Thread.Sleep(2_000); // real time flows while frozen — age crosses the 5 s gate
            var stale = reader.Snapshot();
            Assert.NotNull(stale);
            Assert.True(stale.Stale);                                  // frozen last-good surface
            Assert.Equal(good.CapturedAtTickMs, stale.CapturedAtTickMs); // ¬ a fresh read
            Assert.True(stale.Lan.Active);                             // producer flag frozen at 1 (stale-hold law)
            Assert.True(stale.Lan.AgeMs() > BusZoneInfo.MaxZoneAgeMs); // real time kept flowing

            // THE DECAY PIN: the verdict RECOMPUTES from the frozen tick —
            // age > 5 s ⇒ inactive, no matter what the frozen flag claims.
            Assert.False(stale.Lan.IsActive);
        }
        finally
        {
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StaleDecay_ZoneAgeUnder5s_VerdictStillActive()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Producer wrote the LAN zone 1 s ago (flag 1) — then the snapshot
        // path goes stale; the age is still under the gate ⇒ active stands.
        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteZoneHeader(_mmfPath, ZoneKind.Lan,
            DateTime.UtcNow.AddMilliseconds(-1_000).Ticks, active: true, count: 9);

        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var err), err);

        var good = reader.Snapshot();
        Assert.NotNull(good);
        Assert.True(good.Lan.IsActive);

        using var gate = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var m = new Mutex(false, _mutexName);
            m.WaitOne();
            gate.Set();
            Thread.Sleep(1_200); // stale, but age ≈ 2.2 s — still under the gate
            m.ReleaseMutex();
        });
        gate.Wait();
        try
        {
            var stale = reader.Snapshot();
            Assert.NotNull(stale);
            Assert.True(stale.Stale);
            Assert.Equal(good.CapturedAtTickMs, stale.CapturedAtTickMs);
            Assert.True(stale.Lan.Active);
            Assert.True(stale.Lan.AgeMs() < BusZoneInfo.MaxZoneAgeMs);
            Assert.True(stale.Lan.IsActive); // no over-flip: still active while young
        }
        finally
        {
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    // ═══ INCIDENT PIN (owner-confirmed 2026-08-28 stand incident) — the
    //        falsifying pin to «host запустился раньше iCem»: READER opens
    //        FIRST, then the producer attaches ReadWrite on the SAME name.
    //        Pre-fix: the reader birthed the named section READ-ONLY ⇒ the
    //        producer's ReadWrite attach died with ERROR_INVALID_PARAMETER
    //        («The parameter is incorrect») forever — BusWriter disabled,
    //        «RETRY bus still disabled (attempt N)» in TapOut.log. Post-fix:
    //        the reader never claims the name ⇒ the attach succeeds. ═══

    [Fact]
    public void Incident_ReaderOpensFirst_ProducerReadWriteAttach_Succeeds()
    {
        if (!OperatingSystem.IsWindows())
            return;

        BusTestFactory.CreateFile(_mmfPath);
        BusTestFactory.WriteFloat(_mmfPath, 200, 2.75f);

        // READER FIRST — the incident's fatal order.
        using var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var err), err); // unnamed fallback (SECTION-CREATION LAW)

        // The name is STILL unclaimed after the reader opened — pre-fix this
        // OpenExisting SUCCEEDED (a reader-born READ-ONLY section existed).
        Assert.Null(TryOpenSectionRead(_mapName));

        // PRODUCER attach ReadWrite on the SAME name — pre-fix: ERROR_INVALID_PARAMETER.
        using var producer = new SectionWriter(_mmfPath, _mapName, _mutexName);
        producer.WriteFloat(200, 12.75f);

        var snap = reader.Snapshot(); // coherent — one file, one data section
        Assert.NotNull(snap);
        Assert.Equal(12.75f, snap.GetZoneFloats(ZoneKind.Lan)[0]);
    }

    // ── Helpers ──

    /// <summary>
    /// Probe the named section with Read rights. Returns null ONLY when the
    /// section does not exist (FileNotFoundException) — any other failure
    /// propagates and fails the test. Non-null results are caller-disposed.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static MemoryMappedFile? TryOpenSectionRead(string mapName)
    {
        try
        {
            return MemoryMappedFile.OpenExisting(
                mapName, MemoryMappedFileRights.Read, HandleInheritability.None);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// A LIVE named ReadWrite section born over a SMALL backing file — the
    /// section capacity freezes at creation (sectionBytes) and denies views
    /// beyond it (Windows ACCESS DENIED semantics). The caller keeps the
    /// handle alive for the test duration.
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

    /// <summary>
    /// Producer-section surrogate: a named ReadWrite file-backed section born
    /// from a FILE_SHARE_DELETE stream over the EXISTING seeded file (the
    /// stream is disposed immediately — the section pins the file object,
    /// whose share mode survives). The share-delete birth is what makes the
    /// mid-life rename-away legally pass the sharing check while the section
    /// stays alive. The caller keeps the returned section alive for the
    /// producer's lifetime.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private MemoryMappedFile CreateSectionShareDelete(string path, string mapName)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        return MemoryMappedFile.CreateFromFile(
            fs, mapName, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: true);
    }

    /// <summary>
    /// RENAME-AWAY name removal — the production mid-life mechanism: Win32
    /// DeleteFile hard-fails with ERROR_ACCESS_DENIED on any file with an
    /// active section reference (the kernel additionally denies delete
    /// disposition on files with a writable data section), but renaming is
    /// legal: it detaches the NAME from the file object while the section
    /// keeps the data pages alive. The reader's probe keys on File.Exists —
    /// a rename-away is indistinguishable from a deletion at its surface,
    /// which is exactly what the #11/#12 law pins.
    /// </summary>
    private void RenameAwayName(string path)
    {
        File.Move(path, path + ".orphaned");
    }
}
