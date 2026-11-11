using System.Text.RegularExpressions;
using iCemDataMonitor.Sdk.Diagnostics;

namespace iCemDataMonitor.Sdk.Tests;

// Owner log policy 2026-08-26 (canon TapLog, Burservis.iCem.DataMonitor):
// 25 MB cap, in-place tail-half rewrite, newest bytes survive, swallow-all,
// per-file serialization. Mirrors Burservis.iCem.DataMonitor.Tests\TapLogTests
// and adds the path-parameterized semantics (lazy dir creation, per-file gates).
public class StationFileLogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public StationFileLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stationfilelog_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "app.log");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // temp cleanup best-effort
        }
    }

    [Fact]
    public void Policy_Consts_Are_Pinned()
    {
        Assert.Equal(25L * 1024 * 1024, StationFileLog.MaxBytes);
        Assert.Equal(12L * 1024 * 1024 + 512L * 1024, StationFileLog.KeepBytes); // 12.5 MB tail half
        Assert.Equal(60, StationFileLog.RotationCheckInterval);
        Assert.Equal(@"C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs", StationFileLog.LogsRoot);
    }

    [Fact]
    public void AppLogDir_Joins_LogsRoot_With_AppName()
    {
        Assert.Equal(
            Path.Combine(StationFileLog.LogsRoot, "iCemDataMonitor.Host"),
            StationFileLog.AppLogDir("iCemDataMonitor.Host"));
    }

    [Fact]
    public void Write_AppendsLine_WithTimestamp()
    {
        StationFileLog.Write(_path, "hello station");
        StationFileLog.Write(_path, "second line");

        Assert.True(File.Exists(_path));
        string content = File.ReadAllText(_path);
        Assert.Contains("hello station", content);
        Assert.Contains("second line", content);

        string[] lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        // Canon TapLog format: UTC ISO-8601 "O" timestamp + space + payload.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z? hello station$", lines[0]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z? second line$", lines[1]);
    }

    [Fact]
    public void Write_NullLine_IsNoOp_NoThrow()
    {
        StationFileLog.Write(_path, null!);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Write_CreatesMissingDirectory_Lazily()
    {
        // Deviation from TapLog dictated by the directive: the per-app directory
        // is created on first write (best-effort), not a swallowed failure.
        Assert.False(Directory.Exists(_dir));
        StationFileLog.Write(_path, "first write creates the dir");
        Assert.True(Directory.Exists(_dir));
        Assert.Contains("first write creates the dir", File.ReadAllText(_path));
    }

    [Fact]
    public void Write_NeverThrows_OnIllegalPath()
    {
        // '\0' makes every IO call fail instantly — must be swallowed.
        StationFileLog.Write(Path.Combine(_dir, "ba\0d.log"), "swallowed");
        // A DIRECTORY passed as the file path fails too — swallowed.
        StationFileLog.Write(_dir, "swallowed");
    }

    [Fact]
    public void Rotation_UnderCap_IsNoOp()
    {
        StationFileLog.Write(_path, "small line");
        long before = new FileInfo(_path).Length;
        Assert.True(before < StationFileLog.MaxBytes);

        StationFileLog.TruncateIfNeeded(_path);

        Assert.Equal(before, new FileInfo(_path).Length); // untouched under cap
    }

    [Fact]
    public void Rotation_MissingFile_IsSwallowed_NoThrow()
    {
        StationFileLog.TruncateIfNeeded(Path.Combine(_dir, "__absent__.log"));
    }

    [Fact]
    public void Rotation_Over25MB_KeepsTailHalf_AndPreservesNewestBytes()
    {
        // Operator log policy: cap 25 MB, keep the tail 12.5 MB on exceed.
        // Build an over-cap file instantly: SetLength (sparse zeros) + a marker at the tail.
        // (The directory is created here directly — this test bypasses Write's
        // lazy directory creation on purpose.)
        Directory.CreateDirectory(_dir);
        long overCap = StationFileLog.MaxBytes + 4L * 1024 * 1024; // 29 MB
        using (var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.SetLength(overCap);
            byte[] marker = { 0x4D, 0x41, 0x52, 0x4B, 0x3A, 0x21, 0x3F, 0x23 }; // "MARK:!?#"
            fs.Position = overCap - marker.Length;
            fs.Write(marker);
        }

        StationFileLog.TruncateIfNeeded(_path);

        Assert.Equal(StationFileLog.KeepBytes, new FileInfo(_path).Length); // tail half kept
        byte[] tail = File.ReadAllBytes(_path);
        Assert.Equal(0x4D, tail[^8]);                                       // newest bytes survive
        Assert.Equal(0x23, tail[^1]);
    }

    [Fact]
    public async Task Concurrent_Writers_SameFile_LinesAreNeverTorn()
    {
        const int writers = 8;
        const int linesPerWriter = 50;

        var tasks = new Task[writers];
        for (int w = 0; w < writers; w++)
        {
            int id = w;
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < linesPerWriter; i++)
                {
                    StationFileLog.Write(_path, $"t{id}_w{i}");
                }
            });
        }
        await Task.WhenAll(tasks);

        string[] lines = File.ReadAllText(_path)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Every line arrived whole: count exact, each token present exactly once,
        // and every line ENDS with its own token (a torn line would break this).
        Assert.Equal(writers * linesPerWriter, lines.Length);

        var seen = new Dictionary<string, int>();
        foreach (string l in lines)
        {
            Match m = Regex.Match(l, @"t(\d+)_w(\d+)$");
            Assert.True(m.Success, $"torn or malformed line: '{l}'");
            string token = m.Value;
            seen[token] = seen.GetValueOrDefault(token) + 1;
        }

        Assert.Equal(writers * linesPerWriter, seen.Count); // no duplicates, none lost
        for (int w = 0; w < writers; w++)
        {
            for (int i = 0; i < linesPerWriter; i++)
            {
                Assert.Contains($"t{w}_w{i}", seen.Keys);
            }
        }
    }

    [Fact]
    public void Different_Files_Are_Written_Independently()
    {
        string other = Path.Combine(_dir, "other.log");

        StationFileLog.Write(_path, "main-1");
        StationFileLog.Write(other, "other-1");
        StationFileLog.Write(_path, "main-2");
        StationFileLog.Write(other, "other-2");

        string main = File.ReadAllText(_path);
        string oth = File.ReadAllText(other);
        Assert.Contains("main-1", main);
        Assert.Contains("main-2", main);
        Assert.DoesNotContain("other-", main);
        Assert.Contains("other-1", oth);
        Assert.Contains("other-2", oth);
        Assert.DoesNotContain("main-", oth);
    }
}
