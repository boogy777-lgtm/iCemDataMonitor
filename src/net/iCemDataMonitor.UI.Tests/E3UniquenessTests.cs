using iCemDataMonitor.HostClient;
using Xunit;

namespace iCemDataMonitor.UI.Tests;

// ═══════════════════════════════════════════════════════════════════════════
// E.3.1 (owner decree 2026-08-24): uniqueness of the server — the UI starts
// the host ONLY when it is not running and NEVER spawns a copy of a live one;
// a second Host.exe fails fast with exit 1 (SingleInstanceGuard).
//
// Plus source guards (owner decree 2026-08-26, P1): SINGLE tray owner = HOST —
// the shell carries no tray icon of its own and its window close = process
// exit; the shell exit path must NOT contain the ShutdownHost RPC.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Spawn law over the ensure-host decision table: Running ⇒ spawn FORBIDDEN.
/// This is the unit-tested encoding used by MainWindow.RunEnsureHostFlowAsync.
/// </summary>
public class EnsureHostSpawnLawTests
{
    [Fact]
    public void MutexAndPipe_Running_NeverSpawns()
    {
        // E.3.1: mutex✓+pipe✓ = a live instance serves ⇒ NO new host process.
        var v = EnsureHost.Decide(mutexExists: true, pipeResponds: true);
        Assert.False(EnsureHost.ShouldSpawnNewHostProcess(v));
    }

    [Fact]
    public void PipeOnly_Running_NeverSpawns()
    {
        // pipe is authoritative: a serving pipe forbids spawning just as hard.
        var v = EnsureHost.Decide(mutexExists: false, pipeResponds: true);
        Assert.False(EnsureHost.ShouldSpawnNewHostProcess(v));
    }

    [Theory]
    [InlineData(false, false)] // nothing there — spawn permitted (dialog-gated)
    [InlineData(true, false)]  // wedged/starting host — dialog offers launch
    public void NotRunning_SpawnPermitted(bool mutexExists, bool pipeResponds)
    {
        var v = EnsureHost.Decide(mutexExists, pipeResponds);
        Assert.Equal(HostPresence.NotRunning, v.Presence);
        Assert.True(EnsureHost.ShouldSpawnNewHostProcess(v));
    }
}

/// <summary>
/// Mechanism contract behind «второй Host.exe → exit 1»: Program.cs of the
/// host returns 1 exactly when SingleInstanceGuard.Acquired == false, i.e.
/// when a named-mutex acquisition reports createdNew == false. The guard class
/// itself lives in the net10 Host exe project (not referenceable from net8),
/// so this test pins the BCL mutex semantics the guard relies on, using a
/// unique test-scoped name (the production name is never touched from tests —
/// a live host on the stand must not be disturbed). Full exe-level proof =
/// live gate, outside unit scope.
/// </summary>
public class SecondInstanceMutexContractTests : IDisposable
{
    private readonly string _name = @"Global\icem_ui_tests_" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        // Named mutexes vanish when the last handle closes — nothing to clean.
    }

    [Fact]
    public void SecondAcquisition_SeesExistingInstance_NotCreatedNew()
    {
        using var first = new Mutex(initiallyOwned: true, _name, out bool firstCreated);
        Assert.True(firstCreated); // instance #1 owns the guard

        using var second = new Mutex(initiallyOwned: true, _name, out bool secondCreated);
        Assert.False(secondCreated); // instance #2: Acquired=false ⇒ host exits 1
    }

    [Fact]
    public void HeldMutex_IsDetectable_ViaTryOpenExisting()
    {
        using var first = new Mutex(initiallyOwned: true, _name, out _);
        // Same probe API EnsureHost.MutexExists() uses against the production name.
        Assert.True(Mutex.TryOpenExisting(_name, out _));
    }

    [Fact]
    public void ProductionMutexName_Literal_PinnedToSingleInstanceGuard()
    {
        // Drift guard: EnsureHost.HostMutexName must stay byte-equal to
        // SingleInstanceGuard.DefaultMutexName (iCemDataMonitor.Host) —
        // a mismatch would only degrade dialog detail, but pinning costs nothing.
        Assert.Equal(@"Global\iCemDataMonitorHost_v1", EnsureHost.HostMutexName);
    }
}

