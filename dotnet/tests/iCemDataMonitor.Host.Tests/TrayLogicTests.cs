using System.Diagnostics;
using iCemDataMonitor.Host.Tray;

namespace iCemDataMonitor.Host.Tests;

// E.2 unit coverage: everything around the tray EXCEPT the NotifyIcon itself
// (WinForms message loop is not unit-testable — manual live scenario covers
// icon/menu/restart). Pure functions are tested as decision tables.
public class TrayLogicTests
{
    // ── E.2.3 tooltip formatter (table) ─────────────────────────────────────

    [Theory]
    [InlineData(true, 42, 8090, "iCemDataMonitor Host — bus:open HB 42s · web:8090")]
    [InlineData(false, 0, 8090, "iCemDataMonitor Host — bus:closed HB 0s · web:8090")]
    [InlineData(false, 7, 0, "iCemDataMonitor Host — bus:closed HB 7s · web:0")]
    public void Format_Table(bool busOpen, int hb, int port, string expected) =>
        Assert.Equal(expected, TrayTooltipFormatter.Format(busOpen, (short)hb, port));

    [Fact]
    public void Format_NeverExceeds_NotifyIconLimit()
    {
        // Worst case: max-width fields must clamp to the Windows 63-char limit
        // (NotifyIcon.Text throws beyond it).
        var text = TrayTooltipFormatter.Format(busOpen: true, heartbeat: short.MaxValue, webPort: 65535);
        Assert.True(text.Length <= TrayTooltipFormatter.MaxLength, $"len={text.Length}");
    }

    // ── E.2.2 restart semantics (decision table on the pure builder) ────────

    [Fact]
    public void Restart_Build_PreservesExeAndArgsVerbatim()
    {
        var psi = RestartPlan.Build(@"C:\deploy\iCemDataMonitor.Host.exe", ["--ini", @"C:\x\station.ini", "--port", "8091"]);
        Assert.Equal(@"C:\deploy\iCemDataMonitor.Host.exe", psi.FileName);
        Assert.Equal(["--ini", @"C:\x\station.ini", "--port", "8091"], psi.ArgumentList);
    }

    [Fact]
    public void Restart_Build_EmptyArgs_SpawnSameProcessBare()
    {
        var psi = RestartPlan.Build(@"C:\h\iCemDataMonitor.Host.exe", []);
        Assert.Empty(psi.ArgumentList);
        Assert.Equal(@"C:\h\iCemDataMonitor.Host.exe", psi.FileName);
    }

    [Fact]
    public void Restart_Build_HiddenDetachedSpawn()
    {
        var psi = RestartPlan.Build(@"C:\h\iCemDataMonitor.Host.exe", ["--port", "1"]);
        Assert.False(psi.UseShellExecute);          // detached child, no shell
        Assert.True(psi.CreateNoWindow);            // hidden console contract
        Assert.Equal(@"C:\h", psi.WorkingDirectory); // exe dir, not caller's cwd
    }

    [Fact]
    public void Restart_Build_SetsGraceEnvVar_ForMutexRetry()
    {
        var psi = RestartPlan.Build(@"C:\h\iCemDataMonitor.Host.exe", []);
        Assert.Equal("1", psi.Environment[RestartPlan.GraceEnvVar]);
    }

    // ── E.3.2 legacy-Tray mutual exclusion: DELETED (W20 F.2) ───────────────
    // The legacy Tray project is removed from the solution — the second
    // backend no longer exists structurally, LegacyTrayConflictGuard is gone,
    // there is nothing left to conflict with.

    // ── E.2.2 [Открыть панель] decision table ───────────────────────────────

    [Fact]
    public void PanelOpen_ShellRunning_SignalsShowEvent_NotSpawn()
    {
        var d = PanelOpenPolicy.Decide(shellMutexTaken: true, shellExePath: @"C:\shell.exe");
        Assert.Equal(PanelOpenAction.SignalShowEvent, d.Action);
        Assert.Contains(CrossProcessNames.ShellShowWindowEvent, d.LogLine);
    }

    [Fact]
    public void PanelOpen_NoShell_ExeFound_SpawnsShell()
    {
        var d = PanelOpenPolicy.Decide(shellMutexTaken: false, shellExePath: @"C:\shell\iCemDataMonitor.WinUI.exe");
        Assert.Equal(PanelOpenAction.SpawnShell, d.Action);
        Assert.Contains("iCemDataMonitor.WinUI.exe", d.LogLine);
    }

