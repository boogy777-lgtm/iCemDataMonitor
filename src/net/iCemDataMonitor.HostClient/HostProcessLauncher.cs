using System.Diagnostics;

namespace iCemDataMonitor.HostClient;

/// <summary>
/// B.2.1: [Запустить хост] — spawn iCemDataMonitor.Host.exe.
/// Path resolution mirrors the emule resolver doctrine: sibling of the client
/// exe FIRST (deployment layout), then a host\ subfolder, then the dev-tree
/// walk-up (server-dotnet\iCemDataMonitor.Host\bin\...\net10.0).
/// P1 production autopsy (2026-08-26): the MSI stage layout is
/// &lt;monitor&gt;\cli\ · &lt;monitor&gt;\host\ · &lt;monitor&gt;\shell\ — the host exe is NOT at
/// the monitor root and NOT next to the shell/CLI exe, so both clients need
/// the "&lt;monitor&gt;\host\" probe (shell: ..\host\, cli: ..\host\) or launch
/// aborts BEFORE the boot splash ever appears (owner complaint #1).
/// </summary>
public static class HostProcessLauncher
{
    /// <summary>Pure candidate list (unit-tested): ordered best-first.</summary>
    public static IReadOnlyList<string> ResolveCandidates(string clientBaseDir)
    {
        var list = new List<string>
        {
            // 1) deployment layout: host deployed next to the client exe
            Path.Combine(clientBaseDir, "iCemDataMonitor.Host.exe"),
            // 2) deployment layout variant: host in a dedicated subfolder
            Path.Combine(clientBaseDir, "host", "iCemDataMonitor.Host.exe"),
            // 3) flat-monitor-root layout: the client lives in cli\/shell\,
            //    the host exe sits one level UP (the monitor root).
            Path.GetFullPath(Path.Combine(clientBaseDir, "..", "iCemDataMonitor.Host.exe")),
            // 4) MSI production layout (P1 2026-08-26): monitor root carries
            //    cli\ + host\ + shell\ subfolders — the host lives in the
            //    SIBLING host\ folder. Covers BOTH the WinUI shell ([Запустить
            //    хост]) and icemdatamonitor-cli --start (same resolver).
            Path.GetFullPath(Path.Combine(clientBaseDir, "..", "host", "iCemDataMonitor.Host.exe")),
        };

        // 5) dev-layout walk-up: find <repo>\iCemDataMonitor.Host\bin
        //    (2026-09-01 repo-restructure decree: flat layout, no server-dotnet)
        var dir = new DirectoryInfo(clientBaseDir);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var binRoot = Path.Combine(dir.FullName, "iCemDataMonitor.Host", "bin");
            if (!Directory.Exists(binRoot))
                continue;
            // layout: bin\<config>\<tfm>\iCemDataMonitor.Host.exe
            // 2026-09-01 (Блок 2 п.4): the host dropped to PLAIN net10.0 with
            // the H.NotifyIcon tray; net10.0-windows kept as a legacy-layout
            // fallback for stale dev outputs.
            foreach (var config in new[] { "Debug", "Release" })
            foreach (var tfm in new[] { "net10.0", "net10.0-windows" })
            {
                var candidate = Path.Combine(binRoot, config, tfm, "iCemDataMonitor.Host.exe");
                if (!list.Contains(candidate))
                    list.Add(candidate);
            }
            break; // found the repo root — no need to walk further
        }

        return list;
    }

    /// <summary>First existing candidate or null (caller shows an honest error).</summary>
    public static string? ResolveHostExePath(string clientBaseDir) =>
        ResolveCandidates(clientBaseDir).FirstOrDefault(File.Exists);

    /// <summary>
    /// Start the headless host as a HIDDEN console process. The engine child
    /// (if emule_on=1) is spawned by the HOST's EngineSupervisor — never here.
    /// </summary>
    public static Process? Spawn(string hostExePath, Action<string>? log = null)
    {
        var p = Process.Start(new ProcessStartInfo(hostExePath)
        {
            WorkingDirectory = Path.GetDirectoryName(hostExePath)!,
            UseShellExecute = false,
            CreateNoWindow = true, // console host stays invisible behind the UI
        });
        log?.Invoke($"host spawned: {hostExePath} (pid {p?.Id})");
        return p;
    }
}
