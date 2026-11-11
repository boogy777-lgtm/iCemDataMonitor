using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Data.Telemetry;
using iCemDataMonitor.Sdk.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// D.4 MetricsSampler contract tests: freshness (sensor-at-send), drop-oldest
/// backpressure, delta correctness (first sample full, then only changed),
/// interval clamp, bus-closed fail-fast.
///
/// TEST SEAM (§15.7 whitelist (a) — data seam, ¬ code seam): the register
/// source is a REAL BusReader over a temp persisted MMF driven by the test-side
/// PRODUCER SURROGATE — BusTestFactory raw-byte seeding + a LIVE SectionWriter
/// (the AddIn stand-in, created BEFORE the reader so the named section is born
/// ReadWrite — producer-first order, INV-26) — plus direct mutex-guarded MMF
/// word writes for the AddIn-owned header state regs 0..3. MetricsSampler
/// itself carries NO production seam: only the physical carrier is substituted
/// (temp file + session-local map/mutex names instead of the live Global\ bus),
/// which makes the tests fully deterministic and isolated from the running
/// iCem.exe station.
/// Precedent: BusReaderTests / BusDecodeTruthTableTests use the same pattern.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // bus MMF + mutexes are Windows-only by contract
public sealed class MetricsSamplerTests : IDisposable
{
    private readonly string _dir;
    private BusAcquisitionService? _acquisition;

    private MetricsSampler CreateSampler(BusReader reader)
    {
        _acquisition = new BusAcquisitionService(reader, TimeSpan.FromMilliseconds(200));
        _acquisition.CaptureOnce();
        return new MetricsSampler(_acquisition);
    }
    private readonly string _mmfPath;
    private readonly string _mapName;
    private readonly string _mutexName;

    public MetricsSamplerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "metrics_sampler_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _mmfPath = Path.Combine(_dir, "bus.dat");
        _mapName = $"MetricsSamplerTestMap_{Guid.NewGuid():N}";
        _mutexName = $"MetricsSamplerTestMutex_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        _acquisition?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── test-source helpers (the §15.7(a) seam) ─────────────────────────────

    /// <summary>Writes raw u16 words at absolute body addresses under the bus mutex.</summary>
    private void WriteRawWords(params (int Addr, ushort Value)[] writes)
    {
        using var mmf = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.ReadWrite);
        using var mutex = new Mutex(false, _mutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }
        try
        {
            long len = new FileInfo(_mmfPath).Length;
            using var acc = mmf.CreateViewAccessor(0, len, MemoryMappedFileAccess.ReadWrite);
            foreach (var (addr, value) in writes)
            {
                int off = BusLayout.BodyOffset + addr * 2;
                acc.Write(off, (byte)(value >> 8));     // body words are BIG-endian
                acc.Write(off + 1, (byte)(value & 0xFF));
            }
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
        _acquisition?.CaptureOnce();
    }

    /// <summary>Writes the four AddIn-owned header state regs (v5 map).</summary>
    private void WriteHeader(short comPort, short caseState, short caseLanState, short heartbeat) =>
        WriteRawWords(
            (BusLayout.RegComPort, unchecked((ushort)comPort)),
            (BusLayout.RegCaseState, unchecked((ushort)caseState)),
            (BusLayout.RegCaseLanState, unchecked((ushort)caseLanState)),
            (BusLayout.RegHeartbeat, unchecked((ushort)heartbeat)));

    /// <summary>Creates the bus file + a LIVE producer section (producer-first
    /// order) and opens a reader against it. The returned SectionWriter is the
    /// test-side AddIn stand-in — the caller keeps it alive for the test and
    /// drives it with WriteFloat.</summary>
    private BusReader CreateOpenBus(out SectionWriter writer)
    {
        BusTestFactory.CreateFile(_mmfPath);
        writer = new SectionWriter(_mmfPath, _mapName, _mutexName); // born ReadWrite, before the reader
        var reader = new BusReader(_mmfPath, _mapName, _mutexName);
        Assert.True(reader.TryOpen(out var rerr), rerr);
        return reader;
    }

    // ═══ (а) freshness: значение меняется между сэмплами ═══

