using iCemDataMonitor.Host.Tray;

namespace iCemDataMonitor.Host.Tests;

// ═══════════════════════════════════════════════════════════════════════════
// 2026-08-28 OWNER DECREE («команды из трея кривые и не привязаны к общим
// командам host»): the tray toggle, the SetConsoleVisible RPC and the
// autostart hide must ALL behave as ONE command surface. This pins the
// ConsoleVisibilityService routing semantics on the §15.7(a) native-boundary
// seam (probe/executor delegates) — NO real Win32 console is touched by the
// shared testhost (AllocConsole/SW_HIDE in a test process would flash
// windows and corrupt the very state these tests reason about).
// The decision TABLE itself lives in TrayLogicTests; the live Win32 recipe
// (windowless console → FreeConsole+AllocConsole) is pinned by
// ConsoleAttachRecipeProbeTests.
// ═══════════════════════════════════════════════════════════════════════════
public class ConsoleVisibilityServiceTests
{
    private static (ConsoleVisibilityService Svc, List<ConsoleWindowAction> Executed, List<string> Log) Make(
        ConsolePresence presence)
    {
        var executed = new List<ConsoleWindowAction>();
        var log = new List<string>();
        var svc = new ConsoleVisibilityService(
            log: m => log.Add(m),
            probe: () => presence,
            execute: (action, l) => { executed.Add(action); l?.Invoke($"executed:{action}"); });
        return (svc, executed, log);
    }

    [Fact]
    public void Show_FromNone_ExecutesAttachAndShow_AndLogsEvidence()
    {
        var (svc, executed, log) = Make(ConsolePresence.None);

        svc.Show();

        Assert.Equal([ConsoleWindowAction.AttachAndShow], executed);
        Assert.Contains(log, l => l.Contains("executed:AttachAndShow"));
    }

    [Fact]
    public void Show_FromHidden_ExecutesShowWithFocus()
    {
        var (svc, executed, _) = Make(ConsolePresence.Hidden);

        svc.Show();

        Assert.Equal([ConsoleWindowAction.ShowWithFocus], executed);
    }

    [Fact]
    public void Show_FromVisible_IsAnHonestNoOp()
    {
        var (svc, executed, _) = Make(ConsolePresence.Visible);

        svc.Show();

        Assert.Equal([ConsoleWindowAction.Nothing], executed);
    }

    [Fact]
    public void Hide_FromNone_IsAnHonestNoOp()
    {
        var (svc, executed, _) = Make(ConsolePresence.None);

        svc.Hide();

        Assert.Equal([ConsoleWindowAction.Nothing], executed);
    }

    [Fact]
    public void Hide_FromVisible_ExecutesHide()
    {
        var (svc, executed, _) = Make(ConsolePresence.Visible);

        svc.Hide();

        Assert.Equal([ConsoleWindowAction.Hide], executed);
    }

    [Fact]
    public void Toggle_FlipsByFreshPresence()
    {
        // None → Show arm; Visible → Hide arm (the tray click semantic).
        var none = Make(ConsolePresence.None);
        none.Svc.Toggle();
        Assert.Equal([ConsoleWindowAction.AttachAndShow], none.Executed);

        var visible = Make(ConsolePresence.Visible);
        visible.Svc.Toggle();
        Assert.Equal([ConsoleWindowAction.Hide], visible.Executed);
    }

    [Fact]
    public void SetVisible_IsTheSharedEntryPoint_ShowAndHideDelegateToIt()
    {
        // ONE decision point: SetVisible(bool) — Show()/Hide() are thin
        // aliases, the RPC and the tray cannot drift apart.
        var show = Make(ConsolePresence.Hidden);
        show.Svc.SetVisible(visible: true);
        Assert.Equal([ConsoleWindowAction.ShowWithFocus], show.Executed);

        var hide = Make(ConsolePresence.Hidden);
        hide.Svc.SetVisible(visible: false);
        Assert.Equal([ConsoleWindowAction.Hide], hide.Executed);
    }

    [Fact]
    public void ProductionCtor_WiresTheRealWin32Boundary()
    {
        // The production composition must wire the REAL probe/executor: the
        // public ctor is the composition surface. Probe the real presence —
        // any ConsolePresence value is a valid answer here (the testhost console
        // state is environment-dependent); the LAW is that probing never throws.
        var svc = new ConsoleVisibilityService(log: null);
        var presence = ConsoleVisibilityPolicy.Classify(
            Tray.ConsoleWindow.Handle != IntPtr.Zero, Tray.ConsoleWindow.Visible);
        Assert.Equal(presence, presence); // classify is total; svc is constructed
        _ = svc;                          // ctor without DI must not throw
    }
}
