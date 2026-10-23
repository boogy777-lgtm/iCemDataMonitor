using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Data.Telemetry;
using iCemDataMonitor.Sdk.Bus;
using iCemDataMonitor.Transport.Models;
using Xunit.Abstractions;

namespace iCemDataMonitor.Transport.Tests;

/// <summary>Same-binary legacy CALL-PATTERN comparator, not a benchmark of the historical binary or hardware.</summary>
public sealed class AcquisitionMeasurementsTests(ITestOutputHelper output)
{
    private static readonly MethodInfo Read = typeof(ModbusTcpPublisher).GetMethod("ProcessRead", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Write = typeof(ModbusTcpPublisher).GetMethod("ProcessWriteSingle", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // One-shot test-only handoff. The control thread issues Stop before the
    // in-flight source call continues into the real Reader's held-mutex wait.
    private sealed class StopCaptureProbe : IDisposable
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim StopRequested = new();
        public long CaptureEnteredTimestamp, ReaderStartedTimestamp, ReaderCompletedTimestamp;
        public void Dispose() { Entered.Dispose(); StopRequested.Dispose(); }
    }

    private sealed class CountingSource(BusReader reader) : IBusCaptureSource
    {
        public long Attempts, Successes;
        public StopCaptureProbe? NextStopProbe;
        public BusSnapshot? LastSnapshot => reader.LastSnapshot;
        public bool IsOpen => reader.IsOpen;
        public string? LastError => reader.LastError;
        public BusSnapshot? Snapshot()
        {
            Interlocked.Increment(ref Attempts);
            var probe = Interlocked.Exchange(ref NextStopProbe, null);
            if (probe is not null)
            {
                probe.CaptureEnteredTimestamp = Stopwatch.GetTimestamp();
                probe.Entered.Set();
                if (!probe.StopRequested.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Stop control thread did not acknowledge the in-flight capture");
            }
            var before = reader.LastSnapshot;
            BusSnapshot? value;
            if (probe is not null) probe.ReaderStartedTimestamp = Stopwatch.GetTimestamp();
            try { value = reader.Snapshot(); }
            finally
            {
                if (probe is not null) probe.ReaderCompletedTimestamp = Stopwatch.GetTimestamp();
            }
            // Healthy test carrier: each returned non-stale snapshot means one copy+decode.
            if (value is { Stale: false } && !ReferenceEquals(before, reader.LastSnapshot))
                Interlocked.Increment(ref Successes);
            return value;
        }
    }

    // Deliberately ONLY in tests: restores the old per-call capture PATTERN for comparison.
    private sealed class LegacyCallPattern(CountingSource source) : ILatestBusState
    {
        public LatestBusState Current
        {
            get
            {
                var started = Stopwatch.GetTimestamp();
                var snapshot = source.Snapshot();
                var done = Stopwatch.GetTimestamp();
                return new(snapshot, source.IsOpen, source.LastError, started, done, done, source.Successes);
            }
        }
    }

    private sealed class Cached : ILatestBusState
    {
        public LatestBusState State = new(null, false, null, null, null, null, 0);
        public int Reads;
        public LatestBusState Current { get { Reads++; return State; } }
    }

    private static byte[] ReadRegister(ModbusTcpPublisher publisher, int addr)
        => (byte[])Read.Invoke(publisher, new object[] { (byte)1, (byte)3, new byte[] { 1, 3, (byte)(addr >> 8), (byte)addr, 0, 1 } })!;
    private static void WriteRegister(ModbusTcpPublisher publisher, int addr, ushort value)
        => Write.Invoke(publisher, new object[] { (byte)1, new byte[] { 1, 6, (byte)(addr >> 8), (byte)addr, (byte)(value >> 8), (byte)value } });
    private static ushort Word(ModbusTcpPublisher publisher, int addr)
    {
        var bytes = ReadRegister(publisher, addr);
        return (ushort)((bytes[3] << 8) | bytes[4]);
    }

    [Fact]
    public void CachedPublication_EveryModbusTickOverwritesActiveFc06_HeaderBeforeStale_IndependentZonesAndReserveHold()
    {
        var words = new ushort[10001];
        words[0] = 3; words[4] = 0x00a3; words[5] = 0x6553;
        words[200] = 222; words[400] = 444;
        BusZoneInfo Active(ZoneKind kind) => new(kind, DateTime.MaxValue.Ticks, true, 1);
        var snapshot = new BusSnapshot(BusLayout.Magic, BusLayout.Version, BusLayout.LayoutVersion, 10001,
            words, Active(ZoneKind.ComMain), Active(ZoneKind.Lan), default, default, false, 123);
        var latest = new Cached { State = new(snapshot, true, null, 0, 0, 0, 1) };
        using var publisher = new ModbusTcpPublisher(latest, new ModbusOptions { Total = 650 });
        publisher.TickOnce();
        Assert.Equal(10001, publisher.RegisterCount);
        foreach (var addr in new[] { 0, 4, 200, 400, 700 }) WriteRegister(publisher, addr, 999);
        publisher.TickOnce(); // EXACT SAME publication must still overwrite active writes
        Assert.Equal(3, Word(publisher, 0));
        Assert.Equal(0x00a3, Word(publisher, 4));
        Assert.Equal(222, Word(publisher, 200));
        Assert.Equal(999, Word(publisher, 400)); // inactive RAW independently holds
        Assert.Equal(999, Word(publisher, 700)); // reserve holds
        latest.State = latest.State with { Snapshot = snapshot with { Stale = true, RegCount = 650, Words = words.Take(650).ToArray() } };
        WriteRegister(publisher, 0, 999);
        WriteRegister(publisher, 200, 999);
        publisher.TickOnce();
        Assert.Equal(650, publisher.RegisterCount);
        Assert.Equal(3, Word(publisher, 0));
        Assert.Equal(999, Word(publisher, 200));
        Assert.Equal(3, latest.Reads);
    }

    [Fact]
    public async Task RealMmf_CallPatternComparison_EmitsReproducibleMetrics()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("C6 measurement NOT EXECUTED: Windows named MMF required; Linux is not evidence.");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "c6_measure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Data.dat");
        var map = "C6MeasureMap_" + Guid.NewGuid().ToString("N");
        var mutexName = "C6MeasureMutex_" + Guid.NewGuid().ToString("N");
        var bytes = new byte[BusLayout.TotalFileBytes];
        BusLayout.MagicBytes.CopyTo(bytes, 0);
        BitConverter.GetBytes(BusLayout.Version).CopyTo(bytes, BusLayout.HeaderVersionOffset);
        BitConverter.GetBytes(BusLayout.LayoutVersion).CopyTo(bytes, BusLayout.HeaderLayoutVersionOffset);
        BitConverter.GetBytes(BusLayout.RegCount).CopyTo(bytes, BusLayout.HeaderRegCountOffset);
        BitConverter.GetBytes(DateTime.MaxValue.Ticks).CopyTo(bytes, BusLayout.ZoneLastWriteTickOffset(ZoneKind.Lan));
        bytes[BusLayout.ZoneActiveFlagOffset(ZoneKind.Lan)] = 1;
        File.WriteAllBytes(path, bytes);
        try
        {
            Emit(new { kind = "runtime", comparator = "same-binary legacy call-pattern, NOT historical 401 binary, NOT hardware proof", os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), cpu = Environment.ProcessorCount, registerCount = BusLayout.RegCount, periodMs = 200, phaseMs = 1500, copyDecodeCounts = "inferred 1:1 from successful healthy Reader.Snapshot, no Reader instrumentation", requestAllocations = "aggregate process estimate includes acquisition/ticks/tasks; excludes RPC serialization/network", requestProjection = "real RegisterDecoder", modbus = "actual TickOnce + actual private FC03 projection invoked by test reflection; no socket" });
            using (var allocationReader = new BusReader(path, map, mutexName))
            {
                allocationReader.Snapshot(); // warmup outside measurement
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 20; i++) allocationReader.Snapshot();
                Emit(new { kind = "isolated_sync_reader_capture_allocations", captures = 20, bytesPerCapture = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 20.0, method = "GetAllocatedBytesForCurrentThread; synchronous, no await" });
            }
            foreach (bool loaded in new[] { false, true })
            {
                await Phase(legacy: true, loaded);
                await Phase(legacy: false, loaded);
            }
            await Stop(held: false);
            await Stop(held: true);
        }
        finally { Directory.Delete(dir, true); }

        async Task Phase(bool legacy, bool loaded)
        {
            using var reader = new BusReader(path, map, mutexName);
            var source = new CountingSource(reader);
            using var owner = new BusAcquisitionService(source, TimeSpan.FromMilliseconds(200), TimeProvider.System);
            ILatestBusState latest = legacy ? new LegacyCallPattern(source) : owner;
            if (!legacy) await owner.StartAsync(CancellationToken.None);
            using var publisher = new ModbusTcpPublisher(latest, new ModbusOptions());
            var ticks = new ConcurrentBag<double>();
            var lateness = new ConcurrentBag<double>();
            var ages = new ConcurrentBag<double>();
            var toPublication = new ConcurrentBag<double>();
            var toModbus = new ConcurrentBag<double>();
            var updates = new ConcurrentDictionary<ushort, long>();
            var publishedMarkers = new HashSet<ushort>();
            var modbusMarkers = new HashSet<ushort>();
            using var run = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var watch = Stopwatch.StartNew();
            long allocated = GC.GetTotalAllocatedBytes(false);
            long requests = 0;
            var jobs = new List<Task>();
            double tickDue = 0;
            jobs.Add(Loop(200, () =>
            {
                lateness.Add(Math.Max(0, watch.Elapsed.TotalMilliseconds - tickDue));
                tickDue += 200;
                long start = Stopwatch.GetTimestamp();
                publisher.TickOnce();
                ticks.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                var word = Word(publisher, 200);
                if (updates.TryGetValue(word, out var changed) && modbusMarkers.Add(word))
                    toModbus.Add(Stopwatch.GetElapsedTime(changed).TotalMilliseconds);
            }));
            // Existing 1 Hz health/ring topology, even in idle mode.
            jobs.Add(Loop(1000, () => { _ = latest.Current; _ = latest.Current; }));
            if (loaded)
            {
                for (int i = 0; i < 4; i++) jobs.Add(Loop(10, () =>
                {
                    var state = latest.Current;
                    if (state.Snapshot is { } snapshot)
                    {
                        var words = new ushort[Math.Min(snapshot.Words.Count, 600)];
                        snapshot.CopyWordsTo(0, words);
                        for (int addr = 4; addr < 600; addr += 2) _ = RegisterDecoder.DecodePair(words, addr);
                    }
                    Interlocked.Increment(ref requests);
                }));
                jobs.Add(Loop(50, () => { _ = latest.Current; Interlocked.Increment(ref requests); })); // status-like legacy reg1 read
                jobs.Add(Loop(200, () => { _ = latest.Current; _ = latest.Current; })); // two independent subscribers
            }
            ushort marker = 0;
            jobs.Add(Loop(100, () =>
            {
                using var mutex = new Mutex(false, mutexName);
                mutex.WaitOne();
                try
                {
                    marker++;
                    updates[marker] = Stopwatch.GetTimestamp();
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    file.Position = BusLayout.BodyOffset + 200 * 2;
                    file.WriteByte((byte)(marker >> 8)); file.WriteByte((byte)marker);
                }
                finally { mutex.ReleaseMutex(); }
            }));
            // Observer does not call the legacy adapter (that would invent extra legacy captures).
            jobs.Add(Loop(2, () =>
            {
                var state = legacy ? null : owner.Current;
                if (state?.CompletedTimestamp is long completed)
                {
                    ages.Add(Stopwatch.GetElapsedTime(completed).TotalMilliseconds);
                    var value = state.Snapshot!.GetRegister(200);
                    if (updates.TryGetValue(value, out var changed) && changed <= completed && publishedMarkers.Add(value))
                        toPublication.Add(Stopwatch.GetElapsedTime(changed).TotalMilliseconds);
                }
            }));
            await Task.WhenAll(jobs);
            double seconds = watch.Elapsed.TotalSeconds;
            if (!legacy) await owner.StopAsync(CancellationToken.None);

            Emit(new { kind = "phase", pattern = legacy ? "legacy_call_pattern" : "new_acquisition", loaded, seconds, requests, captureAttempts = source.Attempts, successfulCopiesAndDecodesInferred = source.Successes, attemptsPerSecond = source.Attempts / seconds, successesPerSecond = source.Successes / seconds, aggregateProcessAllocationEstimateBytes = GC.GetTotalAllocatedBytes(false) - allocated, tickMs = Distribution(ticks), tickLatenessMs = Distribution(lateness), completedPublicationAgeMs = Distribution(ages), sourceUpdates = updates.Count, publicationLatency = "first marker visibility, sampled at 2 ms: upper bound; skipped source updates are not invented", modbusLatency = "first marker visibility at actual TickOnce/FC03 projection", updateToPublicationMs = Distribution(toPublication), updateToModbusVisibilityMs = Distribution(toModbus) });
            Assert.True(source.Attempts > 0);
            Assert.Equal(source.Attempts, source.Successes);
            if (!legacy) Assert.InRange(source.Attempts, 1, (long)Math.Ceiling(seconds / .2) + 2); // functional isolation: ~5/s regardless of requests

            async Task Loop(int intervalMs, Action action)
            {
                await Task.Yield();
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
                    do { action(); } while (await timer.WaitForNextTickAsync(run.Token));
                }
                catch (OperationCanceledException) when (run.IsCancellationRequested) { }
            }
        }

        async Task Stop(bool held)
        {
            using var reader = new BusReader(path, map, mutexName);
            var source = new CountingSource(reader);
            using var owner = new BusAcquisitionService(source, TimeSpan.FromMilliseconds(200), TimeProvider.System);
            await owner.StartAsync(CancellationToken.None);
            using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (owner.Current.Snapshot is null) await Task.Delay(2, startupDeadline.Token);
            if (!held)
            {
                long started = Stopwatch.GetTimestamp();
                await owner.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Emit(new { kind = "stop", heldMutex = false, captureWasInFlight = (bool?)null,
                    coverage = "idle load, no induced contention; capture overlap not instrumented", elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    readerStillOpenBeforeDiDisposal = reader.IsOpen, publishedOpen = owner.Current.IsOpen });
                Assert.False(owner.Current.IsOpen);
                Assert.True(reader.IsOpen);
                return;
            }

            using var probe = new StopCaptureProbe();
            using var release = new ManualResetEventSlim();
            var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = Task.Factory.StartNew(() =>
            {
                using var mutex = new Mutex(false, mutexName);
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Measurement mutex holder did not acquire the bus mutex");
                    try { acquired.TrySetResult(); release.Wait(); }
                    finally { mutex.ReleaseMutex(); }
                }
                catch (Exception ex) { acquired.TrySetException(ex); throw; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task<(long Started, long Completed, bool WasPending)>? control = null;
            try
            {
                await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                control = Task.Factory.StartNew(() =>
                {
                    controlReady.TrySetResult();
                    if (!probe.Entered.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("No armed source capture entered before stop measurement deadline");
                    try
                    {
                        long started = Stopwatch.GetTimestamp();
                        var stop = owner.StopAsync(CancellationToken.None);
                        bool wasPending = !stop.IsCompleted;
                        probe.StopRequested.Set(); // acknowledge issuance, not completion
                        stop.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                        return (started, Stopwatch.GetTimestamp(), wasPending);
                    }
                    finally { probe.StopRequested.Set(); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await controlReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Volatile.Write(ref source.NextStopProbe, probe);
                var measured = await control.WaitAsync(TimeSpan.FromSeconds(10));
                bool captureWasInFlight = probe.CaptureEnteredTimestamp <= measured.Started &&
                    measured.Started < probe.ReaderCompletedTimestamp && measured.WasPending;
                Emit(new { kind = "stop", heldMutex = true, captureWasInFlight,
                    coverage = "Stop issued during source capture; acknowledged before real Reader held-mutex wait; elapsed includes handoff",
                    elapsedMs = Stopwatch.GetElapsedTime(measured.Started, measured.Completed).TotalMilliseconds,
                    sourceCaptureEnteredTimestamp = probe.CaptureEnteredTimestamp, stopStartedTimestamp = measured.Started,
                    readerCallStartedTimestamp = probe.ReaderStartedTimestamp, readerCallCompletedTimestamp = probe.ReaderCompletedTimestamp,
                    stopCompletedTimestamp = measured.Completed, stopWasPendingAtHandoff = measured.WasPending,
                    readerStillOpenBeforeDiDisposal = reader.IsOpen, publishedOpen = owner.Current.IsOpen });
                Assert.True(captureWasInFlight, "Stop did not overlap the armed source capture; see stop timestamp diagnostics");
                Assert.True(measured.Started <= probe.ReaderStartedTimestamp);
                Assert.True(probe.ReaderStartedTimestamp < probe.ReaderCompletedTimestamp);
                Assert.True(probe.ReaderCompletedTimestamp <= measured.Completed);
                Assert.False(owner.Current.IsOpen);
                Assert.True(reader.IsOpen); // joined before caller/DI disposes the reader
            }
            finally
            {
                probe.StopRequested.Set();
                release.Set();
                await holder.WaitAsync(TimeSpan.FromSeconds(5));
                await owner.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                if (control is not null) await control.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static object Distribution(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double? At(double p) => sorted.Length == 0 ? null : sorted[(int)Math.Floor((sorted.Length - 1) * p)];
        return new { n = sorted.Length, p50 = At(.50), p95 = At(.95), p99 = At(.99) };
    }

    private void Emit(object value)
    {
        string line = JsonSerializer.Serialize(value);
        output.WriteLine(line);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !Directory.Exists(Path.Combine(root.FullName, ".git")) && !File.Exists(Path.Combine(root.FullName, ".git"))) root = root.Parent;
        var results = Path.Combine(root.FullName, "TestResults");
        Directory.CreateDirectory(results);
        File.AppendAllText(Path.Combine(results, "c6-mmf-measurements.log"), line + Environment.NewLine);
    }
}
