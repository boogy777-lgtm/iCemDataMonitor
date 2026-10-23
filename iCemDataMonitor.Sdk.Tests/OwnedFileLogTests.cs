using System.Text;
using iCemDataMonitor.Sdk.Diagnostics;

namespace iCemDataMonitor.Sdk.Tests;

public sealed class OwnedFileLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ownedlogs_" + Guid.NewGuid().ToString("N"));
    public OwnedFileLogTests() => Directory.CreateDirectory(_directory);
    private string Log(string name = "owned.log") => Path.Combine(_directory, name);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Rotation_ExactBoundaryAndExactTailBytes(int delta)
    {
        var path = Log();
        using (var stream = File.Create(path))
        {
            stream.SetLength(StationFileLog.MaxBytes + delta);
            stream.Position = stream.Length - StationFileLog.KeepBytes;
            stream.Write(new byte[] { 0xA1, 0xB2 }); // first bytes of retained half
            stream.Position = stream.Length - 4;
            stream.Write(Encoding.UTF8.GetBytes("éЯ"));
        }
        StationFileLog.TruncateIfNeeded(path);
        Assert.Equal(delta > 0 ? StationFileLog.KeepBytes : StationFileLog.MaxBytes + delta, new FileInfo(path).Length);
        var bytes = File.ReadAllBytes(path);
        if (delta > 0) Assert.Equal(new byte[] { 0xA1, 0xB2 }, bytes[..2]);
        Assert.Equal(Encoding.UTF8.GetBytes("éЯ"), bytes[^4..]);
    }

    [Fact]
    public void Rotation_ChecksBeforeSixtiethAppend_FormattedUtf8HasNoExtraPrefixOrBom()
    {
        var path = Log("кириллица.log");
        using (var stream = File.Create(path)) stream.SetLength(StationFileLog.MaxBytes + 1);
        for (int i = 0; i < 59; i++) StationFileLog.WriteFormatted(path, "é\r\n");
        Assert.Equal(StationFileLog.MaxBytes + 1 + 59 * 4, new FileInfo(path).Length);
        StationFileLog.WriteFormatted(path, "[local tid=7] Я\r\n");
        Assert.Equal(StationFileLog.KeepBytes + Encoding.UTF8.GetByteCount("[local tid=7] Я\r\n"), new FileInfo(path).Length);
        Assert.EndsWith("[local tid=7] Я\r\n", File.ReadAllText(path));
        var fresh = Log("fresh.log");
        StationFileLog.WriteFormatted(fresh, "[12:34:56.789 tid=7] é\r\n");
        Assert.Equal(Encoding.UTF8.GetBytes("[12:34:56.789 tid=7] é\r\n"), File.ReadAllBytes(fresh));
    }

    [Fact]
    public void OwnedRetention_AndClear_NeverScanDirectory_AndContinuePastBadPaths()
    {
        var old = Log("old.log");
        var fresh = Log("fresh.log");
        var foreign = new[] { Log("foreign.log"), Log("Data.dat"), Log("config.ini"), Log("nested/unregistered.log") };
        foreach (var file in foreign.Concat(new[] { old, fresh }))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "keep");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-8));
        }
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);
        StationFileLog.DeleteOwnedOlderThan(new[] { "\0bad", old, fresh }, DateTime.UtcNow.AddDays(-7));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.All(foreign, file => Assert.True(File.Exists(file)));
        using (var held = new FileStream(fresh, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            StationFileLog.ClearOwned(new[] { "\0bad", fresh, Log("missing.log"), old });
            // Windows rejects deletion; Unix permits unlinking an open file. Both are best-effort.
            if (OperatingSystem.IsWindows()) Assert.True(File.Exists(fresh));
        }
        StationFileLog.ClearOwned(new[] { fresh });
        StationFileLog.Write(fresh, "after clear");
        Assert.Matches(@"^\d{4}-.*Z after clear$", Assert.Single(File.ReadAllLines(fresh)));
        Assert.All(foreign, file => Assert.True(File.Exists(file)));
    }

    private sealed class PausedEncoding(ManualResetEventSlim entered, ManualResetEventSlim release) : UTF8Encoding(false)
    {
        public override byte[] GetPreamble()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("test release missing");
            return base.GetPreamble();
        }
        public override ReadOnlySpan<byte> Preamble => GetPreamble();
    }

    [Fact]
    public void SamePathAppendClearTruncate_ShareCoordinator_WhileOtherPathProgresses()
    {
        var path = Log();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var clearStarted = new ManualResetEventSlim();
        using var truncateStarted = new ManualResetEventSlim();
        using var clearDone = new ManualResetEventSlim();
        using var truncateDone = new ManualResetEventSlim();
        var writer = new Thread(() => OwnedFileLog.Write(path, "first\n", false, 0, new PausedEncoding(entered, release)));
        var clearer = new Thread(() => { clearStarted.Set(); StationFileLog.ClearOwned(new[] { OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path }); clearDone.Set(); });
        var truncater = new Thread(() => { truncateStarted.Set(); StationFileLog.TruncateIfNeeded(path); truncateDone.Set(); });
        writer.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "writer must enter append while holding coordinator");
            clearer.Start();
            truncater.Start();
            Assert.True(clearStarted.Wait(TimeSpan.FromSeconds(30)));
            Assert.True(truncateStarted.Wait(TimeSpan.FromSeconds(30)));
            StationFileLog.WriteFormatted(Log("independent.log"), "independent\n");
            Assert.Equal("independent\n", File.ReadAllText(Log("independent.log")));
            Assert.False(clearDone.IsSet);
            Assert.False(truncateDone.IsSet);
        }
        finally
        {
            release.Set();
            Assert.True(writer.Join(TimeSpan.FromSeconds(30)));
            if (clearer.ThreadState != ThreadState.Unstarted) Assert.True(clearer.Join(TimeSpan.FromSeconds(30)));
            if (truncater.ThreadState != ThreadState.Unstarted) Assert.True(truncater.Join(TimeSpan.FromSeconds(30)));
        }
        StationFileLog.WriteFormatted(path, "last whole line\n");
        Assert.Equal("last whole line\n", File.ReadAllText(path));
    }
}
