using System.IO;
using System.IO.MemoryMappedFiles;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Host.Core.Health;
using iCemDataMonitor.Sdk.Bus;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iCemDataMonitor.Host.Tests;

// ═══════════════════════════════════════════════════════════════════════════
// BusHealthMonitor verdict tests (NO hosted loop — Probe() is internal via
// InternalsVisibleTo, precedent ModbusTcpPublisher.TickOnce): the 1 Hz
// health iteration pinned against a REAL BusReader over a temp bus file.
//
// FIX 2026-08-28 (dishonest LAN surfaces):
//   • case_lan_state = header reg 2 (BusLayout.RegCaseLanState, tap-exposed)
//     — the former StationHealth.LanStatus carried header reg 8, a com_stream
//     float-pair low word in v5 = garbage rendered as LAN state.
//   • STALE-DECAY LAW: on a STALE snapshot (mutex-timeout last-good /
//     file-absent degrade, BUS-LIFECYCLE rows #10/#11/#12) the age-gated zone
//     verdicts are RECOMPUTED from the frozen copy's real ticks — a zone
//     decays to inactive after 5 s naturally. Frozen are ONLY the values that
//     cannot be recomputed (AddInAlive heartbeat movement, register VALUES).
//     Pre-fix the whole verdict froze ⇒ LAN stayed «active» forever after
//     Data.dat vanished.
//
// Fixture = the TelemetryServiceImplTests producer-first shape (INV-26): the
// named ReadWrite section is born BEFORE the reader opens; all writes go
// through the section under the session-local bus mutex. Session-local
// map/mutex names — the real Global\ bus is NEVER touched.
// ═══════════════════════════════════════════════════════════════════════════

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BusHealthMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bhm_{Guid.NewGuid():N}");
    private readonly string _mapName;
    private readonly string _mutexName;
    private readonly string _mmfPath;
    private readonly BusReader _reader;
    private readonly ProbeClock _clock = new();
    private readonly MemoryMappedFile _section;

    public BusHealthMonitorTests()
    {
        Directory.CreateDirectory(_dir);
        _mmfPath = Path.Combine(_dir, "bus.dat");
        _mapName = $"BhmMap_{Guid.NewGuid():N}";
        _mutexName = $"BhmMutex_{Guid.NewGuid():N}";

        CreateBusFile(_mmfPath);
        _section = MemoryMappedFile.CreateFromFile(
            new FileStream(_mmfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite),
            _mapName, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);

        _reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(_reader.TryOpen(out var rerr), rerr);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _section.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── fixture helpers (TelemetryServiceImplTests shape) ───────────────────

    private static void CreateBusFile(string mmfPath)
    {
        using var fs = new FileStream(mmfPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        Span<byte> header = stackalloc byte[BusLayout.HeaderBytes];
        BusLayout.MagicBytes.CopyTo(header);
        BitConverter.TryWriteBytes(header.Slice(BusLayout.HeaderVersionOffset, sizeof(ushort)), BusLayout.Version);
        BitConverter.TryWriteBytes(header.Slice(BusLayout.HeaderLayoutVersionOffset, sizeof(ushort)), BusLayout.LayoutVersion);
        BitConverter.TryWriteBytes(header.Slice(BusLayout.HeaderRegCountOffset, sizeof(ushort)), BusLayout.RegCount);
        fs.Write(header);
        fs.SetLength(BusLayout.TotalFileBytes);
    }

    /// <summary>Raw BE u16 words at absolute body addresses under the bus mutex.</summary>
    private void WriteRawWords(params (int Addr, ushort Value)[] writes)
    {
        using var mmf = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.ReadWrite);
        using var mutex = new Mutex(false, _mutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }
        try
        {
            using var acc = mmf.CreateViewAccessor(0, BusLayout.TotalFileBytes, MemoryMappedFileAccess.ReadWrite);
            foreach (var (addr, value) in writes)
            {
                int off = BusLayout.BodyOffset + addr * 2;
                acc.Write(off, (byte)(value >> 8));   // body words are BIG-endian
                acc.Write(off + 1, (byte)(value & 0xFF));
            }
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    /// <summary>Zone liveness header block (LE u64 tick / u8 flag / u32 count), under the mutex.</summary>
    private void WriteZoneHeader(ZoneKind kind, long tick, bool active, uint count)
    {
        using var mmf = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.ReadWrite);
        using var mutex = new Mutex(false, _mutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }
        try
        {
            using var acc = mmf.CreateViewAccessor(0, BusLayout.TotalFileBytes, MemoryMappedFileAccess.ReadWrite);
            acc.Write(BusLayout.ZoneLastWriteTickOffset(kind), tick);
            acc.Write(BusLayout.ZoneActiveFlagOffset(kind), active ? (byte)1 : (byte)0);
            acc.Write(BusLayout.ZoneWriteCountOffset(kind), count);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private BusHealthMonitor BuildMonitor(StationHealthState state) =>
        new(_reader, state, NullLogger<BusHealthMonitor>.Instance, _clock);

    private sealed class ProbeClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } =
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan elapsed) => Now += elapsed;
    }

    /// <summary>Keep ownership on one dedicated thread until the entire
    /// assertion body finishes, including exception paths. A timer must never
    /// release the mutex underneath a delayed probe.</summary>
    private static async Task HoldBusMutexAsync(string mutexName, Func<Task> during)
    {
        using var release = new ManualResetEventSlim(false);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Factory.StartNew(() =>
        {
            try
            {
                using var mutex = new Mutex(false, mutexName);
                mutex.WaitOne();
                try
                {
                    acquired.SetResult();
                    release.Wait();
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex)
            {
                acquired.TrySetException(ex);
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await during();
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    // ═══ FIX 1: case_lan_state = header reg 2 — the reg-8 float pair ═══
    //        NEVER leaks into the health surface. ═══

    [Fact]
    public void Probe_CaseLanState_ComesFromHeaderReg2_NotTheReg8FloatPair()
    {
        // Header state reg 2 = caseLanState (tap-exposed); regs 8..9 = com_stream
        // float pair idx 2 — the v4 "LanStatus" surface, here a float payload
        // whose low word (0x7777) must never surface as LAN state.
        WriteRawWords((BusLayout.RegCaseLanState, 0x0102), (8, 0x7777), (9, 0x1234));

        var state = new StationHealthState();
        var monitor = BuildMonitor(state);

        monitor.Probe(); // fresh snapshot

        var h = state.Current;
        Assert.Equal(0x0102, h.CaseLanState);        // reg 2 — the honest surface
        Assert.NotEqual(0x7777, h.CaseLanState);     // the reg-8 low word did NOT leak
        Assert.Equal(0x7777, _reader.LastSnapshot!.GetRegister(8)); // reg 8 = raw float-pair word
    }

    // ═══ FIX 2: stale decay — the frozen copy cannot freeze a «live» verdict ═══

    [Fact]
    public async Task StaleProbe_ZoneAgeCrosses5s_VerdictDecaysToInactive()
    {
        // Virtual UTC fixes the age independently of runner scheduling.
        WriteZoneHeader(ZoneKind.Lan, _clock.Now.AddSeconds(-4).Ticks, active: true, count: 7);

        var state = new StationHealthState();
        var monitor = BuildMonitor(state);

        // Fresh probe #1: baseline (AddInAlive conservative false), zone active.
        monitor.Probe();
        Assert.True(state.Current.Lan.Active);
        Assert.False(state.Current.AddInAlive);          // first sample: no movement seen yet

        // Fresh probe #2 with a heartbeat ADVANCE (reg 3 = movement signal):
        // AddInAlive flips true — the "last verdict" is now a LIVE lan zone.
        WriteRawWords((BusLayout.RegHeartbeat, 101));
        monitor.Probe();
        Assert.True(state.Current.AddInAlive);
        Assert.Equal(101, state.Current.Heartbeat);
        Assert.True(state.Current.Lan.Active);

        // Freeze the real reader's last-good snapshot under a real mutex.
        // Only the monitor clock advances; the producer ticks remain frozen.
        await HoldBusMutexAsync(_mutexName, () =>
        {
            _clock.Advance(TimeSpan.FromSeconds(2));

            // Prove the hold is LIVE right now: the reader's own snapshot call
            // times out and returns the frozen last-good with Stale=true (the
            // stale copy is the RETURN VALUE — LastSnapshot keeps the fresh one).
            var staleSnap = _reader.Snapshot();
            Assert.NotNull(staleSnap);
            Assert.True(staleSnap.Stale);
            Assert.True(staleSnap.Lan.Active); // raw producer flag STILL 1 in the frozen copy

            monitor.Probe(); // stale probe #1 — internal Snapshot times out the same way

            var h1 = state.Current;
            Assert.False(h1.Lan.Active);                          // THE DECAY PIN: verdict recomputed ⇒ inactive
            Assert.Equal(6_000, h1.Lan.AgeMs); // age crossed the gate while frozen

            // Frozen (NOT recomputable from a frozen copy): the heartbeat-
            // movement verdict and the register VALUES stay at last-good.
            Assert.True(h1.AddInAlive);
            Assert.Equal(101, h1.Heartbeat);

            // Recomputed: reader state stays live on the stale path (only a
            // file-vanish closes the reader — a mutex hold does not).
            Assert.True(h1.BusOpen);

            _clock.Advance(TimeSpan.FromMilliseconds(50));
            monitor.Probe(); // stale probe #2 — decay KEEPS progressing
            Assert.False(state.Current.Lan.Active);
            Assert.Equal(h1.Lan.AgeMs + 50, state.Current.Lan.AgeMs); // age still growing
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StaleProbe_ZoneAgeUnder5s_VerdictStaysActive_NoOverFlip()
    {
        WriteZoneHeader(ZoneKind.Lan, _clock.Now.AddMilliseconds(-500).Ticks, active: true, count: 9);

        var state = new StationHealthState();
        var monitor = BuildMonitor(state);

        monitor.Probe();
        Assert.True(state.Current.Lan.Active);

        await HoldBusMutexAsync(_mutexName, () =>
        {
            // Prove the hold is LIVE (same surface law as above), then probe.
            var staleSnap = _reader.Snapshot();
            Assert.NotNull(staleSnap);
            Assert.True(staleSnap.Stale);

            monitor.Probe(); // stale — age still under the 5 s gate

            var h = state.Current;
            Assert.Equal(500, h.Lan.AgeMs);
            Assert.True(h.Lan.Active); // still active — stale ≠ dead
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Probe_Fresh_ZoneVerdict_IsAgeGated_EvenWhenFlagClaimsActive()
    {
        // W10.1 fresh-path pin: a zone whose tick is already > 5 s old reads
        // inactive on a FRESH snapshot too — the flag alone is never truth.
        WriteZoneHeader(ZoneKind.Lan, _clock.Now.AddMilliseconds(-6_000).Ticks, active: true, count: 3);
        WriteZoneHeader(ZoneKind.ComMain, _clock.Now.Ticks, active: true, count: 1);

        var state = new StationHealthState();
        BuildMonitor(state).Probe();

        Assert.False(state.Current.Lan.Active);  // old tick beats the flag
        Assert.True(state.Current.Lan.AgeMs >= BusZoneInfo.MaxZoneAgeMs);
        Assert.True(state.Current.Com.Active);   // fresh tick + flag ⇒ active
        Assert.True(state.Current.Com.AgeMs < BusZoneInfo.MaxZoneAgeMs);
    }

    [Theory]
    [InlineData(-1, true, true, 0)]
    [InlineData(0, true, true, 0)]
    [InlineData(4999, true, true, 4999)]
    [InlineData(5000, true, false, 5000)]
    [InlineData(5001, true, false, 5001)]
    [InlineData(0, false, false, 0)]
    public async Task Probe_AllZones_UseSameAgeGate_OnFreshAndStaleSnapshots(
        long ageMs, bool flag, bool active, long reportedAgeMs)
    {
        foreach (var zone in new[] { ZoneKind.ComMain, ZoneKind.Lan, ZoneKind.RawReserve })
            WriteZoneHeader(zone, _clock.Now.AddMilliseconds(-ageMs).Ticks, flag, count: 1);

        var state = new StationHealthState();
        var monitor = BuildMonitor(state);
        void AssertZones()
        {
            foreach (var zone in new[] { state.Current.Com, state.Current.Lan, state.Current.Raw })
            {
                Assert.Equal(active, zone.Active);
                Assert.Equal(reportedAgeMs, zone.AgeMs);
            }
            Assert.Equal(_clock.Now.UtcDateTime, state.Current.UpdatedAtUtc);
        }

        monitor.Probe();
        AssertZones();
        await HoldBusMutexAsync(_mutexName, () =>
        {
            Assert.True(_reader.Snapshot()!.Stale);
            monitor.Probe();
            AssertZones();
            return Task.CompletedTask;
        });
    }

}

