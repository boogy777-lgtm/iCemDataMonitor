namespace iCemDataMonitor.Host.Tests;

/// <summary>
/// D.2.5 arch guards (grep-стражи, precedent HeadlessHygieneTests):
///   (а) ровно один *.proto в солюшене;
///   (б) в Host / Host.Core / Transport НЕТ пакетов SignalR/CORS;
///   (в) в исходниках зоны Host нет MapControllers и нет новых data-MapGet
///       вне freeze-списка (/api/status, /api/status/summary, /api/regs;
///       2026-08-27 /api/engine удалён вместе с надзором за эмулятором);
///   (г) D.6.2: литерал pipe-имени хоста живёт ТОЛЬКО в Contracts
///       (HostChannelNames.PipeName) — bind/connect-сайты вне Contracts с
///       строковым литералом = fail.
/// </summary>
public class D2GuardTests
{
    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "iCemDataMonitor.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("solution directory not found");
    }

    private static List<string> SourceFiles(string projectDir) =>
        Directory.EnumerateFiles(projectDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                       && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                       && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
            })
            .ToList();

    // ── (а) один .proto ─────────────────────────────────────────────────────

    [Fact]
    public void ExactlyOne_ProtoFile_InSolution()
    {
        var slnDir = FindSolutionDir();
        var protos = Directory.EnumerateFiles(slnDir, "*.proto", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var expected = Path.Combine(slnDir, "iCemDataMonitor.Contracts", "Protos", "telemetry.proto");
        Assert.Equal(new[] { expected }, protos);
    }

    // ── (б) ¬SignalR / ¬CORS пакеты ─────────────────────────────────────────

    public static TheoryData<string> GuardedProjects => new()
    {
        "iCemDataMonitor.Host",
        "iCemDataMonitor.Host.Core",
        "iCemDataMonitor.Transport",
    };

    [Theory]
    [MemberData(nameof(GuardedProjects))]
    public void Project_HasNo_SignalRorCors_Packages(string projectName)
    {
        var csproj = Path.Combine(FindSolutionDir(), projectName, $"{projectName}.csproj");
        Assert.True(File.Exists(csproj), $"missing {csproj}");

        var text = File.ReadAllText(csproj);
        Assert.DoesNotContain("SignalR", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AspNetCore.Cors", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── (в) ¬MapControllers, MapGet только freeze-список ────────────────────

    /// <summary>Frozen legacy ops surface (D.0): the ONLY allowed data-MapGet paths.
    /// 2026-08-27: /api/engine REMOVED with engine supervision (the emulator is
    /// a separate independent app now). 2026-08-27 corrected decree: the web
    /// layer is RESTORED — /api/regs/history is back on the frozen list
    /// (Э2 web_endpoint_regs_history).</summary>
    private static readonly string[] FrozenMapGetPaths =
    {
        "/api/status",
        "/api/status/summary",
        "/api/regs",
        "/api/regs/history",
    };

    [Fact]
    public void HostZone_HasNo_MapControllers()
    {
        var offenders = HostZoneSourceFiles()
            .Where(f => File.ReadAllText(f).Contains("MapControllers", StringComparison.Ordinal))
            .ToList();
        Assert.True(offenders.Count == 0, $"MapControllers found in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void HostZone_MapGet_OnlyFrozenPaths()
    {
        var offenders = new List<string>();
        foreach (var file in HostZoneSourceFiles())
        {
            var text = File.ReadAllText(file);
            var idx = 0;
            while ((idx = text.IndexOf("MapGet(", idx, StringComparison.Ordinal)) >= 0)
            {
                // Extract the first string literal after MapGet(.
                var quote = text.IndexOf('"', idx);
                if (quote < 0) break;
                var end = text.IndexOf('"', quote + 1);
                if (end < 0) break;
                var route = text.Substring(quote + 1, end - quote - 1);

                if (!FrozenMapGetPaths.Contains(route))
                    offenders.Add($"{file}: MapGet(\"{route}\")");

                idx = end;
            }
        }

        Assert.True(offenders.Count == 0,
            "data-MapGet outside the frozen list: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// Live-surface scope per D.2.5: Program.cs / HostAppFactory / Host.Core
    /// ROOT files (the frozen ops mapping lives in HostComposition). The
    /// dormant Endpoints\*.cs files are NOT mapped in production (STATE §17
    /// GAP-6) and are out of scope here.
    /// </summary>
    private static List<string> HostZoneSourceFiles()
    {
        var sln = FindSolutionDir();
        var hostCoreRoot = Path.Combine(sln, "iCemDataMonitor.Host.Core");
        return SourceFiles(Path.Combine(sln, "iCemDataMonitor.Host"))
            .Concat(Directory.EnumerateFiles(hostCoreRoot, "*.cs", SearchOption.TopDirectoryOnly))
            .ToList();
    }

    // ── (г) D.6.2: pipe-name literal guard ──────────────────────────────────

    /// <summary>Needle built SPLIT so this guard file never matches itself.</summary>
    private static string PipeNameLiteral => "\"iCemDataMonitor" + ".Host\"";

    private static readonly string[] PipeBindConnectApis =
    {
        "ListenNamedPipe(",
        "NamedPipeClientStream(",
        "NamedPipeServerStream(",
    };

    [Fact]
    public void PipeName_Literal_DefinedExactlyOnce_InContracts()
    {
        var contractsDir = Path.Combine(FindSolutionDir(), "iCemDataMonitor.Contracts");
        var hits = SourceFiles(contractsDir)
            .Where(f => File.ReadAllText(f).Contains(PipeNameLiteral, StringComparison.Ordinal))
            .ToList();

        var expected = Path.Combine(contractsDir, "HostChannelNames.cs");
        Assert.True(hits.Count == 1 && hits[0] == expected,
            $"pipe-name literal must live ONLY in {expected}; found: {string.Join(", ", hits)}");
    }

    [Fact]
    public void PipeName_Literal_OutsideContracts_AtBindConnectSites_Fails()
    {
        var slnDir = FindSolutionDir();
        var offenders = new List<string>();

        foreach (var project in Directory.EnumerateDirectories(slnDir))
        {
            if (Path.GetFileName(project) == "iCemDataMonitor.Contracts")
                continue; // the single sanctioned source (HostChannelNames.PipeName)

            foreach (var file in SourceFiles(project))
            {
                var text = File.ReadAllText(file);
                foreach (var api in PipeBindConnectApis)
                {
                    var idx = 0;
                    while ((idx = text.IndexOf(api, idx, StringComparison.Ordinal)) >= 0)
                    {
                        // Arguments of the bind/connect call — a short window is
                        // enough and keeps the guard immune to distant literals.
                        var windowEnd = Math.Min(text.Length, idx + 240);
                        var window = text.Substring(idx, windowEnd - idx);
                        if (window.Contains(PipeNameLiteral, StringComparison.Ordinal))
                            offenders.Add($"{file}: {api}");

                        idx += api.Length;
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "pipe-name literal outside Contracts at a bind/connect site "
            + "(use iCemDataMonitor.Contracts.HostChannelNames.PipeName): "
            + string.Join("; ", offenders));
    }
}
