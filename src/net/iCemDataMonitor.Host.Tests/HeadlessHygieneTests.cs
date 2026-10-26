using System.Text.RegularExpressions;

namespace iCemDataMonitor.Host.Tests;

// Headless-hygiene law, 2026-09-01 revision (owner decree «Блок 2 п.4:
// миграция на winui-трей или H.NotifyIcon»): the host tray moved from WinForms
// to H.NotifyIcon.Core (pure Win32, trim-compatible — WinForms blocked
// PublishTrimmed). The whitelist FLIPS: H.NotifyIcon is now the ONLY allowed
// tray stack, System.Windows.Forms/UseWindowsForms join the BANNED list.
// Everything else stays banned: WPF/WinUI/UWP-XAML markers, modal dialogs in
// the headless path, SCM hosting. A dedicated ratchet also asserts that the
// removed remote stop-RPC never reappears anywhere in Host sources (E.2.7 law).
public class HeadlessHygieneTests
{
    private static string FindHostProjectDir()
    {
        // Walk up from the test bin dir to the repo layout: .../<repo>/iCemDataMonitor.Host
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "iCemDataMonitor.Host", "iCemDataMonitor.Host.csproj");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "iCemDataMonitor.Host");
            dir = dir.Parent;
        }
        throw new InvalidOperationException("iCemDataMonitor.Host project directory not found");
    }

    /// <summary>Banned fragments (code scan, comments stripped).</summary>
    public static TheoryData<string> ForbiddenFragments => new()
    {
        "Microsoft.UI.Xaml",        // WinUI 3
        "Windows.UI.Xaml",          // UWP XAML
        "System.Xaml",              // WPF XAML stack
        "MessageBox",               // modal dialogs — headless path has no owner window
        "UseWindowsService",        // SCM hosting — REJECTED by owner decision (UAC)
        "WindowsAppSDK",            // Windows App SDK
        "System.Windows.Forms",     // WinForms tray — REPLACED 2026-09-01 (blocks trimming)
        "<UseWindowsForms>",        // the MSBuild switch itself stays banned
        "<UseWinUI>",               // WinUI project property
    };

    [Theory]
    [MemberData(nameof(ForbiddenFragments))]
    public void HostProject_ContainsNo_DesktopUiOrServicePlumbing(string fragment)
    {
        var offenders = ScanHostFiles(f => StripComments(f, File.ReadAllText(f)))
            .Where(t => t.Text.Contains(fragment, StringComparison.Ordinal))
            .Select(t => t.File)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Forbidden fragment '{fragment}' found in CODE (comments stripped): {string.Join(", ", offenders)}");
    }

    [Fact]
    public void HostProject_ContainsNo_SystemWindows_Namespaces()
    {
        // 2026-09-01: with the WinForms whitelist gone, EVERY System.Windows.*
        // namespace is banned in host code (WPF stacks, WinForms, etc.).
        var regex = new Regex(@"System\.Windows", RegexOptions.Compiled);
        var offenders = ScanHostFiles(f => StripComments(f, File.ReadAllText(f)))
            .Where(t => regex.IsMatch(t.Text))
            .Select(t => t.File)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"System.Windows.* found in CODE: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void HostProject_Uses_HNotifyIcon_ForTrayOnly()
    {
        // Positive sanity: the 2026-09-01 whitelist is not vacuous — the
        // H.NotifyIcon tray plumbing exists.
        var hits = ScanHostFiles(f => StripComments(f, File.ReadAllText(f)))
            .Where(t => t.Text.Contains("H.NotifyIcon", StringComparison.Ordinal))
            .Select(t => t.File)
            .ToList();

        Assert.NotEmpty(hits);
    }

    /// <summary>
    /// E.2.7 law ratchet: the remote stop-RPC was REMOVED from proto, service
    /// and clients; the host must stop ONLY from its own tray. RAW scan (no
    /// comment stripping) — even a mention in a comment fails, so the name can
    /// never creep back as dead weight or documentation drift.
    /// </summary>
    [Fact]
    public void HostProject_ContainsNo_RemovedRemoteStopRpc()
    {
        const string removedRpc = "ShutdownHost";
        var hostDir = FindHostProjectDir();
        var files = Directory.EnumerateFiles(hostDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                       && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                       && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
            })
            .ToList();

        var offenders = files
            .Where(f => File.ReadAllText(f).Contains(removedRpc, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Removed RPC '{removedRpc}' reappeared in Host sources: {string.Join(", ", offenders)}");
    }

    private static IEnumerable<(string File, string Text)> ScanHostFiles(Func<string, string> transform)
    {
        var hostDir = FindHostProjectDir();
        var files = Directory.EnumerateFiles(hostDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                       && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                       && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
            })
            .ToList();

        Assert.NotEmpty(files);
        return files.Select(f => (File: f, Text: transform(f)));
    }

    /// <summary>Strip // and /// line comments plus XML &lt;!-- --&gt; doc blocks so the
    /// guard matches real code/refs, not documentation ABOUT the law.</summary>
    private static string StripComments(string file, string text)
    {
        if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var idx = text.IndexOf("<!--", StringComparison.Ordinal);
            while (idx >= 0)
            {
                var end = text.IndexOf("-->", idx, StringComparison.Ordinal);
                if (end < 0) break;
                text = text.Remove(idx, end + 3 - idx);
                idx = text.IndexOf("<!--", StringComparison.Ordinal);
            }
            return text;
        }

        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//"))
                lines[i] = string.Empty;
        }
        return string.Join('\n', lines);
    }
}