    [Fact]
    public void PanelOpen_NoShell_NoExe_HonestError()
    {
        var d = PanelOpenPolicy.Decide(shellMutexTaken: false, shellExePath: null);
        Assert.Equal(PanelOpenAction.ReportError, d.Action);
        Assert.False(string.IsNullOrWhiteSpace(d.LogLine));
    }

    // ── E.2.2 shell resolver doctrine (sibling → shell\ → ..\shell\ → dev-walk-up) ──

    [Fact]
    public void ShellResolver_Candidates_OrderedBestFirst()
    {
        var list = ShellExeResolver.ResolveCandidates(@"C:\deploy\host");
        Assert.Equal(@"C:\deploy\host\iCemDataMonitor.WinUI.exe", list[0]);
        Assert.Equal(@"C:\deploy\host\shell\iCemDataMonitor.WinUI.exe", list[1]);
        // P1 2026-08-26: MSI monitor-root layout — <monitor>\host\ + <monitor>\shell\
        Assert.Equal(
            Path.Combine(@"C:\deploy", "shell", ShellExeResolver.ShellExeName),
            Path.GetFullPath(list[2]));
        Assert.All(list, c => Assert.EndsWith(ShellExeResolver.ShellExeName, c, StringComparison.Ordinal));
    }

    [Fact]
    public void ShellResolver_MsiLayout_FindsSiblingShellFolder()
    {
        // MSI production layout (P1 2026-08-26): <monitor>\host\ base dir, the
        // shell exe lives in the SIBLING <monitor>\shell\ folder. Owner
        // complaint #3: [Открыть панель] reported «shell exe not found».
        var root = Path.Combine(Path.GetTempPath(), $"shellexe_{Guid.NewGuid():N}");
        try
        {
            var hostDir = Path.Combine(root, "monitor", "host");
            var shellDir = Path.Combine(root, "monitor", "shell");
            Directory.CreateDirectory(hostDir);
            Directory.CreateDirectory(shellDir);
            var shellExe = Path.Combine(shellDir, ShellExeResolver.ShellExeName);
            File.WriteAllText(shellExe, "stub");

            var resolved = ShellExeResolver.ResolveShellExePath(hostDir);
            Assert.NotNull(resolved);
            Assert.Equal(shellExe, resolved);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ShellResolver_DevWalkUp_FindsRepoLayout()
    {
        // AppContext.BaseDirectory of the TEST host lives under
        // <repo>\iCemDataMonitor.Host.Tests\bin\... — walk-up must
        // reach the real Shell.WinUI bin candidates.
        // 2026-09-01 repo-restructure decree: flat layout, no server-dotnet level.
        var list = ShellExeResolver.ResolveCandidates(AppContext.BaseDirectory);
        // P1 2026-08-26: THREE deployment probes now precede the dev walk-up
        // (sibling → shell\ → ..\shell\) — skip all of them.
        var devCandidates = list.Skip(3).ToList();
        Assert.NotEmpty(devCandidates);
        Assert.All(devCandidates, c =>
            Assert.Contains(Path.Combine("iCemDataMonitor.Shell.WinUI", "bin"), c));
    }

    [Fact]
    public void ShellResolver_Resolve_MissingEverywhere_ReturnsNullNotThrow()
    {
        var empty = Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}");
        Assert.Null(ShellExeResolver.ResolveShellExePath(empty));
    }

    // ── E.2.4 console launch-mode decision table ────────────────────────────

    [Theory]
    [InlineData(false, false, ConsoleLaunchMode.HiddenSpawn)]   // spawned CreateNoWindow
    [InlineData(true, true, ConsoleLaunchMode.AutostartHide)]   // Run-key autostart
    [InlineData(true, false, ConsoleLaunchMode.ManualVisible)]  // manual exe run
    public void ConsoleMode_Decide_Table(bool hasConsole, bool autostart, ConsoleLaunchMode expected) =>
        Assert.Equal(expected, ConsoleWindow.Decide(hasConsole, autostart));

    // ── 2026-08-28 console-visibility decision table (owner decree) ─────────
    // ONE service for the tray toggle + SetConsoleVisible RPC + autostart
    // hide: presence (None/Hidden/Visible) × command (Show/Hide) → action.
    // Replaces the old two-arm TogglePlan — the Hidden arm now distinguishes
    // SW_SHOW+focus from demand-attach, and Show-on-Visible is an explicit
    // honest no-op.

    [Theory]
    [InlineData(ConsolePresence.None, ConsoleCommand.Show, ConsoleWindowAction.AttachAndShow)]  // headless spawn + «показать»
    [InlineData(ConsolePresence.Hidden, ConsoleCommand.Show, ConsoleWindowAction.ShowWithFocus)] // autostart/X-hidden → show+focus
    [InlineData(ConsolePresence.Visible, ConsoleCommand.Show, ConsoleWindowAction.Nothing)]     // already visible ⇒ honest no-op
    [InlineData(ConsolePresence.None, ConsoleCommand.Hide, ConsoleWindowAction.Nothing)]        // nothing exists ⇒ nothing to hide
    [InlineData(ConsolePresence.Hidden, ConsoleCommand.Hide, ConsoleWindowAction.Hide)]         // idempotent SW_HIDE
    [InlineData(ConsolePresence.Visible, ConsoleCommand.Hide, ConsoleWindowAction.Hide)]         // instant SW_HIDE
    public void ConsoleVisibility_Decide_Table(ConsolePresence presence, ConsoleCommand command, ConsoleWindowAction expected) =>
        Assert.Equal(expected, ConsoleVisibilityPolicy.Decide(presence, command));

    [Theory]
    [InlineData(false, false, ConsolePresence.None)]
    [InlineData(false, true, ConsolePresence.None)]   // invisible without a handle is impossible — classify honestly as None
    [InlineData(true, false, ConsolePresence.Hidden)]
    [InlineData(true, true, ConsolePresence.Visible)]
    public void ConsoleVisibility_Classify_Table(bool hasWindow, bool windowVisible, ConsolePresence expected) =>
        Assert.Equal(expected, ConsoleVisibilityPolicy.Classify(hasWindow, windowVisible));

    // Fail-safe arm: an out-of-table combination must never spawn/hide anything.
    [Fact]
    public void ConsoleVisibility_Decide_Unknown_FailsSafeToNothing()
    {
        Assert.Equal(ConsoleWindowAction.Nothing,
            ConsoleVisibilityPolicy.Decide((ConsolePresence)99, ConsoleCommand.Show));
    }

    // ── E.2.2 menu captions (additive-menu contract anchors) ────────────────

    [Fact]
    public void Menu_Captures_AllCommands()
    {
        // 2026-08-27 RESTORED menu (corrected decree «Web - удали (контрол
        // sim)» = the sim control died, the page survived): «Открыть Web» is
        // BACK between [Открыть панель] and the console toggle. «Статус»
        // stays REMOVED (decree).
        Assert.Equal("Открыть панель", HostTray.OpenPanelItemText);
        Assert.Equal("Открыть Web", HostTray.OpenWebItemText);
        Assert.Equal("Показать консоль", HostTray.ShowConsoleItemText);
        Assert.Equal("Скрыть консоль", HostTray.HideConsoleItemText);
        Assert.Equal("Перезапуск", HostTray.RestartItemText);
        Assert.Equal("Закрыть", HostTray.CloseItemText);
    }

    // ── P3 2026-08-27: console caption = POST-ACTION state, not intent ──────
    // The old blind flip relabelled the item to «Скрыть консоль» even when the
    // demand-attach failed — the caption must mirror what is ACTUALLY on screen.

    [Theory]
    [InlineData(false, HostTray.ShowConsoleItemText)] // no console visible → «Показать»
    [InlineData(true, HostTray.HideConsoleItemText)]  // console visible    → «Скрыть»
    public void ConsoleCaption_Table(bool visible, string expected) =>
        Assert.Equal(expected, HostTray.ConsoleCaption(visible));

    // ── W21 G.1 console close guard (decision table) ────────────────────────

    [Theory]
    [InlineData(ConsoleClosePolicy.CtrlCloseEvent, ConsoleCtrlAction.SwallowAndHideConsole)] // X button
    [InlineData(ConsoleClosePolicy.CtrlCEvent, ConsoleCtrlAction.DefaultHandling)]           // graceful stop stays
    [InlineData(ConsoleClosePolicy.CtrlBreakEvent, ConsoleCtrlAction.DefaultHandling)]
    [InlineData(ConsoleClosePolicy.CtrlLogoffEvent, ConsoleCtrlAction.DefaultHandling)]
    [InlineData(ConsoleClosePolicy.CtrlShutdownEvent, ConsoleCtrlAction.DefaultHandling)]
    [InlineData(99, ConsoleCtrlAction.DefaultHandling)] // unknown → fail-safe default
    public void ConsoleClose_Decide_Table(int ctrlType, ConsoleCtrlAction expected) =>
        Assert.Equal(expected, ConsoleClosePolicy.Decide(ctrlType));

    // ── 2026-08-27 dispatch-order fix (owner decree) ────────────────────────
    // ConsoleLifetime registers its OWN handler while the host starts, AFTER
    // our Install(). Windows dispatches in REVERSE registration order, so the
    // re-registration must be a SEPARATE, callable-again operation that does
    // NOT hit the Install() early-return gate. These facts pin the API shape
    // the Program.cs fix depends on; the LIVE dispatch order is verified by
    // ConsoleCloseProbeTests (real host exe + real WM_CLOSE on its console).

    [Fact]
    public void ConsoleCloseGuard_Install_RegistersAndMarksInstalled()
    {
        ConsoleCloseGuard.Install();
        Assert.True(ConsoleCloseGuard.Installed);
    }

    [Fact]
    public void ConsoleCloseGuard_Reinstall_AfterInstall_Succeeds_AndStaysInstalled()
    {
        ConsoleCloseGuard.Install();
        ConsoleCloseGuard.Reinstall(); // the dispatch-head re-add — must NOT throw
        Assert.True(ConsoleCloseGuard.Installed);
    }

    // ── W21 G.2 minimized-console restore mapping ───────────────────────────

    [Fact]
    public void ConsoleShow_Minimized_RestoresNotPlainShow()
    {
        Assert.Equal(9, ConsoleWindow.ShowCmdFor(isIconic: true));  // SW_RESTORE
        Assert.Equal(5, ConsoleWindow.ShowCmdFor(isIconic: false)); // SW_SHOW
    }

    [Fact]
    public void Tooltip_Clamp_TruncatesAtLimit()
    {
        var longText = new string('x', TrayTooltipFormatter.MaxLength + 10);
        Assert.Equal(TrayTooltipFormatter.MaxLength, TrayTooltipFormatter.Clamp(longText).Length);
    }

    // ── P2 2026-08-26: [Закрыть] = close the UI + host (owner decree) ────────
    // 2026-08-27: the emulator UI event was REMOVED — the emulator is a
    // separate INDEPENDENT app and [Закрыть] must NOT close it.

    [Fact]
    public void TrayCloseContract_SignalsPanelOnly_NotEmulator()
    {
        var names = TrayCloseContract.UiShutdownEventNames;
        Assert.Contains(CrossProcessNames.ShellShutdownEvent, names); // panel
        Assert.DoesNotContain(names, n => n.Contains("Emule", StringComparison.Ordinal));
        Assert.Single(names.Distinct(StringComparer.Ordinal)); // one waiter, one event
        Assert.All(names, n => Assert.StartsWith(@"Local\", n));
    }

    [Fact]
    public void TrayCloseContract_EventsAreDistinctFromShowEvent()
    {
        // Shutdown must never collide with the second-instance show signal —
        // a show would otherwise kill the panel.
        Assert.NotEqual(CrossProcessNames.ShellShowWindowEvent, CrossProcessNames.ShellShutdownEvent);
    }

    // ── 2026-08-31 (owner decree): shell «Перезапуск хоста» button → named
    // event owned by the HOST (inverted ownership vs the shell events) ──────

    [Fact]
    public void HostRestartEvent_IsDistinct_FromAllShellSignals()
    {
        // A name collision would restart the host on an unrelated signal
        // (show window / shutdown) — the restart poke is a DEDICATED event.
        Assert.StartsWith(@"Local\", CrossProcessNames.HostRestartEvent);
        Assert.NotEqual(CrossProcessNames.ShellShowWindowEvent, CrossProcessNames.HostRestartEvent);
        Assert.NotEqual(CrossProcessNames.ShellShutdownEvent, CrossProcessNames.HostRestartEvent);
        Assert.NotEqual(CrossProcessNames.ShellMutex, CrossProcessNames.HostRestartEvent);
    }

    [Fact]
    public void HostRestartEvent_IsNotAUiShutdownSignal()
    {
        // [Закрыть] semantics are unchanged: the restart poke must NEVER be
        // signalled by the close path (that would resurrect the draining host).
        Assert.DoesNotContain(CrossProcessNames.HostRestartEvent, TrayCloseContract.UiShutdownEventNames);
    }

    // ── 2026-08-31 (слой CLI «n+1 источник команд»): CLI `stop` → host-owned
    // shutdown event (same inverted ownership as the restart poke) ───────────

    [Fact]
    public void HostShutdownEvent_IsDistinct_FromAllOtherSignals()
    {
        // A collision would CLOSE the host on an unrelated signal — the
        // shutdown poke is a DEDICATED event.
        Assert.StartsWith(@"Local\", CrossProcessNames.HostShutdownEvent);
        Assert.NotEqual(CrossProcessNames.ShellShowWindowEvent, CrossProcessNames.HostShutdownEvent);
        Assert.NotEqual(CrossProcessNames.ShellShutdownEvent, CrossProcessNames.HostShutdownEvent);
        Assert.NotEqual(CrossProcessNames.ShellMutex, CrossProcessNames.HostShutdownEvent);
        Assert.NotEqual(CrossProcessNames.HostRestartEvent, CrossProcessNames.HostShutdownEvent);
    }

    [Fact]
    public void HostShutdownEvent_IsNotAUiShutdownSignal()
    {
        // UiShutdownEventNames is the REVERSE direction (host → UI): the
        // client→host shutdown poke must never be signalled back by the
        // close path itself (an infinite close loop / self-poke).
        Assert.DoesNotContain(CrossProcessNames.HostShutdownEvent, TrayCloseContract.UiShutdownEventNames);
    }

    // ── P3 2026-08-27: terminal-command re-entrancy gate (pure table) ───────
    // Live-log evidence: THREE host boots within 2 s (startup.log 08:29:46-48)
    // from rapid «Перезапуск» clicks. The gate makes every terminal click
    // after the first a LOGGED no-op — one successor spawn, one drain.

    [Fact]
    public void ActionGate_FreshGate_IsIdle()
    {
        var gate = new TrayActionGate();
        Assert.Equal(TrayGateState.Idle, gate.State);
        Assert.Equal("idle", gate.StateDescription);
    }

    [Fact]
    public void ActionGate_Idle_AcceptsRestart()
    {
        var gate = new TrayActionGate();
        Assert.True(gate.TryBeginRestart());
        Assert.Equal(TrayGateState.RestartDraining, gate.State);
        Assert.Equal("restart-draining", gate.StateDescription);
    }

    [Fact]
    public void ActionGate_Idle_AcceptsClose()
    {
        var gate = new TrayActionGate();
        Assert.True(gate.TryBeginClose());
        Assert.Equal(TrayGateState.CloseDraining, gate.State);
        Assert.Equal("close-draining", gate.StateDescription);
    }

    [Fact]
    public void ActionGate_SecondRestart_Ignored()
    {
        var gate = new TrayActionGate();
        Assert.True(gate.TryBeginRestart());
        Assert.False(gate.TryBeginRestart()); // the second click during the drain
        Assert.Equal(TrayGateState.RestartDraining, gate.State); // state unchanged
    }

    [Fact]
    public void ActionGate_SecondClose_Ignored()
    {
        var gate = new TrayActionGate();
        Assert.True(gate.TryBeginClose());
        Assert.False(gate.TryBeginClose());
        Assert.Equal(TrayGateState.CloseDraining, gate.State);
    }

    [Fact]
    public void ActionGate_RestartDuringCloseDrain_Ignored()
    {
        // Mutually exclusive: «Перезапуск» during a close drain must not
        // spawn a successor into a process that is already tearing down.
        var gate = new TrayActionGate();
        Assert.True(gate.TryBeginClose());
        Assert.False(gate.TryBeginRestart());
        Assert.Equal(TrayGateState.CloseDraining, gate.State);
    }

    [Fact]
    public void ActionGate_CloseDuringRestartDrain_Ignored()
    {
        var gate = new TrayActionGate();
        Assert.True(gate.TryBeginRestart());
        Assert.False(gate.TryBeginClose());
        Assert.Equal(TrayGateState.RestartDraining, gate.State);
    }

    [Fact]
    public void ActionGate_Reset_ReturnsToIdle_AndAcceptsAgain()
    {
        // Production call site: successor SPAWN FAILED → Reset() → the
        // operator can retry instead of being lied to («уже выполняется»).
        var gate = new TrayActionGate();
        Assert.True(gate.TryBeginRestart());
        gate.Reset();
        Assert.Equal(TrayGateState.Idle, gate.State);
        Assert.True(gate.TryBeginRestart());
    }

    [Fact]
    public void ActionGate_ConcurrentClicks_ExactlyOneAccepted()
    {
        // The live incident was RAPID clicks; even a storm of simultaneous
        // clicks must yield exactly ONE accepted terminal command.
        var gate = new TrayActionGate();
        var accepted = 0;
        Parallel.For(0, 100, _ =>
        {
            if (gate.TryBeginRestart())
                Interlocked.Increment(ref accepted);
        });
        Assert.Equal(1, accepted);
        Assert.Equal(TrayGateState.RestartDraining, gate.State);
    }

    [Fact]
    public void ActionGate_BusyMessages_ExactOperatorText()
    {
        Assert.Equal(
            "перезапуск уже выполняется — повторный клик проигнорирован",
            TrayActionGate.RestartBusyMessage);
        Assert.Equal(
            "закрытие уже выполняется — повторный клик проигнорирован",
            TrayActionGate.CloseBusyMessage);
    }
}
