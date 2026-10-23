using System;
using System.IO;
using Burservis.iCem.DataMonitor.Bus;
using Xunit;

namespace Burservis.iCem.DataMonitor.Tests;

public class TapLogTests : IDisposable
{
    private readonly string _path;

    public TapLogTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "taplog_test_" + Guid.NewGuid().ToString("N") + ".log");
        TapLog.PathOverride = _path;
    }

    public void Dispose()
    {
        TapLog.PathOverride = null;
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // temp cleanup best-effort
        }
    }

    [Fact]
    public void Log_AppendsLine_WithTimestamp()
    {
        TapLog.Log("hello bus");
        TapLog.Log("second line");

        Assert.True(File.Exists(_path));
        string content = File.ReadAllText(_path);
        Assert.Contains("hello bus", content);
        Assert.Contains("second line", content);
        // two timestamped lines
        Assert.Equal(2, content.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Log_Null_IsNoOp_NoThrow()
    {
        TapLog.Log(null);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Log_ToMissingDirectory_IsSwallowed()
    {
        TapLog.PathOverride = @"C:\iCEM\__no_such_dir__\TapOut.log";
        TapLog.Log("should be swallowed"); // must not throw
    }

    [Fact]
    public void Rotation_Over25MB_KeepsTailHalf_AndPreservesNewestBytes()
    {
        // Operator log policy: cap 25 MB, keep the tail 12.5 MB on exceed,
        // swallow-all.
        Assert.Equal(25L * 1024 * 1024, TapLog.MaxBytes);
        Assert.Equal(12L * 1024 * 1024 + 512L * 1024, TapLog.KeepBytes);     // 12.5 MB

        // Build an over-cap file instantly: SetLength (sparse zeros) + a marker at the tail.
        long overCap = TapLog.MaxBytes + 4L * 1024 * 1024; // 29 MB
        using (var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.SetLength(overCap);
            byte[] marker = new byte[] { 0x4D, 0x41, 0x52, 0x4B, 0x3A, 0x21, 0x3F, 0x23 }; // "MARK:!?#"
            fs.Position = overCap - marker.Length;
            fs.Write(marker, 0, marker.Length);
        }

        TapLog.TruncateIfNeeded(_path);

        Assert.Equal(TapLog.KeepBytes, new FileInfo(_path).Length);          // tail half kept
        byte[] tail = File.ReadAllBytes(_path);
        Assert.Equal(0x4D, tail[tail.Length - 8]);                           // newest bytes survive
        Assert.Equal(0x23, tail[tail.Length - 1]);
    }

    [Fact]
    public void Rotation_UnderCap_IsNoOp()
    {
        TapLog.Log("small line");
        long before = new FileInfo(_path).Length;
        Assert.True(before < TapLog.MaxBytes);

        TapLog.TruncateIfNeeded(_path);

        Assert.Equal(before, new FileInfo(_path).Length); // untouched under cap
    }

    [Fact]
    public void DeleteOldLogs_RemovesOnlyExplicitlyOwnedStaleFile()
    {
        // Operator log policy: at the rotation check, *.log files older than
        // 7 days are deleted (BCL only, swallow-all).
        Assert.Equal(7, TapLog.MaxAgeDays);

        string dir = Path.GetDirectoryName(_path);
        string oldLog = Path.Combine(dir, "taplog_old_" + Guid.NewGuid().ToString("N") + ".log");
        string freshLog = Path.Combine(dir, "taplog_fresh_" + Guid.NewGuid().ToString("N") + ".log");
        string notLog = Path.Combine(dir, "taplog_keep_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(_path, "owned stale");
            File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddDays(-8));
            File.WriteAllText(oldLog, "stale");
            File.WriteAllText(freshLog, "fresh");
            File.WriteAllText(notLog, "keep");
            File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-8));    // older than 7 days
            File.SetLastWriteTimeUtc(freshLog, DateTime.UtcNow.AddDays(-1)); // within 7 days

            TapLog.DeleteOldLogs(_path);

            Assert.False(File.Exists(_path), "owned stale log must be deleted");
            Assert.True(File.Exists(oldLog), "unregistered old .log must survive");
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
        TapLog.DeleteOldLogs(@"C:\iCEM\__no_such_dir__\TapOut.log"); // must not throw
    }
}
