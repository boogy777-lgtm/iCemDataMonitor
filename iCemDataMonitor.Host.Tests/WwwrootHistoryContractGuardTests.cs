namespace iCemDataMonitor.Host.Tests;

/// <summary>
/// WWWROOT HISTORY CONTRACT GUARD (2026-08-29). /api/regs/history
/// (Э2 web_endpoint_regs_history) returns a FLAT addr-keyed dict:
///   { "100": [{"ts":"...","v":1.5},...], "200": [...] }
/// (RegisterMirrorEndpoints.cs builds Dictionary&lt;string, object&gt; keyed by
/// addr and returns it via Results.Ok). Web charts went SILENTLY blank
/// (HTTP 200, no console error): main.js refreshChart read a NESTED shape
/// data.addr[String(addr)] that the server never produced → every dataset
/// came up empty and renderChart drew nothing, while tables (different
/// endpoint /api/regs) kept working. Coordinator decree: fix the CLIENT,
/// keep the flat server shape. This guard pins BOTH sides from source text
/// so the contract cannot re-desync.
/// </summary>
public class WwwrootHistoryContractGuardTests
{
    private static string FindRepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "iCemDataMonitor.Host")) &&
                Directory.Exists(Path.Combine(dir.FullName, "iCemDataMonitor.Host.Core")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "repo root (iCemDataMonitor.Host + iCemDataMonitor.Host.Core) not found");
    }

    private static string LoadEndpointSource()
    {
        var path = Path.Combine(FindRepoDir(),
            "iCemDataMonitor.Host.Core", "Endpoints", "RegisterMirrorEndpoints.cs");
        return File.ReadAllText(path);
    }

    private static string LoadMainJs()
    {
        var path = Path.Combine(FindRepoDir(),
            "iCemDataMonitor.Host", "wwwroot", "main.js");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The /api/regs/history handler body (from its MapGet to its
    /// Results.Ok return). Slicing keeps the negative assertions away from
    /// unrelated code elsewhere in the file (e.g. `int addr = baseReg`
    /// loops in BuildZone).
    /// </summary>
    private static string HistoryHandlerSlice()
    {
        var src = LoadEndpointSource();
        var start = src.IndexOf("MapGet(\"/api/regs/history\"", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "RegisterMirrorEndpoints.cs lost the /api/regs/history endpoint (Э2 web_endpoint_regs_history)");
        const string okTail = "Results.Ok(result)";
        var end = src.IndexOf(okTail, start, StringComparison.Ordinal);
        Assert.True(end > start,
            "/api/regs/history no longer returns the flat dict via Results.Ok(result)");
        return src.Substring(start, end - start + okTail.Length);
    }

    [Fact]
    public void HistoryEndpoint_ReturnsFlatAddrKeyedDict_NoAddrWrapper()
    {
        var handler = HistoryHandlerSlice();

        // Flat shape: Dictionary<string, object> keyed by addr, values are
        // the QueryAddr sample lists, and the DICT ITSELF is the 200 body.
        Assert.Contains("new Dictionary<string, object>()", handler, StringComparison.Ordinal);
        Assert.Contains("history.QueryAddr(addr)", handler, StringComparison.Ordinal);
        Assert.Contains("Results.Ok(result)", handler, StringComparison.Ordinal);

        // NOT nested: the dict must not be wrapped as { addr = {...} } /
        // new { addr = ... } / ["addr"] = ... — the silent-blank incident of
        // 2026-08 came from exactly that kind of drift.
        Assert.DoesNotContain("addr =", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"addr\"] =", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void MainJs_RefreshChart_ReadsFlatDict_NotDataDotAddr()
    {
        var js = LoadMainJs();

        // Flat read: data is the top-level addr-keyed dict (matches the
        // server's Dictionary<string, object> body, see the endpoint guard).
        Assert.Contains("data?.[String(addr)]", js, StringComparison.Ordinal);

        // The nested-shape reads that produced the silent empty-chart bug
        // must be gone (whole file: any data.addr read means the drift is
        // back, whatever the context).
        Assert.DoesNotContain("data.addr", js, StringComparison.Ordinal);
        Assert.DoesNotContain("data?.addr", js, StringComparison.Ordinal);
    }

    [Fact]
    public void MainJs_HistoryFailure_IsSurfacedToTheUi_NotConsoleOnly()
    {
        // Second lesson of the incident: the failure mode was INVISIBLE
        // (HTTP 200 + empty datasets, console.warn only). Pin the UI
        // surfacing in refreshChart's catch.
        var js = LoadMainJs();
        var start = js.IndexOf("async function refreshChart()", StringComparison.Ordinal);
        Assert.True(start >= 0, "main.js lost the refreshChart function");
        var end = js.IndexOf("ui.onRegToggle", start, StringComparison.Ordinal);
        Assert.True(end > start, "main.js refreshChart boundaries changed — update this guard");
        var body = js.Substring(start, end - start);

        Assert.Contains("Ошибка загрузки истории", body, StringComparison.Ordinal);
        Assert.Contains("chartEmpty", body, StringComparison.Ordinal);
    }
}
