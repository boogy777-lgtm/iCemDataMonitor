using System.Diagnostics;
using System.IO.Pipes;
using iCemDataMonitor.Contracts;
using iCemDataMonitor.Core.Config;
using iCemDataMonitor.Host.Tray;

namespace iCemDataMonitor.Host.Tests;

// ═══════════════════════════════════════════════════════════════════════════
// 2026-08-27 LIVE PROBE of the console-X fix (owner decree #4): closing the
// console window must HIDE the console and keep the HOST PROCESS ALIVE.
//
// The .NET trap this guards: ConsoleLifetime registers its own
// SetConsoleCtrlHandler (→ StopApplication) while the host starts, AFTER
// ConsoleCloseGuard.Install(). Windows dispatches handlers in REVERSE
// registration order, so ConsoleLifetime would kill the host on X. The fix is
// ConsoleCloseGuard.Reinstall() right after app.StartAsync() in Program.cs —
// this test spawns the REAL host exe in its own console, sends a REAL
// WM_CLOSE to that console window (exactly what the X button does), and
// asserts the process is still alive.
//
// SPAWN SHAPE (live-verified): cmd /c start "" "<exe>" --port 0 --ini "<ini>"
// via ProcessStartInfo.Arguments — this exact shape boots the host in a FRESH
// console of its own (a shared console would route the WM_CLOSE to the TEST
// console). Boot evidence: the host's startup.log must gain a
// "console close guard re-registered at dispatch head" line — the definitive
// proof the dispatch-order fix ran before the close.
//
// Environment conflicts (a LIVE host holding the single-instance mutex or the
// named pipe) make the spawned host exit(1) or fail to bind — the test then
// reports an honest SKIP-ish no-op message and passes; run with
//   dotnet test --filter "Category!=LiveProbe"
// when the product host runs. (No Xunit.SkippableFact dependency: the
// environment conflict is reported in the console output, not as a failure.)
//
// The class lives in the HostKestrelSerialCollection: the spawned host binds
// the SAME named pipe as every other real-Kestrel test, so it must never
// overlap them.
// ═══════════════════════════════════════════════════════════════════════════

