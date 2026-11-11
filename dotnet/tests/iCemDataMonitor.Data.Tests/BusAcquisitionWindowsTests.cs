using System.IO.MemoryMappedFiles;
using iCemDataMonitor.Data.Bus;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

public sealed class BusAcquisitionWindowsTests
{
    [Fact]
    public async Task RealMmf_AbsentPresent_DeleteRecreate_ReopenFallbackNotFresh_TimeoutAndStopContention()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "c6_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Data.dat");
        var map = "C6Map_" + Guid.NewGuid().ToString("N");
        var mutexName = "C6Mutex_" + Guid.NewGuid().ToString("N");
        try
        {
            using var reader = new BusReader(path, map, mutexName);
            using var acquisition = new BusAcquisitionService(reader, TimeSpan.FromMilliseconds(200));
            acquisition.CaptureOnce();
            Assert.Null(acquisition.Current.Snapshot);
            Assert.False(acquisition.Current.IsOpen);
            BusTestFactory.CreateFile(path);
            BusTestFactory.WriteWord(path, 3, 41);
            using var producerFile = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            using var producerSection = MemoryMappedFile.CreateFromFile(producerFile, map, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            acquisition.CaptureOnce();
            var good = acquisition.Current;
            Assert.True(good.IsOpen);
            Assert.Equal(41, good.Snapshot!.Heartbeat);
            Assert.Equal(1, good.SuccessfulCaptureSequence);
            acquisition.CaptureOnce(); // identical persisted words/source time still constitute a new copy
            Assert.Equal(2, acquisition.Current.SuccessfulCaptureSequence);
            Assert.Equal(41, acquisition.Current.Snapshot!.Heartbeat);
            producerSection.Dispose();
            producerFile.Dispose();
            // Same legal Windows name-removal fixture as BusOpenTruthTableTests rows 11/12.
            File.Move(path, path + ".orphaned");
            acquisition.CaptureOnce();
            Assert.False(acquisition.Current.IsOpen);
            Assert.True(acquisition.Current.Snapshot!.Stale);
            for (int i = 0; i < 3; i++)
            {
                // Reader's repeated failed reopen returns its non-Stale _lastGood.
                acquisition.CaptureOnce();
                Assert.True(acquisition.Current.Snapshot!.Stale);
                Assert.Equal(2, acquisition.Current.SuccessfulCaptureSequence);
                Assert.False(reader.LastSnapshot!.Stale);
            }
            BusTestFactory.CreateFile(path);
            BusTestFactory.WriteWord(path, 3, 42);
            acquisition.CaptureOnce();
            Assert.True(acquisition.Current.IsOpen);
            Assert.Equal(42, acquisition.Current.Snapshot!.Heartbeat);
            Assert.Equal(3, acquisition.Current.SuccessfulCaptureSequence);

            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = Task.Factory.StartNew(() =>
            {
                using var mutex = new Mutex(false, mutexName);
                mutex.WaitOne();
                try { held.Set(); release.Wait(); }
                finally { mutex.ReleaseMutex(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
                acquisition.CaptureOnce();
                Assert.True(acquisition.Current.IsOpen);
                Assert.True(acquisition.Current.Snapshot!.Stale);
                Assert.Equal(3, acquisition.Current.SuccessfulCaptureSequence);
                Assert.NotNull(acquisition.Current.LastError);
                await acquisition.StartAsync(CancellationToken.None);
                // A real 500 ms timeout remains bounded, with stop joining the capture.
                await acquisition.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(acquisition.Current.IsOpen);
                Assert.True(acquisition.Current.Snapshot!.Stale);
            }
            finally { release.Set(); await holder; }
        }
        finally { Directory.Delete(dir, true); }
    }
}