    [Fact]
    public async Task Freshness_ValueChangesBetweenSamples_HeaderAndZone()
    {
        using var reader = CreateOpenBus(out var writer);
        using var producer = writer;
        WriteHeader(comPort: 1, caseState: 2, caseLanState: 0, heartbeat: 100);

        var sampler = CreateSampler(reader);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var samples = new List<MetricsSample>();
        // A capacity-one DropOldest stream may lose its first full sample before
        // the consumer runs. Only observed loss makes an attempt inconclusive;
        // wrong values on a loss-free stream still fail the original assertions.
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            WriteFreshnessState(1.0f, 100);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var subscription = sampler.Subscribe(intervalMs: 200, stop.Token);
            try
            {
                await using var stream = subscription.GetAsyncEnumerator(deadline.Token);
                Assert.True(await stream.MoveNextAsync(), "stream ended before initial sample");
                var initial = stream.Current;
                // Change value and heartbeat in ONE real producer transaction.
                WriteFreshnessState(2.0f, 101);
                MetricsSample changed;
                do
                {
                    Assert.True(await stream.MoveNextAsync(), "stream ended before changed sample");
                    changed = stream.Current;
                }
                while (changed.Heartbeat != 101 && subscription.DroppedCount == 0);
                if (subscription.DroppedCount == 0)
                {
                    samples.Add(initial);
                    samples.Add(changed);
                    break;
                }
            }
            finally
            {
                stop.Cancel();
                await subscription.ProducerCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        Assert.Equal(2, samples.Count);

        // Zone float freshness: same address, different values across samples.
        var v1 = samples[0].Deltas.First(d => d.Addr == 200);
        var v2 = samples[1].Deltas.First(d => d.Addr == 200);
        Assert.Equal(RegisterDelta.ValueOneofCase.FloatValue, v1.ValueCase);
        Assert.Equal(1.0f, v1.FloatValue, 3);
        Assert.Equal(2.0f, v2.FloatValue, 3);

        // Header freshness surfaces through the proto header fields (sensor-at-send,
        // NOT a cached first read): heartbeat advanced 100 → 101 between samples.
        Assert.Equal(100, samples[0].Heartbeat);
        Assert.Equal(101, samples[1].Heartbeat);
    }

    private void WriteFreshnessState(float value, ushort heartbeat)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        WriteRawWords((200, (ushort)(bits & 0xffff)), (201, (ushort)(bits >> 16)),
            (BusLayout.RegHeartbeat, heartbeat));
    }

    // ═══ (б) drop-oldest: медленный consumer теряет сэмплы, очередь не копится ═══

