namespace iCemDataMonitor.Host.Tray;

/// <summary>What [Открыть панель] must do (pure decision, unit-tested table).</summary>
public enum PanelOpenAction
{
    /// <summary>Shell already runs — poke its second-instance show event.</summary>
    SignalShowEvent,

    /// <summary>No shell running — spawn iCemDataMonitor.WinUI.exe.</summary>
    SpawnShell,

    /// <summary>Shell exe not found anywhere — honest error to stderr.</summary>
    ReportError,
}

/// <param name="Action">Resolved action.</param>
/// <param name="LogLine">One-line explanation for startup.log / console.</param>
public sealed record PanelOpenDecision(PanelOpenAction Action, string LogLine);

/// <summary>
/// E.2.2 [Открыть панель]: the host has NO UI of its own (E.2.0) — the panel
/// IS the existing WinUI shell. If the shell already runs (its single-instance
/// mutex is held), a spawned copy would just exit silently — instead we signal
/// the shell's named show-event so the running instance restores its window.
/// </summary>
public static class PanelOpenPolicy
{
    public static PanelOpenDecision Decide(bool shellMutexTaken, string? shellExePath)
    {
        if (shellMutexTaken)
        {
            return new PanelOpenDecision(
                PanelOpenAction.SignalShowEvent,
                $"shell already running ({CrossProcessNames.ShellMutex} held) — " +
                $"signalling {CrossProcessNames.ShellShowWindowEvent}");
        }
        if (shellExePath is null)
        {
            return new PanelOpenDecision(
                PanelOpenAction.ReportError,
                "shell exe not found (probed: sibling → shell\\ → ..\\shell\\ → dev-walk-up) — panel NOT opened");
        }
        return new PanelOpenDecision(PanelOpenAction.SpawnShell, $"spawning shell: {shellExePath}");
    }
}

/// <summary>
/// Resolver doctrine mirrored from HostClient.HostProcessLauncher (B.2.1):
/// sibling of the host exe FIRST (deployment layout), then a dedicated
/// subfolder, then the MSI monitor-root shell\ folder, then the dev-tree
/// walk-up. NOT reused via a project reference: HostClient drags gRPC client
/// + ReactiveUI + Emulator.Core into the host process for one path function —
/// layering pollution outweighs reuse here; the doctrine (candidate order +
/// honest null) is copied instead.
/// P1 production autopsy (2026-08-26): the MSI stage layout is
/// &lt;monitor&gt;\cli\ · &lt;monitor&gt;\host\ · &lt;monitor&gt;\shell\ — the shell exe is NOT next
/// to the host exe and NOT in a host\shell\ subfolder, so [Открыть панель]
/// reported «shell exe not found» on every production click (owner complaint
/// #3). The "&lt;monitor&gt;\shell\" probe restores the tray→panel path.
/// </summary>
public static class ShellExeResolver
{
    public const string ShellExeName = "iCemDataMonitor.WinUI.exe";

    /// <summary>Pure candidate list (unit-tested): ordered best-first.</summary>
    public static IReadOnlyList<string> ResolveCandidates(string hostBaseDir)
    {
        var list = new List<string>
        {
            // 1) deployment layout: shell deployed next to the host exe
            Path.Combine(hostBaseDir, ShellExeName),
            // 2) deployment layout variant: shell in a dedicated subfolder
            Path.Combine(hostBaseDir, "shell", ShellExeName),
            // 3) MSI production layout (P1 2026-08-26): monitor root carries
            //    cli\ + host\ + shell\ subfolders — the shell lives in the
            //    SIBLING shell\ folder.
            Path.GetFullPath(Path.Combine(hostBaseDir, "..", "shell", ShellExeName)),
        };

        // 3) dev-layout walk-up: find <repo>\iCemDataMonitor.Shell.WinUI\bin
        //    (2026-09-01 repo-restructure decree: flat layout — projects live
        //    in the repo ROOT, the server-dotnet level no longer exists).
        var dir = new DirectoryInfo(hostBaseDir);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var binRoot = Path.Combine(dir.FullName, "iCemDataMonitor.Shell.WinUI", "bin");
            if (!Directory.Exists(binRoot))
                continue;
            // layout: bin\[x64\]<config>\net10.0-windows10.0.19041.0\win-x64\iCemDataMonitor.WinUI.exe
            foreach (var platform in new[] { "x64", "" })
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(
                    binRoot, platform, config, "net10.0-windows10.0.19041.0", "win-x64", ShellExeName);
                if (!list.Contains(candidate))
                    list.Add(candidate);
            }
            break; // found the repo root — no need to walk further
        }

        return list;
    }

    /// <summary>First existing candidate or null (caller reports honestly).</summary>
    public static string? ResolveShellExePath(string hostBaseDir) =>
        ResolveCandidates(hostBaseDir).FirstOrDefault(File.Exists);
}
