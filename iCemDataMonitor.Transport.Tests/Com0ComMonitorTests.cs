using System.Runtime.Versioning;
using iCemDataMonitor.Transport;

namespace iCemDataMonitor.Transport.Tests;

/// <summary>
/// Com0ComMonitor process-runner regression suite.
///
/// DEADLOCK REGRESSION (2026-08-26): RealSystemProcess.RunAndCapture drained
/// the child's pipes SEQUENTIALLY (stdout.ReadToEnd() → stderr.ReadToEnd()).
/// A child that writes more than the stderr pipe-buffer capacity (~4 KB on
/// Windows) while the parent is still blocked reading stdout fills its stderr
/// buffer, blocks forever on that write, never closes stdout ⇒ the parent's
/// stdout ReadToEnd never returns and the WaitForExit(15 s) timeout is never
/// reached — an unbounded hang of the com0com status refresh path. The drain
/// is now CONCURRENT (both readers started before any wait) with a Kill
/// fallback; these tests pin that behavior with a child that deterministically
/// overflows BOTH pipes (cmd.exe for-loops, ~40 KB per stream).
///
/// Windows-only by design (com0com domain, cmd.exe children) — attribute,
/// ¬ suppression (house rule debt_5).
/// </summary>
[SupportedOSPlatform("windows")]
public class Com0ComMonitorTests
{
    private static readonly ISystemProcess Runner = new Com0ComMonitor.RealSystemProcess();

    /// <summary>Run the real runner on a pool thread with a bounded verdict:
    /// a regression to sequential draining hangs FOREVER (the runner's own
    /// 15 s kill sits unreachable behind ReadToEnd), so the wait itself must
    /// carry the timeout and fail fast instead of hanging the suite.</summary>
    private static async Task<(int Code, string Stdout, string Stderr)> RunSafeAsync(string[] args)
    {
        var run = Task.Run(() =>
        {
            int code = Runner.RunAndCapture("cmd.exe", args, out var so, out var se);
            return (Code: code, Stdout: so, Stderr: se);
        });

        try
        {
            return await run.WaitAsync(TimeSpan.FromSeconds(90));
        }
        catch (TimeoutException)
        {
            Assert.Fail("RunAndCapture did not complete within 90 s — the stdout/stderr " +
                        "drain deadlocked (sequential ReadToEnd regression).");
            throw; // unreachable
        }
    }

    [Fact]
    public async Task RunAndCapture_BothStreamsCaptured_ExitCodePreserved()
    {
        var (code, stdout, stderr) = await RunSafeAsync(new[]
        {
            "/c", "echo OUT_MARKER & echo ERR_MARKER 1>&2",
        });

        Assert.Equal(0, code);
        Assert.Contains("OUT_MARKER", stdout);
        Assert.Contains("ERR_MARKER", stderr);
    }

    [Fact]
    public async Task RunAndCapture_PipeBufferOverflowOnBothStreams_DoesNotDeadlock()
    {
        // 4000 lines × ~10 B ≈ 40 KB per stream — 10× the Windows pipe buffer.
        // The child emits ALL stderr first: a sequential reader is parked in
        // stdout.ReadToEnd() while the child blocks on a full stderr pipe —
        // the exact historical hang, now provably survived.
        const int lines = 4000;
        var (code, stdout, stderr) = await RunSafeAsync(new[]
        {
            "/c",
            $"(for /L %i in (1,1,{lines}) do @echo E%i 1>&2) & (for /L %i in (1,1,{lines}) do @echo O%i)",
        });

        Assert.Equal(0, code);
        Assert.Contains($"O{lines}", stdout);
        Assert.Contains($"E{lines}", stderr);
        Assert.True(stdout.Split('\n').Length >= lines, $"stdout truncated: {stdout.Length} chars");
        Assert.True(stderr.Split('\n').Length >= lines, $"stderr truncated: {stderr.Length} chars");
    }

    [Fact]
    public async Task RunAndCapture_TimeoutedChild_Killed_ReturnsNonZero()
    {
        // A child that ignores everything and sleeps: the 15 s kill must fire
        // and the call must RETURN (bounded), surfacing whatever was captured.
        var run = Task.Run(() =>
        {
            int code = Runner.RunAndCapture("cmd.exe", new[] { "/c", "ping -n 60 127.0.0.1 > nul" },
                out var so, out var se);
            return (Code: code, Stdout: so, Stderr: se);
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (code, stdout, stderr) = await run.WaitAsync(TimeSpan.FromSeconds(60));
        sw.Stop();

        Assert.NotEqual(0, code);                       // killed ≠ success
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"RunAndCapture took {sw.Elapsed.TotalSeconds:0}s — the kill fallback did not fire");
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
    }
}