    [Fact]
    public async Task DropOldest_SlowConsumerLosesSamples_BacklogNeverAccumulates()
    {
        using var reader = CreateOpenBus(out var writer);
        using var producer = writer;
        writer.WriteFloat(200, 1.0f);
        WriteHeader(comPort: 1, caseState: 0, caseLanState: 0, heartbeat: 1);

        var sampler = CreateSampler(reader);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // The initial full sample can be evicted before this consumer runs.
        // Retry only observed initial loss; wrong values on a loss-free attempt fail.
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var subscription = sampler.Subscribe(intervalMs: 200, stop.Token);
            try
            {
                MetricsSample first = await FirstSampleAsync(subscription, deadline.Token);
                if (subscription.DroppedCount != 0) continue;
                Assert.Equal(1.0f, first.Deltas.First(d => d.Addr == 200).FloatValue, 3);

                // Mark later samples, then wait for actual overflow while the
                // consumer is paused. Runner speed is not the verdict.
                WriteHeader(comPort: 1, caseState: 0, caseLanState: 0, heartbeat: 2);
                long baseline = subscription.DroppedCount;
                while (subscription.DroppedCount < baseline + 2)
                    await Task.Delay(20, deadline.Token);

                // Change the marker AFTER the queue is full. DropNewest would
                // retain heartbeat 2; DropOldest must replace it with heartbeat 3.
                WriteHeader(comPort: 1, caseState: 0, caseLanState: 0, heartbeat: 3);
                long markedBaseline = subscription.DroppedCount;
                while (subscription.DroppedCount < markedBaseline + 2)
                    await Task.Delay(20, deadline.Token);

                // Stop production before draining. Exactly ONE retained sample
                // must be the newer heartbeat; a FIFO/backlog or DropNewest
                // implementation fails without relying on timestamp age.
                stop.Cancel();
                await subscription.ProducerCompletion.WaitAsync(deadline.Token);
                var retained = new List<MetricsSample>();
                await foreach (var sample in subscription.WithCancellation(deadline.Token))
                    retained.Add(sample);

                var resumed = Assert.Single(retained);
                Assert.Equal(3, resumed.Heartbeat);
                Assert.True(subscription.DroppedCount >= markedBaseline + 2);
                break;
            }
            finally
            {
                stop.Cancel();
                await subscription.ProducerCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static async Task<MetricsSample> FirstSampleAsync(
        MetricsSubscription subscription, CancellationToken ct = default)
    {
        await foreach (var s in subscription.WithCancellation(ct))
            return s;
        throw new InvalidOperationException("stream ended before a sample arrived");
    }

    private void WriteDeltaState(float value, ushort heartbeat)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        WriteRawWords((202, (ushort)(bits & 0xffff)), (203, (ushort)(bits >> 16)),
            (BusLayout.RegHeartbeat, heartbeat));
    }

    // ═══ (в) delta-корректность: первый полный, второй только изменённые ═══

    [Fact]
    public async Task Deltas_FirstSampleCarriesAllLiveRegisters_SecondOnlyChanged()
    {
        using var reader = CreateOpenBus(out var writer);
        writer.WriteFloat(6, 6.0f);
        writer.WriteFloat(202, 10.0f);
        writer.WriteFloat(598, 598.0f);

        // addr 4..5 = Unix timestamp int32 RAW BITS (CDAB word order:
        // reg[4] = low word, reg[5] = high word). Words are computed from the
        // value — no hand-rolled hex.
        const int TimestampValue = 1700000000;
        WriteRawWords(
            (4, unchecked((ushort)(TimestampValue & 0xFFFF))),
            (5, unchecked((ushort)(TimestampValue >> 16))));

        WriteHeader(comPort: 2, caseState: 7, caseLanState: 0, heartbeat: 7);

        var sampler = CreateSampler(reader);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        MetricsSample full;
        MetricsSample delta;
        // DropOldest deliberately cannot promise delivery of the producer's first
        // full sample to a stalled consumer. Only a proven loss makes an attempt
        // inconclusive; an incorrect full/delta on a loss-free stream still fails.
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            // Both changed values are one real MMF transaction, not two snapshots.
            WriteDeltaState(10.0f, 7);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var subscription = sampler.Subscribe(intervalMs: 200, stop.Token);
            try
            {
                await using var stream = subscription.GetAsyncEnumerator(deadline.Token);
                Assert.True(await stream.MoveNextAsync(), "stream ended before full sample");
                full = stream.Current;
                WriteDeltaState(20.0f, 8);
                do
                {
                    Assert.True(await stream.MoveNextAsync(), "stream ended before changed sample");
                    delta = stream.Current;
                }
                while (delta.Heartbeat != 8 && subscription.DroppedCount == 0);
                if (subscription.DroppedCount == 0) break;
            }
            finally
            {
                stop.Cancel();
                await subscription.ProducerCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // ── Sample 1: FULL — header regs 0..3 + every CDAB pair 4..598 ──
        var addrs = full.Deltas.Select(d => d.Addr).ToArray();
        Assert.Equal(302, addrs.Length); // 4 single words + (598-4)/2+1 = 298 pairs
        Assert.Equal(addrs.Length, addrs.Distinct().Count());

        foreach (var addr in new uint[] { 0, 1, 2, 3 })
        {
            var d = full.Deltas.Single(x => x.Addr == addr);
            Assert.Equal(RegisterDelta.ValueOneofCase.Int32Value, d.ValueCase);
        }

        // addr 4..5 surface as a SINGLE int32 delta carrying the raw bits.
        var ts = full.Deltas.Single(x => x.Addr == 4);
        Assert.Equal(RegisterDelta.ValueOneofCase.Int32Value, ts.ValueCase);
        Assert.Equal(TimestampValue, ts.Int32Value);

        // All other pairs decode as float CDAB.
        Assert.Equal(6.0f, full.Deltas.Single(x => x.Addr == 6).FloatValue, 3);
        Assert.Equal(10.0f, full.Deltas.Single(x => x.Addr == 202).FloatValue, 3);
        Assert.Equal(598.0f, full.Deltas.Single(x => x.Addr == 598).FloatValue, 3);

        // ── Sample 2: ONLY the two changed addresses ──
        Assert.Equal(2, delta.Deltas.Count);
        var hb = delta.Deltas.Single(x => x.Addr == 3);
        Assert.Equal(RegisterDelta.ValueOneofCase.Int32Value, hb.ValueCase);
        Assert.Equal(8, hb.Int32Value);
        var f = delta.Deltas.Single(x => x.Addr == 202);
        Assert.Equal(RegisterDelta.ValueOneofCase.FloatValue, f.ValueCase);
        Assert.Equal(20.0f, f.FloatValue, 3);

        // Unchanged registers (incl. untouched timestamp pair) are ABSENT.
        Assert.DoesNotContain(delta.Deltas, d => d.Addr == 4);
        Assert.DoesNotContain(delta.Deltas, d => d.Addr == 200);
        Assert.DoesNotContain(delta.Deltas, d => d.Addr == 6);
    }

    // ═══ (г) clamp min 200 + флаг ═══

    [Fact]
    public async Task Clamp_RequestBelowMinimum_RunsAt200AndSetsFlag()
    {
        using var reader = CreateOpenBus(out var writer);
        writer.WriteFloat(200, 1.0f);
        WriteHeader(comPort: 0, caseState: 0, caseLanState: 0, heartbeat: 1);

        var sampler = CreateSampler(reader);
        var subscription = sampler.Subscribe(intervalMs: 50); // below minimum

        var sw = Stopwatch.StartNew();
        int count = 0;
        await foreach (var sample in subscription)
        {
            Assert.True(sample.IntervalClamped, "interval_clamped must be set when the request was below 200 ms");
            count++;
            if (count == 2) break;
        }
        sw.Stop();

        // Two ticks at the effective 200 ms cadence — not at the requested 50 ms.
        Assert.True(sw.ElapsedMilliseconds >= 180,
            $"two clamped ticks took {sw.ElapsedMilliseconds} ms; clamp to 200 ms is not enforced");
    }

    [Fact]
    public async Task NoClamp_RequestAtOrAboveMinimum_FlagClear()
    {
        using var reader = CreateOpenBus(out var writer);
        writer.WriteFloat(200, 1.0f);
        WriteHeader(comPort: 0, caseState: 0, caseLanState: 0, heartbeat: 1);

        var sampler = CreateSampler(reader);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscription = sampler.Subscribe(intervalMs: 1000, cts.Token);

        MetricsSample first = await FirstSampleAsync(subscription, cts.Token);
        Assert.False(first.IntervalClamped, "no clamp flag when interval_ms >= 200");
    }

    // ═══ (д) bus-closed → fail-fast IOException (¬молчаливые фейки) ═══

    [Fact]
    public async Task BusClosed_StreamFailsFastWithIOException_NoSilentFakeSamples()
    {
        // Reader against a path where NO bus file exists — never opened.
        var missingPath = Path.Combine(_dir, "absent", "bus.dat");
        using var reader = new BusReader(missingPath, _mapName, _mutexName);

        var sampler = CreateSampler(reader);
        var subscription = sampler.Subscribe(intervalMs: 200);

        var ex = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in subscription) { /* no sample ever arrives */ }
        });

        // Honest, actionable message — not a silent fake sample with heartbeat=-1.
        Assert.Contains("bus is not open", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missingPath, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Subscribe_RejectsNullReader() =>
        Assert.Throws<ArgumentNullException>(() => new MetricsSampler(null!));

    // ═══ (е) termination law: pre-cancelled токен ⇒ стрим ЗАВЕРШАЕТСЯ пустым
    //        (¬ вечное ожидание первого сэмпла из-за незапущенного producer'а). ═══

    [Fact]
    public async Task Subscribe_PreCancelledToken_StreamCompletesEmpty_NoHang()
    {
        using var reader = CreateOpenBus(out var writer);
        writer.WriteFloat(200, 1.0f);
        WriteHeader(comPort: 0, caseState: 0, caseLanState: 0, heartbeat: 1);

        var sampler = CreateSampler(reader);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancelled BEFORE Subscribe — producer must still complete the channel

        var subscription = sampler.Subscribe(intervalMs: 200, cts.Token);

        int samples = 0;
        await foreach (var _ in subscription)
            samples++;

        Assert.Equal(0, samples); // completed (empty), not hung
    }
}
