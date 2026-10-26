namespace iCemDataMonitor.Host.Tests;

/// <summary>
/// WWWROOT VERSION GUARD (2026-08-28, lesson from the web outage the
/// coordinator fixed): main.js carries a self-check — <c>APP_VERSION</c> must
/// equal the <c>data-app-version</c> attribute of index.html; on mismatch the
/// script throws «stale index.html (dom=N, js=M)» and the WHOLE dashboard dies
/// (empty tables). The live incident: index.html said 31 (data-app-version,
/// main.css?v, main.js?v) while main.js still declared APP_VERSION="30" →
/// every client got a blank dashboard.
///
/// This guard scans the REAL wwwroot sources and requires ALL FOUR version
/// markers to be ONE number:
///   1. index.html <c>data-app-version="N"</c>
///   2. index.html <c>main.css?v=N</c>
///   3. index.html <c>main.js?v=N</c>
///   4. main.js <c>var APP_VERSION = "N"</c>
/// A single-file bump (exactly the incident shape) fails the build/tests with
/// a message naming the drifted markers. favicon.ico?v=2 is a separate cache
/// key and is intentionally NOT part of the app-version law.
/// </summary>
public class WwwrootVersionGuardTests
{
    private static string FindHostWwwrootDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "iCemDataMonitor.Host", "wwwroot");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("iCemDataMonitor.Host\\wwwroot not found");
    }

    private static (string Html, string Js) LoadSources()
    {
        var wwwroot = FindHostWwwrootDir();
        var html = File.ReadAllText(Path.Combine(wwwroot, "index.html"));
        var js = File.ReadAllText(Path.Combine(wwwroot, "main.js"));
        return (html, js);
    }

    private static string? Extract(System.Text.RegularExpressions.Match m) =>
        m.Success ? m.Groups[1].Value : null;

    [Fact]
    public void AllFourVersionMarkers_AreOneNumber()
    {
        var (html, js) = LoadSources();

        var domVersion = Extract(System.Text.RegularExpressions.Regex.Match(
            html, @"data-app-version=""(\d+)"""));
        var cssVersion = Extract(System.Text.RegularExpressions.Regex.Match(
            html, @"main\.css\?v=(\d+)"));
        var jsRefVersion = Extract(System.Text.RegularExpressions.Regex.Match(
            html, @"main\.js\?v=(\d+)"));
        var jsAppVersion = Extract(System.Text.RegularExpressions.Regex.Match(
            js, @"var APP_VERSION\s*=\s*""(\d+)"""));

        Assert.True(domVersion is not null, "index.html lost data-app-version (the stale-check anchor)");
        Assert.True(cssVersion is not null, "index.html lost main.css?v= (cache-buster)");
        Assert.True(jsRefVersion is not null, "index.html lost main.js?v= (cache-buster)");
        Assert.True(jsAppVersion is not null, "main.js lost var APP_VERSION (the stale-check anchor)");

        Assert.True(domVersion == cssVersion && domVersion == jsRefVersion && domVersion == jsAppVersion,
            "wwwroot version drift — the stale index.html guard kills the WHOLE dashboard JS " +
            "(live incident: index.html=31, main.js APP_VERSION=30 → empty tables). " +
            $"Found: dom={domVersion}, main.css?v={cssVersion}, main.js?v={jsRefVersion}, " +
            $"main.js APP_VERSION={jsAppVersion} — bump ALL FOUR together.");
    }

    [Fact]
    public void StaleCheckIsStillActive_InMainJs()
    {
        // The guard is only meaningful while main.js actually ENFORCES the
        // version match: pin the throw + forced-reload mechanism exists.
        var (_, js) = LoadSources();
        Assert.Contains("data-app-version", js, StringComparison.Ordinal);
        Assert.Contains("stale index.html", js, StringComparison.Ordinal);
    }
}
