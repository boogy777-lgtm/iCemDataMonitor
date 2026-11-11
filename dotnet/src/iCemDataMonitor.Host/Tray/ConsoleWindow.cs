using System.Runtime.InteropServices;

namespace iCemDataMonitor.Host.Tray;

/// <summary>Launch-mode of the host console (pure decision, unit-tested table).</summary>
public enum ConsoleLaunchMode
{
    /// <summary>No console window at all — spawned with CreateNoWindow.</summary>
    HiddenSpawn,

    /// <summary>Console exists but was launched by autostart — hide it.</summary>
    AutostartHide,

    /// <summary>Manual exe run — keep the console visible with a banner.</summary>
    ManualVisible,
}

/// <summary>
/// E.2.4 console contract (Win32 boundary of the console-visibility law):
/// - spawn from tray/UI/autostart → HIDDEN console (CreateNoWindow / --autostart);
/// - manual exe run → VISIBLE console with a banner (iCemEmule W11.9 style);
/// - [Показать/Скрыть консоль] (tray) and SetConsoleVisible RPC (WinUI status
///   tab) and the autostart hide ALL go through <see cref="ConsoleVisibilityService"/>
///   (owner decree 2026-08-28: ONE service) — this static class is the native
///   executor that service calls, nothing else.
///
/// ConsoleCloseGuard interplay: SetConsoleCtrlHandler registration is
/// PROCESS-WIDE — it covers ANY console the process is attached to, including
/// one allocated LATER via AllocConsole (the handler list belongs to the
/// process, not to a particular console). An X click on a demand-attached
/// console therefore still hides instead of killing the host.
///
/// ROOT CAUSE documented here (startup.log 07:56:58, owner machine, host
/// spawned by the WinUI shell via HostProcessLauncher.Spawn — CreateNoWindow):
/// the process owns a WINDOWLESS console — GetConsoleWindow() returns NULL,
/// yet AllocConsole fails with ERROR_ACCESS_DENIED (5) because the console
/// OBJECT exists, and AttachConsole(parent) fails (the GUI parent has none).
/// The old Show() was therefore an honest no-op («console stays hidden») —
/// the owner saw no console at all. THE FIX (AttachAndShow executor): after a
/// failed AllocConsole, detach from the windowless console (FreeConsole) and
/// allocate a FRESH one — a new console created by AllocConsole is a real
/// VISIBLE window. Live-probe pinned in ConsoleAttachRecipeProbeTests.
///
/// ROOT CAUSE #2 documented here (startup.log 10:58:10, owner machine, the
/// NEXT tap after a successful hide): ShowWindow's BOOL return is «was the
/// window visible BEFORE the call» — NOT success. On a hidden console a
/// SUCCESSFUL show returns FALSE and the old ShowWithFocus logged a lying
/// «ShowWindow FAILED — console stayed hidden» while the tray caption flipped
/// to «Скрыть консоль» (the 10:58:16 click took the HIDE arm — which the tray
/// picks ONLY when IsWindowVisible==true — and that hide's return TRUE means
/// «previously visible»). The real user pain was the dropped FOREGROUND
/// outcome: a shown-but-not-activated console sits behind the shell/browser
/// = «моргание и не видит». THE FIX (ShowWithFocus/HideWindow executors):
/// discard the ShowWindow return, verify with IsWindowVisible, BringWindowToTop
/// + best-effort SetForegroundWindow, and log «visible=true, foreground
/// taken/foreground not available» — never FAILED while the window is visible.
/// Live-probe pinned in ConsoleShowAfterHideProbeTests (Hide→Show ×3 cycles).
/// </summary>
public static class ConsoleWindow
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;

        // NOTE: GetConsoleWindow is exported by KERNEL32 (console API), while
    // ShowWindow/IsWindowVisible live in USER32 (window manager). Caught live:
    // a wrong module here throws EntryPointNotFound at startup.
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    // P2: demand-attach for hosts spawned with CreateNoWindow (no console at all).
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    // 2026-08-28 FIX: detach from the WINDOWLESS console (CreateNoWindow spawn)
    // so AllocConsole can materialise a real visible one (ERROR_ACCESS_DENIED
    // otherwise — a process can own only ONE console).
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    // P3 (2026-08-27): parent-console fallback — a host spawned from a console
    // parent (cmd) but with CreateNoWindow/DETACHED has no console of its own;
    // when AllocConsole fails yet a parent console exists, ATTACH to it and the
    // banner still reaches a visible console instead of a dead toggle.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    private const uint AttachParentProcess = 0xFFFFFFFF;

    // Fresh consoles default to the OEM codepage — the banner carries Cyrillic,
    // so switch the console codepages to UTF-8 before writing it (best effort).
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint wCodePageID);

    // 2026-08-28 FIX LAW: after (Free+Alloc)Console the .NET cached Console.Out/
    // Error/In point at the DEAD pre-attach handles — reopen them from the
    // console devices CONOUT$/CONIN$ via CreateFile (CreateFileW, UTF-16 name).
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 0x3;

    // SetLastError: the show-failure evidence line logs GetLastWin32Error —
    // a bare «FAILED» without the code was the 10:58:10 evidence-poverty pain.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // FIX #4 (2026-08-28): a DEAD hwnd (X-closed / conhost restarted after the
    // probe) must classify as presence None — otherwise a corpse handle is
    // treated as Hidden and ShowWithFocus fires on a window that no longer
    // exists. GetConsoleWindow can return a stale HWND; IsWindow is the truth.
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    // FIX LAW 2026-08-28: SW_SHOW alone can leave the console BEHIND the
    // active window (owner sees «nothing happened»); show must also FOCUS.
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // FIX #2 2026-08-28 (10:58:10 evidence): a shown-but-not-activated console
    // stays BEHIND the shell/browser. Raise the z-order even when the
    // foreground-lock refuses activation — visible is the non-negotiable part.
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    public static IntPtr Handle => GetConsoleWindow();

    public static bool Visible
    {
        get
        {
            var h = Handle;
            return h != IntPtr.Zero && IsWindow(h) && IsWindowVisible(h);
        }
    }

    /// <summary>Fresh presence probe (the service's production data source).
    /// FIX #4: the handle must ALSO pass IsWindow — a corpse hwnd (console X-ed
    /// or conhost recycled between probes) classifies as None, so the closed
    /// table routes the next Show to AttachAndShow instead of firing
    /// ShowWithFocus at a dead window.</summary>
    internal static ConsolePresence ProbePresence()
    {
        var h = Handle;
        bool alive = h != IntPtr.Zero && IsWindow(h);
        return ConsoleVisibilityPolicy.Classify(alive, alive && IsWindowVisible(h));
    }

    /// <summary>The service's production executor: one closed switch — the ONLY
    /// place console Win32 actions are performed from (except the synchronous
    /// X-path Hide wrapper used by ConsoleCloseGuard).</summary>
    internal static void ExecuteAction(ConsoleWindowAction action, Action<string>? log)
    {
        switch (action)
        {
            case ConsoleWindowAction.AttachAndShow:
                AttachAndShow(log);
                break;

            case ConsoleWindowAction.ShowWithFocus:
                ShowWithFocus(log);
                break;

            case ConsoleWindowAction.Hide:
                HideWindow(log);
                break;

            case ConsoleWindowAction.Nothing:
                break; // honest no-op (table says so — no evidence spam)

            default:
                break; // fail-safe: unknown action ⇒ never act
        }
    }

    /// <summary>Demand-attach executor (owner FIX LAW, 2026-08-28): a SHOW with
    /// no console window must end with a VISIBLE console. Chain:
    ///   1. AllocConsole — succeeds when the process truly has no console
    ///      (DETACHED_PROCESS spawn) → fresh visible console;
    ///   2. FreeConsole + AllocConsole — the PRODUCTION root-cause arm: the
    ///      CreateNoWindow spawn left a windowless console behind, so step 1
    ///      failed with ERROR_ACCESS_DENIED; detaching releases it and the new
    ///      allocation materialises a visible window;
    ///   3. AttachConsole(ATTACH_PARENT_PROCESS) — last resort: attach to the
    ///      parent's console (host spawned from cmd with a detached console);
    ///   4. honest failure evidence on the startup log — never an exception.
    /// After ANY attach: rebind stdout/stderr/stdin onto the FRESH console
    /// devices (CONOUT$/CONIN$) and print the shared banner. Win32 codes are
    /// captured IMMEDIATELY after each native call — any Win32 call in between
    /// would overwrite LastError and falsify the evidence.</summary>
    private static void AttachAndShow(Action<string>? log)
    {
        if (AllocConsole())
        {
            OnAttached(log, "[console] demand-attached a new console (headless spawn) — visible now");
            return;
        }
        int allocError = Marshal.GetLastWin32Error();

        // The windowless-console arm (production root cause — see class doc).
        bool freed = FreeConsole();
        if (!freed)
        {
            int freeError = Marshal.GetLastWin32Error();
            TryAttachParent(log, allocError, $"FreeConsole FAILED (Win32 {freeError}) before the swap");
            return;
        }

        if (AllocConsole())
        {
            OnAttached(log, "[console] windowless console replaced (FreeConsole+AllocConsole) — visible now");
            return;
        }
        int swapError = Marshal.GetLastWin32Error();
        TryAttachParent(log, allocError, $"FreeConsole+AllocConsole FAILED (Win32 {swapError})");
    }

    /// <summary>Last-resort arm of the demand-attach chain (parent console).</summary>
    private static void TryAttachParent(Action<string>? log, int allocError, string swapOutcome)
    {
        if (AttachConsole(AttachParentProcess))
        {
            OnAttached(log, $"[console] AllocConsole FAILED (Win32 {allocError}) — {swapOutcome}; attached to the PARENT console instead — visible now");
            return;
        }
        int attachError = Marshal.GetLastWin32Error();
        log?.Invoke(
            $"[console] demand-attach FAILED — AllocConsole (Win32 {allocError}), {swapOutcome}, AttachConsole(ATTACH_PARENT) (Win32 {attachError}) — no console available");
    }

    /// <summary>Post-attach common tail: UTF-8 codepages, std-handle rebind,
    /// banner (a freshly attached console must never be empty), and — FIX #3
    /// (2026-08-28, pinned by ConsoleShowAfterHideProbeTests.SWAPVIS) — an
    /// EXPLICIT visibility check: a process spawned with CreateNoWindow
    /// carries STARTUPINFO wShowWindow=SW_HIDE and AllocConsole HONOURS it,
    /// so the swapped-in console can be born HIDDEN. The old «visible now»
    /// claim was unverified. EnsureVisible shows + verifies honestly.</summary>
    private static void OnAttached(Action<string>? log, string evidenceLine)
    {
        RebindStdHandles(log);
        WriteBanner();
        EnsureVisible(log);
        log?.Invoke(evidenceLine);
    }

    /// <summary>FIX #3: after ANY attach, IsWindowVisible is THE truth. A
    /// CreateNoWindow-spawned process gets its AllocConsole window born
    /// hidden (STARTUPINFO wShowWindow=SW_HIDE is honoured by AllocConsole —
    /// live-pinned in ConsoleShowAfterHideProbeTests.SWAPVIS) — so a plain
    /// «AllocConsole succeeded ⇒ visible» assumption lies. Show + verify.</summary>
    private static void EnsureVisible(Action<string>? log)
    {
        var h = Handle;
        if (h == IntPtr.Zero)
        {
            log?.Invoke("[console] attach verification FAILED - no console window handle after attach");
            return;
        }
        if (IsWindowVisible(h))
        {
            return; // genuinely visible — nothing to do, no log noise
        }
        // Born-hidden conhost quirk (live-pinned by both console probes): on a
        // window created hidden by the CreateNoWindow STARTUPINFO a single
        // SW_SHOW does NOT stick — the SW_HIDE→SW_SHOW toggle unsticks it.
        // The window is already hidden, so the SW_HIDE is flash-free.
        _ = ShowWindow(h, SwHide);
        _ = ShowWindow(h, ShowCmdFor(IsIconic(h)));
        bool visible = IsWindowVisible(h);
        if (visible)
        {
            _ = BringWindowToTop(h);
            _ = SetForegroundWindow(h);
        }
        log?.Invoke(visible
            ? "[console] attached console was born hidden (CreateNoWindow STARTUPINFO) - hide/show toggle shown it (visible=true)"
            : "[console] attach verification FAILED - attached console stayed hidden (IsWindowVisible()==false)");
    }

    /// <summary>ShowWithFocus executor. FIX #2 (2026-08-28, production evidence
    /// 10:58:10 «ShowWindow FAILED» while the console WAS shown): ShowWindow's
    /// BOOL return means «was the window visible BEFORE the call» — NOT
    /// success (MSDN). On a HIDDEN console (the toggle's main case) a
    /// SUCCESSFUL show returns FALSE, the old code logged a lying
    /// «ShowWindow FAILED — console stayed hidden», and the dropped message
    /// also hid the SetForegroundWindow outcome — a shown-but-NOT-activated
    /// console sits BEHIND the shell/browser = the owner's «моргание и не
    /// видит». Proof from the owner's log: the 10:58:16 tray click took the
    /// HIDE arm, which the tray picks ONLY when IsWindowVisible==true.
    /// Honest sequence now:
    ///   1. fresh GetConsoleWindow() probe (NEVER cached — handle-drift law),
    ///   2. ShowWindow(SW_SHOW / SW_RESTORE when iconic) — return DISCARDED
    ///      (it reports the prior state, ¬ the outcome),
    ///   3. IsWindowVisible verification — THE truth about the outcome,
    ///   4. BringWindowToTop — raise the z-order even when activation is denied,
    ///   5. SetForegroundWindow — best effort (foreground-lock may refuse),
    ///   6. log never says FAILED while the window is visible.</summary>
    private static void ShowWithFocus(Action<string>? log)
    {
        var h = Handle; // fresh probe per action — never a cached stale HWND
        if (h == IntPtr.Zero)
        {
            log?.Invoke("[console] no console handle — nothing to show");
            return;
        }
        // FIX #4: the window can DIE between the service's probe and this
        // executor (X click, conhost recycle). A corpse hwnd gets no
        // ShowWindow — honest evidence + the NEXT Show re-probes to None and
        // takes the AttachAndShow arm (closed-table routing, no new seam).
        if (!IsWindow(h))
        {
            log?.Invoke($"[console] show SKIPPED - console window died since the probe (hwnd=0x{h.ToInt64():X}, IsWindow=false) — next Show will demand-attach a fresh console");
            return;
        }

        // W21 G.2: a MINIMIZED console must come back via SW_RESTORE — plain
        // SW_SHOW on a minimized window does not un-minimize it.
        bool iconic = IsIconic(h);
        _ = ShowWindow(h, ShowCmdFor(iconic));
        int showError = Marshal.GetLastWin32Error(); // capture BEFORE any other Win32 call
        bool visible = IsWindowVisible(h); // the ONLY honest success signal
        if (visible)
        {
            _ = BringWindowToTop(h); // raise even when activation gets denied
        }
        bool focused = visible && SetForegroundWindow(h);
        if (!visible)
        {
            // EVIDENCE LAW (2026-08-28): never a bare FAILED — the next owner
            // complaint must carry the full window state + the Win32 code.
            log?.Invoke($"[console] show FAILED - console stayed hidden (hwnd=0x{h.ToInt64():X}, IsWindow={IsWindow(h)}, IsWindowVisible=false, Win32 {showError})");
            return;
        }
        log?.Invoke($"[console] console shown ({(iconic ? "SW_RESTORE" : "SW_SHOW")}, visible=true, " +
                    (focused ? "foreground taken)" : "foreground not available - console may be behind the active window)"));
    }

    /// <summary>Hide executor. Same return-semantics truth as ShowWithFocus:
    /// SW_HIDE on an ALREADY-hidden console returns FALSE («was previously
    /// hidden») — the old code logged «ShowWindow(SW_HIDE) FAILED» for a
    /// harmless idempotent hide. Verify with IsWindowVisible instead; the
    /// success evidence line stays byte-identical (log comparability).</summary>
    private static void HideWindow(Action<string>? log)
    {
        var h = Handle;
        if (h == IntPtr.Zero)
        {
            log?.Invoke("[console] no console window — nothing to hide (host is headless)");
            return;
        }
        _ = ShowWindow(h, SwHide);
        int hideError = Marshal.GetLastWin32Error();
        bool stillVisible = IsWindowVisible(h);
        log?.Invoke(stillVisible
            ? $"[console] SW_HIDE did not take effect - console still visible (hwnd=0x{h.ToInt64():X}, IsWindow={IsWindow(h)}, Win32 {hideError})"
            : "[console] console hidden (SW_HIDE)");
    }

    /// <summary>Synchronous hide for the CTRL_CLOSE handler (ConsoleCloseGuard):
    /// the X path needs the INSTANT ShowWindow(SW_HIDE) with no policy work —
    /// the console being closed is by definition VISIBLE (Hide arm of the
    /// table). Logs OFF the handler thread at the call site.</summary>
    public static void Hide(Action<string>? log = null) => ExecuteAction(ConsoleWindowAction.Hide, log);

    /// <summary>Pure mapping (unit-tested): minimized → restore, else plain show.</summary>
    public static int ShowCmdFor(bool isIconic) => isIconic ? SwRestore : SwShow;

    /// <summary>Pure launch-mode decision (unit-tested table).</summary>
    public static ConsoleLaunchMode Decide(bool hasConsoleWindow, bool autostartRequested)
    {
        if (!hasConsoleWindow)
            return ConsoleLaunchMode.HiddenSpawn;
        return autostartRequested ? ConsoleLaunchMode.AutostartHide : ConsoleLaunchMode.ManualVisible;
    }

    /// <summary>
    /// E.2.4 startup banner — ONE text for BOTH entry points: the manual-run
    /// path (ApplyConsoleMode → ManualVisible) and the demand-attach path
    /// (AttachAndShow). Lives here so the two can never drift.
    /// </summary>
    public static void WriteBanner()
    {
        try
        {
            Console.WriteLine($"iCemDataMonitor.Host — headless station host, pid {Environment.ProcessId}");
            Console.WriteLine("interface: tray icon (Открыть панель · Показать/Скрыть консоль · Перезапуск · Закрыть)");
            Console.WriteLine("X here = hide the console only — the server keeps running (reopen via tray), W21 G.1");
            Console.WriteLine("Ctrl+C here = graceful stop (LOCAL keyboard exception to the tray-only stop law, E.2.7)");
        }
        catch
        {
            // banner is cosmetic — a dead stdout must never hurt the host
        }
    }

    /// <summary>
    /// FIX LAW (2026-08-28): after ANY console attach the .NET cached
    /// Console.Out/Error/In point at the DEAD pre-attach handles (a headless
    /// spawn touches stdout during boot — the startup.log echo), so every
    /// subsequent write would vanish into the void. Rebind all THREE from the
    /// console devices: CONOUT$ (stdout + stderr, one CreateFile each) and
    /// CONIN$ (stdin) via CreateFileW — the device names are valid regardless
    /// of whether the OS refreshed the process std-handle slots. Codepages are
    /// switched to UTF-8 for the Cyrillic banner (best effort). Best effort per
    /// handle: an unbindable stream only costs its text, but every failure
    /// lands on the evidence channel — a demand-attached console that shows
    /// nothing looks like a dead tray item otherwise.
    /// </summary>
    private static void RebindStdHandles(Action<string>? log)
    {
        try
        {
            _ = SetConsoleOutputCP(65001); // UTF-8 — Cyrillic banner legibility
            _ = SetConsoleCP(65001);
        }
        catch { /* cosmetic — covered by the rebind evidence below */ }

        RebindOne("CONOUT$", GenericWrite, FileShareWrite, FileAccess.Write,
            bind: stream => Console.SetOut(new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = true }),
            name: "stdout", log);
        RebindOne("CONOUT$", GenericWrite, FileShareWrite, FileAccess.Write,
            bind: stream => Console.SetError(new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = true }),
            name: "stderr", log);
        RebindOne("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, FileAccess.Read,
            bind: stream => Console.SetIn(new StreamReader(stream)),
            name: "stdin", log);
    }

    /// <summary>One std handle rebind: open the console device, wrap, assign —
    /// with a per-handle evidence line on failure (¬ молчание).</summary>
    private static void RebindOne(
        string device, uint access, uint share, System.IO.FileAccess fileAccess,
        Action<System.IO.Stream> bind, string name, Action<string>? log)
    {
        try
        {
            var handle = CreateFileW(device, access, share, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                log?.Invoke($"[console] {name} rebind FAILED ({device}, Win32 {Marshal.GetLastWin32Error()}) — {name} text is lost");
                return;
            }

            bind(new System.IO.FileStream(handle, fileAccess, bufferSize: 1024, isAsync: false));
        }
        catch (Exception ex)
        {
            log?.Invoke($"[console] {name} rebind FAILED: {ex.Message}");
        }
    }
}
