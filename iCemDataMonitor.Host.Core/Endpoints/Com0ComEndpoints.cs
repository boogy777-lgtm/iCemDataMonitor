using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using iCemDataMonitor.Transport;

namespace iCemDataMonitor.Host.Core.Endpoints;

/// <summary>
/// B5 REST endpoints 8-10: com0com null-modem driver monitoring.
/// Contract: response shapes MUST match wwwroot/mock/com0com-status.json.
/// </summary>
public static class Com0ComEndpoints
{
    public static void MapCom0ComEndpoints(this WebApplication app)
    {
        // 8. GET /api/com0com/status → Com0ComStatus + topologyMatch
        // CA1416: com0com is a Windows null-modem driver — on other platforms
        // the endpoint answers honestly (501) instead of risking a crash.
        app.MapGet("/api/com0com/status", (ICom0ComMonitor monitor) =>
        {
            if (!OperatingSystem.IsWindows())
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            var status = monitor.GetStatus();
            return Results.Ok(MapStatus(status));
        });

        // 9. POST /api/com0com/open-config {tool} — WHITELIST: setup, setupg, setupc
        app.MapPost("/api/com0com/open-config", async (HttpContext ctx, ICom0ComMonitor monitor) =>
        {
            if (!OperatingSystem.IsWindows())
                return Results.StatusCode(StatusCodes.Status501NotImplemented);

            var body = await ParseJsonAsync(ctx);
            var tool = GetString(body, "tool");

            if (string.IsNullOrWhiteSpace(tool))
                return Results.BadRequest(new { ok = false, error = "tool field required (setup, setupg, or setupc)" });

            var result = monitor.LaunchConfigTool(tool);
            if (!result.Ok)
                return Results.BadRequest(new { ok = false, error = result.Message });

            return Results.Ok(new { ok = true, processId = result.ProcessId, message = result.Message });
        });

        // 10. POST /api/com0com/refresh — debounce 10s (handled by monitor internally)
        app.MapPost("/api/com0com/refresh", async (ICom0ComMonitor monitor) =>
        {
            if (!OperatingSystem.IsWindows())
                return Results.StatusCode(StatusCodes.Status501NotImplemented);

            var status = await monitor.RefreshAsync();
            return Results.Ok(new { ok = true, status = MapStatus(status) });
        });
    }

    private static object MapStatus(iCemDataMonitor.Transport.Models.Com0ComStatus s) => new
    {
        driverInstalled = s.DriverInstalled,
        driverRunning = s.DriverRunning,
        driverVersion = s.DriverVersion,
        installDir = s.InstallDir,
        hub4ComInstalled = s.Hub4ComInstalled,
        hub4ComRunning = s.Hub4ComRunning,
        pairs = s.Pairs.Select(p => new
        {
            busId = p.BusId,
            busNameA = p.BusNameA,
            busNameB = p.BusNameB,
            portNameA = p.PortNameA,
            portNameB = p.PortNameB,
            attrsA = p.AttrsA,
            attrsB = p.AttrsB,
        }).ToList(),
        lastError = s.LastError,
        topologyMatch = new
        {
            matched = s.TopologyMatch.Matched,
            missing = s.TopologyMatch.Missing,
            unexpected = s.TopologyMatch.Unexpected,
            mismatchedRoles = s.TopologyMatch.MismatchedRoles,
        },
    };

    private static async Task<JsonElement> ParseJsonAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return default;
        }
    }

    private static string? GetString(JsonElement el, string prop)
    {
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            ? v.GetString()
            : null;
    }
}