/// <summary>
/// Source guard, owner decree 2026-08-26 (P1): SINGLE tray owner = HOST.
/// Production bug: TWO identical icons hung in the tray at once — the WinForms
/// NotifyIcon of iCemDataMonitor.Host.exe AND the H.NotifyIcon TaskbarIcon of
/// the shell. Fix: the shell drops its tray entirely (sources and package
/// removed); closing the shell window exits ONLY the shell process, the host
/// keeps running (E.2.7: the host stops exclusively via its own tray). Tests
/// scan the shell sources and fail if a tray icon or a hide-on-close path
/// returns.
/// </summary>
public class ShellExitPathGuardTests
{
    private static string ShellDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "iCemDataMonitor.Shell.WinUI", "App.xaml.cs")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("shell sources not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "iCemDataMonitor.Shell.WinUI");
    }

    private static string Read(string fileName)
    {
        // Sources are UTF-8; explicit encoding keeps кириллица intact.
        return System.IO.File.ReadAllText(Path.Combine(ShellDir(), fileName), System.Text.Encoding.UTF8);
    }

    [Fact]
    public void ShellSources_NoTrayIcon()
    {
        foreach (var file in new[] { "App.xaml.cs", "MainWindow.xaml.cs" })
        {
            var src = Read(file);
            Assert.DoesNotContain("TaskbarIcon", src);
            Assert.DoesNotContain("NotifyIcon", src);
            Assert.DoesNotContain("H.NotifyIcon", src);
            Assert.DoesNotContain("TrayIconHost", src);
        }
        Assert.False(File.Exists(Path.Combine(ShellDir(), "TrayIconHost.cs")),
            "TrayIconHost.cs must be deleted: single tray owner = host (P1)");
    }

    [Fact]
    public void MainWindow_CloseExitsProcess()
    {
        var src = Read("MainWindow.xaml.cs");
        Assert.DoesNotContain("closeToTray", src);
        Assert.DoesNotContain("AppWindow.Hide()", src);
    }

    [Fact]
    public void AppExitPath_NoShutdownRpc()
    {
        var src = Read("App.xaml.cs");
        Assert.DoesNotContain("ShutdownHost", src);
    }
}

/// <summary>
/// Source guard, owner decree 2026-08-27 (CORRECTED the same day): the shell
/// surface is [Состояние | Панель]. The web SPA tab («Панель», WebView2) is
/// BACK — the original removal misread the decree; the shell must embed the
/// host's dashboard in WebView2 again. The in-proc emulator page («Эмулятор»)
/// stays REMOVED — the emulator is a separate independent app with its own
/// UI. Guards: the WebView2 control + Tag="web" MUST be present in the shell
/// sources; every emulator token stays banned (cs/xaml/csproj, obj/bin
/// excluded).
/// </summary>
public class ShellSurfaceGuardTests
{
    private static string ShellDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "iCemDataMonitor.Shell.WinUI", "App.xaml.cs")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("shell sources not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "iCemDataMonitor.Shell.WinUI");
    }

    private static IEnumerable<string> ShellSourceFiles() =>
        Directory.EnumerateFiles(ShellDir(), "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                       && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                       && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
            });

    [Fact]
    public void ShellSources_NoEmulatorWinUI()
    {
        // The in-proc EmulatorPage tab is gone (2026-08-27): no emulator UI
        // library types/namespace may appear in the shell sources.
        foreach (var f in ShellSourceFiles())
        {
            var text = File.ReadAllText(f);
            Assert.False(text.Contains("Emulator.WinUI", StringComparison.Ordinal),
                $"shell source references the emulator UI library: {f}");
        }
    }

    [Fact]
    public void ShellSources_WebView2PanelRestored()
    {
        // 2026-08-27, corrected owner decree: the «Панель» tab (embedded
        // browser over the host's web SPA) is RESTORED — WebView2 must be
        // present in the shell (XAML control, lazy EnsureCoreWebView2Async,
        // and the package reference). The emulator bans above are untouched.
        var xaml = Read("MainWindow.xaml");
        Assert.Contains("WebView2", xaml);
        Assert.Contains("PanelWebView", xaml);
        Assert.Contains("Tag=\"web\"", xaml);
        Assert.Contains("Панель", xaml);

        var cs = Read("MainWindow.xaml.cs");
        Assert.Contains("EnsureCoreWebView2Async", cs);
        Assert.Contains("UiSection.Web", cs);

        var csproj = Read("iCemDataMonitor.Shell.WinUI.csproj");
        Assert.Contains("Microsoft.Web.WebView2", csproj);
    }

    [Fact]
    public void ShellSources_NoGrpcEngineControl()
    {
        // The engine control adapter is gone (2026-08-27): the shell has no
        // engine control channel over the host pipe anymore.
        foreach (var f in ShellSourceFiles())
        {
            var text = File.ReadAllText(f);
            Assert.False(text.Contains("GrpcEngineControl", StringComparison.Ordinal),
                $"shell source references the engine control adapter: {f}");
        }
    }

    [Fact]
    public void Shell_NavigationView_HasStatusAndWebSections()
    {
        // Corrected decree 2026-08-27: [Состояние | Панель] (the old W11
        // order was [Состояние | Панель | Эмулятор]; the emulator tab is
        // gone for good).
        var xaml = Read("MainWindow.xaml");
        Assert.Contains("Tag=\"status\"", xaml);
        Assert.Contains("Tag=\"web\"", xaml);
        Assert.DoesNotContain("Tag=\"emulator\"", xaml);
        Assert.DoesNotContain("EmulatorPageHost", xaml);
    }

    [Fact]
    public void Shell_Never_Spawns_iCemEmule_WinUI()
    {
        // The shell must not launch the emulator UI process — the emulator is
        // a separate independent app now.
        foreach (var f in ShellSourceFiles())
        {
            var text = File.ReadAllText(f);
            Assert.False(text.Contains("iCemEmule.WinUI.exe", StringComparison.Ordinal),
                $"shell source spawns the emulator UI process: {f}");
        }
    }

    private static string Read(string fileName) =>
        System.IO.File.ReadAllText(Path.Combine(ShellDir(), fileName), System.Text.Encoding.UTF8);
}