[Collection(HostKestrelSerialCollection.Name)]
[Trait("Category", "LiveProbe")]
public class ConsoleCloseProbeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _iniPath;
    private Process? _host;

    public ConsoleCloseProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ccprobe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _iniPath = Path.Combine(_dir, "station.ini");
        // All layers off; --port 0 makes the host bind an EPHEMERAL TCP port.
        File.WriteAllText(_iniPath,
            IniConfig.DefaultContent.Replace("modbus_on=1", "modbus_on=0"));
    }

    public void Dispose()
    {
        try { _host?.Kill(entireProcessTree: true); } catch { /* best effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void RealHost_ConsoleClose_HidesConsole_ProcessSurvives()
    {
        var hostExe = Path.Combine(AppContext.BaseDirectory, "iCemDataMonitor.Host.exe");
        if (!File.Exists(hostExe))
        {
            Console.WriteLine("[probe] SKIP-ish: host exe not found next to the test host " +
                              $"(expected {hostExe}) — build iCemDataMonitor.Host first");
            return;
        }

        // Environment conflict: a live host holds the single-instance mutex —
        // the spawned host would exit(1) fail-fast; there is nothing honest to
        // assert. Report as environment conflict (owner instruction), not a
        // regression.
        if (MutexProbe.IsTaken(SingleInstanceGuard.DefaultMutexName))
        {
            Console.WriteLine("[probe] SKIP-ish: environment conflict — a live host holds " +
                              $"mutex {SingleInstanceGuard.DefaultMutexName} (filter Category!=LiveProbe)");
            return;
        }

        var spawnedAt = DateTime.Now;
        SpawnHostInOwnConsole(hostExe);

        // 1. Wait until the host has bound its named pipe (Kestrel up), then
        //    give the post-StartAsync Reinstall() a generous head start — the
        //    close must land when the guard IS at the dispatch head.
        Assert.True(WaitForPipe(TimeSpan.FromSeconds(40)),
            "probe: the host never bound its named pipe — it failed to boot. " +
            $"Startup log tail: {Environment.NewLine}{StartupLogTail()}");
        Thread.Sleep(TimeSpan.FromSeconds(3));

        _host = FindHostProcess(spawnedAt)
            ?? throw new InvalidOperationException(
                "probe: the host process disappeared after binding the pipe. " +
                $"Startup log tail: {Environment.NewLine}{StartupLogTail()}");
        Assert.False(_host.HasExited, "probe: the host died during boot before the close");

        // 2. Boot evidence: the dispatch-order fix line MUST be in the log —
        //    without it the close below would race an un-re-registered guard.
        Assert.True(StartupLogContains("re-registered at dispatch head"),
            "probe: startup.log never gained the re-registration evidence line — " +
            "Program.cs did NOT run ConsoleCloseGuard.Reinstall() after StartAsync?");

        // 3. A REAL X click: a no-console helper attaches to the host's
        //    console and posts WM_CLOSE to its console window. conhost then
        //    sends CTRL_CLOSE_EVENT to every attached process — exactly the
        //    X-button path.
        SendConsoleClose(_host.Id);

        // 4. The LAW: the process must SURVIVE the console close.
        Thread.Sleep(TimeSpan.FromSeconds(4));
        Assert.False(_host.HasExited,
            "probe: the host process DIED on console X — the dispatch-order fix " +
            "(ConsoleCloseGuard.Reinstall after StartAsync) is broken or ConsoleLifetime " +
            "ran before the guard");

        // 5. Stop the spawned host before the next serialized Kestrel test.
        _host.Kill(entireProcessTree: true);
        _host.WaitForExit(5_000);
    }

    /// <summary>Spawn the real host in a FRESH console window of its own.
    /// No .NET stdout redirection: the host's std handles land on its own
    /// console (live-verified), so no child ever holds a vstest pipe.</summary>
    private void SpawnHostInOwnConsole(string hostExe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"/c start \"\" \"{hostExe}\" --port 0 --ini \"{_iniPath}\"",
        };
        using var cmd = Process.Start(psi)
            ?? throw new InvalidOperationException("probe: failed to spawn cmd /c start");
        if (!cmd.WaitForExit(5_000))
        {
            try { cmd.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    private static Process? FindHostProcess(DateTime spawnedAt)
    {
        // The host is the NEWEST iCemDataMonitor.Host process (single-instance
        // guard guarantees there is no other).
        foreach (var p in Process.GetProcessesByName("iCemDataMonitor.Host"))
        {
            try
            {
                if (p.StartTime > spawnedAt && p.Id != Environment.ProcessId)
                    return p;
            }
            catch { /* raced with exit */ }
        }
        return null;
    }

    private static bool WaitForPipe(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".", HostChannelNames.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                pipe.Connect(1_000);
                return true; // connect succeeded ⇒ Kestrel bound the pipe
            }
            catch (TimeoutException) { }
            catch (IOException) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) { Thread.Sleep(250); }
        }
        return false;
    }

    private static string? StartupLogTail()
    {
        try
        {
            var path = iCemDataMonitor.Host.Core.StationLogPaths.HostStartupLog;
            if (!File.Exists(path))
                return $"(no startup.log at {path})";
            var lines = File.ReadLines(path).TakeLast(10);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"(startup.log read failed: {ex.Message})";
        }
    }

    private static bool StartupLogContains(string fragment)
    {
        try
        {
            var path = iCemDataMonitor.Host.Core.StationLogPaths.HostStartupLog;
            if (!File.Exists(path))
                return false;
            return File.ReadLines(path).TakeLast(20)
                .Any(l => l.Contains(fragment, StringComparison.Ordinal));
        }
        catch
        {
            return false; // evidence channel broken — the survival assert still decides
        }
    }

    /// <summary>Post WM_CLOSE to the HOST's console window from a dedicated
    /// no-console helper: FreeConsole (a process can attach only when it has
    /// none) → AttachConsole(host) → GetConsoleWindow (now the host's
    /// console) → PostMessage(WM_CLOSE). conhost then delivers CTRL_CLOSE to
    /// every attached process. The helper itself may be terminated BY that
    /// event (default handler) — expected; a kill fallback covers any helper
    /// that survives.</summary>
    private static void SendConsoleClose(int hostPid)
    {
        // Single-quoted here-string: no escaping of the DllImport quotes.
        var ps = string.Join("\n", new[]
        {
            "$src = @'",
            "using System;",
            "using System.Runtime.InteropServices;",
            "public static class K32 {",
            "  [DllImport(\"kernel32.dll\", SetLastError=true)] public static extern bool AttachConsole(uint pid);",
            "  [DllImport(\"kernel32.dll\", SetLastError=true)] public static extern bool FreeConsole();",
            "  [DllImport(\"kernel32.dll\")] public static extern IntPtr GetConsoleWindow();",
            "  [DllImport(\"user32.dll\")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);",
            "}",
            "'@",
            $"$pidArg = {hostPid}",
            "Add-Type -TypeDefinition $src",
            "[K32]::FreeConsole() | Out-Null",
            "[K32]::AttachConsole($pidArg) | Out-Null",
            "$h = [K32]::GetConsoleWindow()",
            "if ($h -ne [IntPtr]::Zero) { [K32]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }",
            "Start-Sleep -Milliseconds 600",
            "[K32]::FreeConsole() | Out-Null",
            "exit 0",
        });

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            // Drained asynchronously so the helper can never block on its own
            // output; the write-end dies with the helper (or the kill below).
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + ps,
        };
        using var helper = Process.Start(psi)
            ?? throw new InvalidOperationException("probe: failed to spawn the close helper");
        _ = helper.StandardOutput.ReadToEndAsync();
        _ = helper.StandardError.ReadToEndAsync();
        if (!helper.WaitForExit(10_000))
        {
            try { helper.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }
}
