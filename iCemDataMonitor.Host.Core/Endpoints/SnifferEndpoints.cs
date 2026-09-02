using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Host.Core.Endpoints;

/// <summary>
/// B5 REST endpoints for ACE sniffer + Serial Tee (endpoints 1-7 + bonus tee-status/control).
/// Contract: response shapes MUST match wwwroot/mock/sniffer-*.json + tee-status.json.
/// Testability: depends on ISnifferService / ITeeService (facades), not concrete Transport types.
/// </summary>
public static class SnifferEndpoints
{
    public static void MapSnifferEndpoints(this WebApplication app)
    {
        // 1. GET /api/sniffer/status → {ace: AceStatus, tee: ComTeeStatus}
        app.MapGet("/api/sniffer/status", (ISnifferService ace, ITeeService? tee = null) =>
        {
            return Results.Ok(new
            {
                ace = MapAceStatus(ace.GetStatus()),
                tee = tee is not null ? MapTeeStatus(tee.GetStatus()) : new { up = false, baud = 0, physical = "", virtualPort = "", mode = "disabled" },
            });
        });

        // 2. GET /api/sniffer/service-vectors → [ServiceVectorStat + isUnknown]
        app.MapGet("/api/sniffer/service-vectors", (ISnifferService ace) =>
        {
            return Results.Ok(ace.GetServiceVectors());
        });

        // 3. GET /api/sniffer/signal-preview?vectorId=
        app.MapGet("/api/sniffer/signal-preview", (HttpContext ctx, ISnifferService ace) =>
        {
            if (!int.TryParse(ctx.Request.Query["vectorId"], out var vectorId) || vectorId <= 0)
                return Results.BadRequest(new { error = "vectorId query parameter required (positive integer)" });

            var preview = ace.GetSignalPreview(vectorId);
            return Results.Ok(preview);
        });

        // 4. POST /api/sniffer/control {target: ace|tee, action: start|stop}
        app.MapPost("/api/sniffer/control", async (HttpContext ctx, ISnifferService ace, ITeeService tee) =>
        {
            var body = await ParseJsonAsync(ctx);
            var target = GetString(body, "target")?.ToLowerInvariant();
            var action = GetString(body, "action")?.ToLowerInvariant();

            if (target is not "ace" and not "tee")
                return Results.BadRequest(new { ok = false, error = "target must be 'ace' or 'tee'" });
            if (action is not "start" and not "stop")
                return Results.BadRequest(new { ok = false, error = "action must be 'start' or 'stop'" });

            if (target == "ace")
            {
                if (action == "start") await ace.StartSnifferAsync();
                else await ace.StopSnifferAsync();
                var aceStatus = ace.GetStatus();
                return Results.Ok(new { ok = true, target = "ace", state = MapState(aceStatus.State) });
            }
            else
            {
                if (action == "start") await tee.StartManuallyAsync();
                else await tee.StopManuallyAsync();
                var teeStatus = tee.GetStatus();
                return Results.Ok(new { ok = true, target = "tee", state = MapState(teeStatus.State) });
            }
        });

        // 5. POST /api/sniffer/capture {action, durationSec?, note?}
        app.MapPost("/api/sniffer/capture", async (HttpContext ctx, ISnifferService ace) =>
        {
            var body = await ParseJsonAsync(ctx);
            var action = GetString(body, "action")?.ToLowerInvariant();

            if (action == "stop")
            {
                await ace.StopCaptureAsync();
                var capStatus = ace.GetCaptureStatus();
                var filePath = ace.GetCaptureFilePath();
                return Results.Ok(new
                {
                    ok = true,
                    captureId = filePath != null ? Path.GetFileNameWithoutExtension(filePath) : "",
                    state = "complete",
                    filePath = filePath ?? "",
                    sizeBytes = capStatus.FramesCaptured * 152L
                });
            }
            else if (action == "start")
            {
                int? durationSec = null;
                if (body.TryGetProperty("durationSec", out var ds) && ds.ValueKind == JsonValueKind.Number)
                    durationSec = ds.GetInt32();

                await ace.StartCaptureAsync(durationSec);
                return Results.Ok(new
                {
                    ok = true,
                    captureId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"),
                    state = "capturing"
                });
            }

            return Results.BadRequest(new { ok = false, error = "action must be 'start' or 'stop'" });
        });

        // 6. GET /api/sniffer/capture/status
        app.MapGet("/api/sniffer/capture/status", (ISnifferService ace) =>
        {
            var capStatus = ace.GetCaptureStatus();
            if (!capStatus.Capturing)
                return Results.Ok(new { activeCapture = (object?)null });

            return Results.Ok(new
            {
                activeCapture = new
                {
                    captureId = capStatus.TargetPath != null
                        ? Path.GetFileNameWithoutExtension(capStatus.TargetPath) : "",
                    state = capStatus.Capturing ? "capturing" : "complete",
                    elapsedSec = capStatus.ElapsedSec,
                    capturedFrames = capStatus.FramesCaptured,
                    sizeBytes = capStatus.FramesCaptured * 152L
                }
            });
        });

        // 7. GET /api/sniffer/capture/download?captureId= → file download (pcapng)
        app.MapGet("/api/sniffer/capture/download", (HttpContext ctx, ISnifferService ace) =>
        {
            var captureId = ctx.Request.Query["captureId"].ToString();
            if (string.IsNullOrWhiteSpace(captureId))
                return Results.BadRequest(new { error = "captureId query parameter required" });

            var safeName = SanitizeCaptureId(captureId);
            if (string.IsNullOrEmpty(safeName))
                return Results.BadRequest(new { error = "Invalid captureId" });

            var filePath = ace.GetCaptureFilePath();
            if (filePath is null || !File.Exists(filePath))
                return Results.NotFound(new { error = "Capture file not found or still capturing" });

            var actualName = Path.GetFileNameWithoutExtension(filePath);
            if (!string.Equals(actualName, safeName, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Capture not found" });

            return Results.File(filePath, "application/vnd.tcpdump.pcap", Path.GetFileName(filePath));
        });

        // ── Bonus: GUI calls /api/tee/status and /api/tee/control ──

        app.MapGet("/api/tee/status", (ITeeService tee) =>
        {
            return Results.Ok(MapTeeStatus(tee.GetStatus()));
        });

        app.MapPost("/api/tee/control", async (HttpContext ctx, ITeeService tee) =>
        {
            var body = await ParseJsonAsync(ctx);
            var action = GetString(body, "action")?.ToLowerInvariant();

            if (action is not "start" and not "stop")
                return Results.BadRequest(new { ok = false, error = "action must be 'start' or 'stop'" });

            if (action == "start") await tee.StartManuallyAsync();
            else await tee.StopManuallyAsync();

            var status = tee.GetStatus();
            return Results.Ok(new { ok = true, state = MapState(status.State) });
        });
    }

    // ═══ Mappers ═══

    private static object MapAceStatus(AceSnifferStatus s) => new
    {
        adapter = s.Adapter,
        macAddress = s.MacAddress,
        up = s.Up,
        state = s.State,                    // additive (TD-B): RUNNING/PAUSED/ADAPTER_MISSING/...
        unknownVectors = s.UnknownVectors,  // additive: for GUI counter
        availableAdapters = s.AvailableAdapters.Select(a => new { name = a.Name, description = a.Description }).ToList(), // additive (TD-B): manual selection aid
        framesPerSec = Math.Round(s.FramesPerSec, 2),
        lastFrameAgeSec = Math.Round(s.LastFrameAgeSec, 2),
        totalFrames = s.TotalFrames,
        aceFrames = s.AceFrames,        // additive witness (2026-08-30)
        distinctMacs = s.DistinctMacs,  // additive witness (2026-08-30)
        decodeErrors = s.DecodeErrors,
        prefixCounts = s.PrefixCounts,
        equipment = s.Equipment.Select(e => new
        {
            equipmentNumber = e.EquipmentNumber,
            macAddress = e.MacAddress,
            vectorCount = e.VectorCount,
            totalFrameRate = Math.Round(e.TotalFrameRate, 1),
            totalFrames = e.TotalFrames, // additive witness (2026-08-30)
        }).ToList(),
    };

    private static object MapTeeStatus(ComTeeStatus s) => new
    {
        physicalPort = s.PhysicalPort,
        baud = s.Baud,
        templateName = s.TemplateName,
        up = s.Up,
        state = s.State,                    // additive: RUNNING/PORT_BUSY/PAIR_MISSING/PAUSED
        bytesForwardedPerSec = Math.Round(s.BytesForwardedPerSec, 1),
        bytesParsedPerSec = Math.Round(s.BytesParsedPerSec, 1),
        forwardErrors = s.ForwardErrors,
        lastForwardAgeSec = Math.Round(s.LastForwardAgeSec, 2),
        lastParseAgeSec = Math.Round(s.LastParseAgeSec, 2),
        com0comPairOk = s.Com0comPairOk,
        physicalPortBusy = s.PhysicalPortBusy,
    };

    // ═══ Helpers ═══

    private static string MapState(string serviceState) => serviceState.ToLowerInvariant() switch
    {
        "running" => "running",
        "paused" => "stopped",
        _ => "stopped",
    };

    private static string SanitizeCaptureId(string raw)
    {
        if (raw.Length > 128) return "";
        return raw.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-') ? raw : "";
    }

    private static async Task<JsonElement> ParseJsonAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            return doc.RootElement.Clone();
        }
        catch { return default; }
    }

    private static string? GetString(JsonElement el, string prop)
    {
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) ? v.GetString() : null;
    }
}
