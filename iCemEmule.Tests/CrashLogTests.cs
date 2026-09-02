// CrashLogTests — pins for the engine crash-log policy (operator log policy
// 2026-08-26), modeled 1:1 on Burservis.iCem.DataMonitor.Tests\TapLogTests.cs:
// cap 25 MB, on exceed in-place rewrite keeping the tail half, swallow-all,
// lazy default-directory creation. Every test uses PathOverride — the
// production path is never touched.

using System.IO;
using Halliburton.iCemEmule.Core;
using Xunit;

namespace iCemEmule.Tests;

public class CrashLogTests : IDisposable
{
    private readonly string _path;

    public CrashLogTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "crashlog_test_" + Guid.NewGuid().ToString("N") + ".log");
        CrashLog.PathOverride = _path;
    }

    public void Dispose()
    {
        CrashLog.PathOverride = null;
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            // temp cleanup best-effort
        }
    }

    [Fact]
    public void DefaultPath_MatchesOperatorLogsPolicy()
    {
        // Policy: C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\<AppName>\
        Assert.Equal(
            @"C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\iCemEmule\crash.log",
            typeof(CrashLog)
                .GetField("DefaultPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null));
    }

    [Fact]
    public void Log_AppendsLine_WithTimestamp()
    {
        CrashLog.Log("unhandled: boom");
        CrashLog.Log("second line");

        Assert.True(File.Exists(_path));
        string content = File.ReadAllText(_path);
        Assert.Contains("unhandled: boom", content);
        Assert.Contains("second line", content);
        Assert.Equal(2, content.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Log_Null_IsNoOp_NoThrow()
    {
        CrashLog.Log(null);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Log_ToMissingDirectory_IsSwallowed()
    {
        CrashLog.PathOverride = Path.Combine(Path.GetTempPath(), "__no_such_dir_" + Guid.NewGuid().ToString("N") + "__", "crash.log");
        CrashLog.Log("should be swallowed"); // must not throw
    }

    [Fact]
    public void Rotation_Over25MB_KeepsTailHalf_AndPreservesNewestBytes()
    {
        // Canonical TapLog contract: cap 25 MB, keep the tail 12.5 MB on exceed.
        Assert.Equal(25L * 1024 * 1024, CrashLog.MaxBytes);
        Assert.Equal(12L * 1024 * 1024 + 512L * 1024, CrashLog.KeepBytes);   // 12.5 MB

        // Build an over-cap file instantly: SetLength (sparse zeros) + a marker at the tail.
        long overCap = CrashLog.MaxBytes + 4L * 1024 * 1024; // 29 MB
        using (var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.SetLength(overCap);
            byte[] marker = new byte[] { 0x4D, 0x41, 0x52, 0x4B, 0x3A, 0x21, 0x3F, 0x23 }; // "MARK:!?#"
            fs.Position = overCap - marker.Length;
            fs.Write(marker, 0, marker.Length);
        }

        CrashLog.TruncateIfNeeded(_path);

        Assert.Equal(CrashLog.KeepBytes, new FileInfo(_path).Length);        // tail half kept
        byte[] tail = File.ReadAllBytes(_path);
        Assert.Equal(0x4D, tail[tail.Length - 8]);                           // newest bytes survive
        Assert.Equal(0x23, tail[tail.Length - 1]);
    }

    [Fact]
    public void Rotation_UnderCap_IsNoOp()
    {
        CrashLog.Log("small line");
        long before = new FileInfo(_path).Length;
        Assert.True(before < CrashLog.MaxBytes);

        CrashLog.TruncateIfNeeded(_path);

        Assert.Equal(before, new FileInfo(_path).Length); // untouched under cap
    }

    [Fact]
    public void DeleteOldLogs_RemovesOnlyOldLogFiles_InSameDirectory()
    {
        Assert.Equal(7, CrashLog.MaxAgeDays);

        string dir = Path.GetDirectoryName(_path)!;
        string oldLog = Path.Combine(dir, "crashlog_old_" + Guid.NewGuid().ToString("N") + ".log");
        string freshLog = Path.Combine(dir, "crashlog_fresh_" + Guid.NewGuid().ToString("N") + ".log");
        string notLog = Path.Combine(dir, "crashlog_keep_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(oldLog, "stale");
            File.WriteAllText(freshLog, "fresh");
            File.WriteAllText(notLog, "keep");
            File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-8));    // older than 7 days
            File.SetLastWriteTimeUtc(freshLog, DateTime.UtcNow.AddDays(-1)); // within 7 days

            CrashLog.DeleteOldLogs(_path);

            Assert.False(File.Exists(oldLog), "old .log must be deleted");
            Assert.True(File.Exists(freshLog), "fresh .log must survive");
            Assert.True(File.Exists(notLog), "non-.log files must survive");
        }
        finally
        {
            foreach (string f in new[] { oldLog, freshLog, notLog })
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }

    [Fact]
    public void DeleteOldLogs_MissingDirectory_IsSwallowed()
    {
        CrashLog.DeleteOldLogs(Path.Combine(Path.GetTempPath(), "__no_such_dir_" + Guid.NewGuid().ToString("N") + "__", "crash.log")); // must not throw
    }
}
