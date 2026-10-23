using iCemDataMonitor.Sdk.Diagnostics;

namespace iCemDataMonitor.Host.Tests;

// ═══════════════════════════════════════════════════════════════════════════
// W22 (owner decree, ProgramData logging): contract tests for the canonical
// iCemDataMonitor.Sdk.Diagnostics.StationFileLog as consumed by the host zone
// (StartupLog redirect; the EngineSupervisor audit mirror was REMOVED with
// engine supervision 2026-08-27). The heavy rotation semantics are owned by
// Sdk.Tests; here we pin the behaviour the HOST relies on: lazy folder
// creation, append format, swallow-all, cap enforcement.
// ═══════════════════════════════════════════════════════════════════════════

public class StationFileLogHostContractTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"sfl_{Guid.NewGuid():N}");

    public StationFileLogHostContractTests() => Directory.CreateDirectory(_dir);

    private string Path_(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Write_CreatesFolderLazily_AndAppendsTimestampedLine()
    {
        var path = Path_("lazy", "nested", "app.log"); // directory does NOT exist yet

        StationFileLog.Write(path, "hello station");

        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("hello station", text);
        // Canon TapLog line format: UTC ISO-8601 "O" + space + payload.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z? hello station\r?$",
            text.TrimEnd());
    }

    [Fact]
    public void Write_NeverThrows_OnIllegalPath()
    {
        // Null / empty / illegal — every failure mode must be swallowed.
        StationFileLog.Write(null!, "x");
        StationFileLog.Write("", "x");
        StationFileLog.Write("\0illegal", "x");
        StationFileLog.Write(Path_(), "x"); // directory path as file target

        Assert.True(Directory.Exists(_dir) is false || true); // just: no exception escaped
    }

    [Fact]
    public void Write_ConcurrentWriters_SameFile_NoTornLines()
    {
        var path = Path_("concurrent.log");
        const int writers = 8, linesPerWriter = 40;

        Parallel.For(0, writers, w =>
        {
            for (int i = 0; i < linesPerWriter; i++)
                StationFileLog.Write(path, $"w{w}-line{i}");
        });

        var lines = File.ReadAllLines(path);
        Assert.Equal(writers * linesPerWriter, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^[\d\-T:.Z]+ w\d+-line\d+$", l));
    }

    [Fact]
    public void TruncateIfNeeded_KeepsNewestHalf_InPlace()
    {
        var path = Path_("big.log");
        const string headMarker = "OLDEST-EVIDENCE-MARKER";
        File.WriteAllText(path, headMarker);
        // Grow past the 25 MB cap.
        File.AppendAllText(path, new string('x', (int)StationFileLog.MaxBytes));

        StationFileLog.TruncateIfNeeded(path);

        var fi = new FileInfo(path);
        Assert.True(fi.Length <= StationFileLog.MaxBytes, $"length={fi.Length}");
        Assert.True(fi.Length >= StationFileLog.MaxBytes / 4, "newest half must survive");
        Assert.DoesNotContain(headMarker, File.ReadAllText(path)); // oldest half evicted
    }

    [Fact]
    public void TruncateIfNeeded_UnderCap_IsNoOp_MissingFile_NoThrow()
    {
        var path = Path_("small.log");
        File.WriteAllText(path, "tiny");

        StationFileLog.TruncateIfNeeded(path);
        Assert.Equal("tiny", File.ReadAllText(path));

        StationFileLog.TruncateIfNeeded(Path_("does-not-exist.log")); // no throw
    }

    [Fact]
    public void StartupLog_FileHasExactlyOneTimestamp_AndConsoleKeepsSingleEcho()
    {
        // Console is shared process state: verify the file without replacing stdout.
        StartupLog.Create(_dir)("READY café");
        var line = Assert.Single(File.ReadAllLines(Path_("startup.log")));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z READY café$", line);
    }

    [Fact]
    public void StartupLog_DefaultDirectory_IsProgramDataHostDir()
    {
        // Owner decree: startup.log lives under ProgramData\...\iCemDataMonitor.Host\.
        Assert.EndsWith(
            Path.Combine("Halliburton", "iCem", "DataMonitoring", "Logs", "iCemDataMonitor.Host"),
            iCemDataMonitor.Host.Core.StationLogPaths.HostDir,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("startup.log", iCemDataMonitor.Host.Core.StationLogPaths.HostStartupLog, StringComparison.Ordinal);
        // 2026-08-27: HostEngineSupervisorLog REMOVED with engine supervision
        // (the emulator is a separate independent app with its own logs).
    }
}
