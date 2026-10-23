namespace iCemDataMonitor.Host.Tests;

/// <summary>
/// SNIFFER WITNESS SURFACE GUARD (2026-08-30). The ACE LAN sniffer counted
/// frames but logged nothing while running — the "sniffer-witness gap": on a
/// frozen LAN stand there was NO observable surface answering "is the wire
/// actually delivering ACE frames, for which equipment, from how many MACs?".
/// This guard pins the three faces of the witness surface from source text:
///   1. per-minute host log line  "[Ace] witness: …"   (AceSnifferService)
///   2. additive status fields AceFrames/DistinctMacs   (AceSnifferStatus)
///   3. /api/sniffer/status + /api/status/summary keys  (SnifferEndpoints +
///      HostComposition.GetSnifferStatsShape)
/// so the surface cannot silently regress. Behavior itself is proven in
/// Transport.Tests (Witness_* tests over ProcessFrame).
/// </summary>
public class SnifferWitnessSurfaceGuardTests
{
    private static string FindRepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "iCemDataMonitor.Host")) &&
                Directory.Exists(Path.Combine(dir.FullName, "iCemDataMonitor.Host.Core")))
                return dir.FullName; // = server-dotnet (convention: WwwrootHistoryContractGuardTests)
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "repo root (iCemDataMonitor.Host + iCemDataMonitor.Host.Core) not found");
    }

    private static string LoadSource(params string[] relative)
    {
        var path = Path.Combine(new[] { FindRepoDir() }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"source not found: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void SnifferService_EmitsPerMinuteWitnessLine_WithRequiredCounters()
    {
        var src = LoadSource("iCemDataMonitor.Transport", "AceSnifferService.cs");

        // The log call site must exist and carry the "[Ace]" tag + witness payload.
        Assert.Contains("LogWitnessIfNeeded", src, StringComparison.Ordinal);
        var logStart = src.IndexOf("LogInformation(\"[Ace] {Witness}\"", StringComparison.Ordinal);
        Assert.True(logStart >= 0,
            "AceSnifferService lost the witness log call — the sniffer-witness gap is back");

        // The witness line itself must carry the four operator answers:
        // ACE frame count, per-interval delta, last-frame age, distinct MACs.
        var buildStart = src.IndexOf("internal string BuildWitnessLine", StringComparison.Ordinal);
        Assert.True(buildStart >= 0, "BuildWitnessLine method disappeared");
        // Fixed-size slice: the method is short and the interpolated line
        // contains '}' chars (interpolation closers) — an IndexOf('}') slice
        // would stop inside the first interpolation.
        var body = src.Substring(buildStart, Math.Min(800, src.Length - buildStart));
        Assert.Contains("witness:", body, StringComparison.Ordinal);
        Assert.Contains("ace=", body, StringComparison.Ordinal);
        Assert.Contains("lastFrameAgo=", body, StringComparison.Ordinal);
        Assert.Contains("distinctMacs=", body, StringComparison.Ordinal);
        Assert.Contains("equipment=", body, StringComparison.Ordinal);

        // Cadence: per-minute, not per-second (1 Hz stats loop × this constant).
        Assert.Contains("WitnessIntervalSeconds = 60", src, StringComparison.Ordinal);
    }

    [Fact]
    public void SnifferStatus_ExposesWitnessCounters_AsAdditiveFields()
    {
        var statusSrc = LoadSource("iCemDataMonitor.Transport", "Models", "AceSnifferStatus.cs");
        Assert.Contains("public long AceFrames { get; set; }", statusSrc, StringComparison.Ordinal);
        Assert.Contains("public int DistinctMacs { get; set; }", statusSrc, StringComparison.Ordinal);

        var eqSrc = LoadSource("iCemDataMonitor.Transport", "Models", "AceEquipmentStatus.cs");
        Assert.Contains("public long TotalFrames { get; set; }", eqSrc, StringComparison.Ordinal);
    }

    [Fact]
    public void SnifferEndpoints_MapWitnessFields_Additively()
    {
        var src = LoadSource("iCemDataMonitor.Host.Core", "Endpoints", "SnifferEndpoints.cs");
        var mapStart = src.IndexOf("MapAceStatus", StringComparison.Ordinal);
        Assert.True(mapStart >= 0, "MapAceStatus disappeared");
        var mapSlice = src.Substring(mapStart);
        Assert.Contains("aceFrames = s.AceFrames", mapSlice, StringComparison.Ordinal);
        Assert.Contains("distinctMacs = s.DistinctMacs", mapSlice, StringComparison.Ordinal);
        Assert.Contains("totalFrames = e.TotalFrames", mapSlice, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusSummary_SurfacesSnifferStats_BesideIsRunningBool()
    {
        var src = LoadSource("iCemDataMonitor.Host.Core", "HostComposition.cs");

        // The legacy bool stays (read-surface compat)…
        Assert.Contains("layers = GetLayerStatusShape(runtime, ini, layers)", src, StringComparison.Ordinal);
        // …and the additive witness block rides the same endpoint.
        var shapeStart = src.IndexOf("internal static object GetSnifferStatsShape", StringComparison.Ordinal);
        Assert.True(shapeStart >= 0,
            "GetSnifferStatsShape disappeared — /api/status/summary lost the witness surface");
        var shapeEnd = src.IndexOf("}", src.IndexOf("distinctMacs = status.DistinctMacs", shapeStart, StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.True(shapeEnd > shapeStart, "GetSnifferStatsShape body drifted");
        var body = src.Substring(shapeStart, shapeEnd - shapeStart);
        Assert.Contains("aceFrames = status.AceFrames", body, StringComparison.Ordinal);
        Assert.Contains("totalFrames = status.TotalFrames", body, StringComparison.Ordinal);
        Assert.Contains("lastFrameAgeSec = status.LastFrameAgeSec", body, StringComparison.Ordinal);
    }
}
