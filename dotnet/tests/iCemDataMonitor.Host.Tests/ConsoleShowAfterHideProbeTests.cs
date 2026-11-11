using System.Diagnostics;

namespace iCemDataMonitor.Host.Tests;

// ═══════════════════════════════════════════════════════════════════════════
// 2026-08-28 LIVE PROBE #2 — the Show-after-Hide cycle on a REAL console
// (production evidence, owner's startup.log 10:58:10:
//   10:58:08 [console] console hidden (SW_HIDE)
//   10:58:10 [console] ShowWindow FAILED - console stayed hidden   ← THE LIE
//   10:58:16 [console] console hidden (SW_HIDE)  ← the HIDE arm — the tray
//            picks Hide ONLY when IsWindowVisible==true ⇒ the console WAS
//            visible between 10:58:10 and 10:58:16!).
//
// ROOT CAUSE pinned here: ShowWindow's BOOL return is «was the window visible
// BEFORE the call» — NOT success (MSDN). On a hidden console a SUCCESSFUL
// show returns FALSE. The old executor logged that FALSE as «FAILED» while
// the window actually appeared (without foreground — a shown-but-not-
// activated console sits behind the shell = «моргание и не видит»).
//
// THE FIX under test: discard the ShowWindow return, verify with
// IsWindowVisible, BringWindowToTop + best-effort SetForegroundWindow.
//
// The probe reproduces the FULL production shape in its own CreateNoWindow
// PowerShell child (windowless console → FreeConsole+AllocConsole swap →
// SW_HIDE → SHOW), then cycles Hide→Show ×3 and pins on EVERY cycle:
//   • after SW_HIDE:  IsWindowVisible == false
//   • ShowWindow(SW_SHOW) returned FALSE          (the was-hidden semantics!)
//   • after SW_SHOW:  IsWindowVisible == TRUE     (the verification semantics)
// The probe is self-contained (its own process, no host/bus/mutex contact).
// ═══════════════════════════════════════════════════════════════════════════
public class ConsoleShowAfterHideProbeTests
{
    [Fact]
    public void Show_AfterHide_OnRealConsole_IsVisible_EvenThoughShowWindowReturnsFalse()
    {
        // The script is written to a TEMP FILE and run via -File: passing it
        // inline through -Command inside ProcessStartInfo.Arguments mangled
        // the newlines/quotes and the child exited 0 with EMPTY stdout (the
        // first probe version's second bug). -File carries the script intact.
        var ps = string.Join("\n", new[]
        {
            "$src = @'",
            "using System;",
            "using System.Runtime.InteropServices;",
            "public static class W32P {",
            "  [DllImport(\"kernel32.dll\")] public static extern IntPtr GetConsoleWindow();",
            "  [DllImport(\"kernel32.dll\", SetLastError=true)] public static extern bool AllocConsole();",
            "  [DllImport(\"kernel32.dll\", SetLastError=true)] public static extern bool FreeConsole();",
            "  [DllImport(\"user32.dll\", SetLastError=true)] public static extern bool ShowWindow(IntPtr h, int cmd);",
            "  [DllImport(\"user32.dll\")] public static extern bool IsWindowVisible(IntPtr h);",
            "  [DllImport(\"user32.dll\")] public static extern bool IsWindow(IntPtr h);",
            "  [DllImport(\"user32.dll\")] public static extern bool IsIconic(IntPtr h);",
            "  [DllImport(\"user32.dll\")] public static extern bool BringWindowToTop(IntPtr h);",
            "  [DllImport(\"user32.dll\")] public static extern bool SetForegroundWindow(IntPtr h);",
            "}",
            "'@",
            "Add-Type -TypeDefinition $src",
            "# PRE: the production spawn shape (CreateNoWindow child = windowless console)",
            "$h0 = [W32P]::GetConsoleWindow()",
            "$null = [W32P]::AllocConsole(); $err1 = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()",
            "Write-Output (\"PRE hwndNull=\" + ($h0 -eq [IntPtr]::Zero) + \" err1=\" + $err1)",
            "# THE SWAP (ConsoleWindow.AttachAndShow production arm)",
            "$freed = [W32P]::FreeConsole()",
            "$allocated = [W32P]::AllocConsole()",
            "$h = [W32P]::GetConsoleWindow()",
            "Write-Output (\"SWAP freed=\" + $freed + \" allocated=\" + $allocated + \" hwndNonZero=\" + ($h -ne [IntPtr]::Zero))",
            "# conhost may materialise the window's WS_VISIBLE bit a beat later — poll 2 s",
            "$swapVis = $false",
            "for ($w = 0; $w -lt 40; $w++) { if ($h -ne [IntPtr]::Zero -and [W32P]::IsWindowVisible($h)) { $swapVis = $true; break }; Start-Sleep -Milliseconds 50 }",
            "Write-Output (\"SWAPVIS visible=\" + $swapVis)",
            "# THE CYCLE under test (fixed executor semantics): Hide → Show ×3",
            "for ($i = 1; $i -le 3; $i++) {",
            "  $hideRet = [W32P]::ShowWindow($h, 0)          # SW_HIDE",
            "  $visibleAfterHide = [W32P]::IsWindowVisible($h)",
            "  $showRet = [W32P]::ShowWindow($h, 5)          # SW_SHOW",
            "  $showErr = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()  # evidence law: code with the result",
            "  $alive = [W32P]::IsWindow($h)",
            "  $visibleAfterShow = $false",
            "  for ($w = 0; $w -lt 20; $w++) { if ([W32P]::IsWindowVisible($h)) { $visibleAfterShow = $true; break }; Start-Sleep -Milliseconds 50 }",
            "  if ($visibleAfterShow) { $null = [W32P]::BringWindowToTop($h); $focus = [W32P]::SetForegroundWindow($h) } else { $focus = $false }",
            "  Write-Output (\"CYCLE$i hideRet=\" + $hideRet + \" hiddenOk=\" + (-not $visibleAfterHide) + \" showRet=\" + $showRet + \" showErr=\" + $showErr + \" alive=\" + $alive + \" visibleAfterShow=\" + $visibleAfterShow + \" focus=\" + $focus)",
            "}",
            "[W32P]::FreeConsole() | Out-Null",
            "exit 0",
        });
        var scriptPath = Path.Combine(Path.GetTempPath(), $"icem_showprobe_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, ps);
        try
        {
            RunProbe(scriptPath);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }

    private static void RunProbe(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true, // EXACTLY the production spawn shape
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
        };

        using var helper = Process.Start(psi)
            ?? throw new InvalidOperationException("probe: failed to spawn the console helper");
        // DRAIN BOTH pipes CONCURRENTLY (ReadToEndAsync): sequential
        // ReadToEnd(stdout) → ReadToEnd(stderr) DEADLOCKS whenever the child
        // fills the stderr pipe buffer while we block on stdout — the exact
        // hang this probe's first version produced (testhost never returned).
        // Then a BOUNDED wait with a taskkill /T tree-kill fallback (PS 5.1
        // has no Process.Kill(bool)) — a stuck helper can never hang the run.
        var stdoutTask = helper.StandardOutput.ReadToEndAsync();
        var stderrTask = helper.StandardError.ReadToEndAsync();
        if (!helper.WaitForExit(25_000))
        {
            try
            {
                using var tk = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {helper.Id} /T /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                tk?.WaitForExit(5000);
            }
            catch { /* best effort */ }
        }
        string stdout = stdoutTask.Result;
        string stderr = stderrTask.Result;
        Assert.True(helper.HasExited && helper.ExitCode == 0,
            $"probe: helper exited {helper.ExitCode}; stderr: {stderr}");
        var lines = stdout.Split('\n');

        // PRE: the windowless-console production facts (root cause #1 shape).
        var pre = lines.FirstOrDefault(l => l.StartsWith("PRE", StringComparison.Ordinal));
        Assert.True(pre is not null, $"probe: no PRE line; stdout: {stdout}");
        Assert.Contains("hwndNull=True", pre, StringComparison.Ordinal);
        Assert.Contains("err1=5", pre, StringComparison.Ordinal);

        // SWAP: a real console materialised (hwnd exists). PRODUCTION ROOT
        // CAUSE #2 (pinned here, 2026-08-28): a process spawned with
        // CREATE_NO_WINDOW carries STARTUPINFO wShowWindow=SW_HIDE, and
        // AllocConsole HONOURS it — the swapped-in console is born HIDDEN
        // (SWAPVIS visible=False even after a 2 s poll). That is why the old
        // OnAttached claim «visible now» lied too. The SWAPVIS line is
        // therefore informational; the SEMANTIC pins live in the CYCLE below:
        // Hide → Show MUST end visible (that Show is the fix).
        var swap = lines.FirstOrDefault(l => l.StartsWith("SWAP", StringComparison.Ordinal));
        Assert.True(swap is not null, $"probe: no SWAP line; stdout: {stdout}");
        Assert.Contains("allocated=True", swap, StringComparison.Ordinal);
        Assert.Contains("hwndNonZero=True", swap, StringComparison.Ordinal);
        var swapVis = lines.FirstOrDefault(l => l.StartsWith("SWAPVIS", StringComparison.Ordinal));
        Assert.True(swapVis is not null, $"probe: no SWAPVIS line; stdout: {stdout}");

        // ×3 CYCLES: the root-cause semantics + the fixed verification.
        for (int i = 1; i <= 3; i++)
        {
            var cycle = lines.FirstOrDefault(l => l.StartsWith($"CYCLE{i}", StringComparison.Ordinal));
            Assert.True(cycle is not null, $"probe: no CYCLE{i} line; stdout: {stdout}");

            // The hide took effect…
            Assert.Contains("hiddenOk=True", cycle, StringComparison.Ordinal);
            // …THE ROOT CAUSE PIN: ShowWindow(SW_SHOW) on a hidden console
            // returns FALSE («was previously hidden») — the old executor
            // logged this as «FAILED» — and…
            Assert.Contains("showRet=False", cycle, StringComparison.Ordinal);
            // The window stayed ALIVE through the cycle (a corpse hwnd would
            // now route to AttachAndShow via ProbePresence's IsWindow check).
            Assert.Contains("alive=True", cycle, StringComparison.Ordinal);
            // …THE FIX PIN: the window IS visible after the show — the only
            // honest success signal (IsWindowVisible verification).
            Assert.Contains("visibleAfterShow=True", cycle, StringComparison.Ordinal);
        }
    }
}
