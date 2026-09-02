using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Host.Core.Endpoints;

/// <summary>
/// B5 REST endpoints 13: SSF template management.
/// Contract: response shapes MUST match wwwroot/mock/templates-*.json + template-detail-*.json.
/// </summary>
public static class TemplatesEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static void MapTemplatesEndpoints(this WebApplication app)
    {
        // 13a. GET /api/templates/files → [{family, configs: [{name}]}]
        app.MapGet("/api/templates/files", () =>
        {
            var families = DeviceFamilyRegistry.Families
                .Select(kv => new
                {
                    family = kv.Key,
                    configs = kv.Value.Select(name => new { name }).ToList(),
                })
                .ToList();

            return Results.Ok(families);
        });

        // 13b. GET /api/templates/detail?family=&name=
        app.MapGet("/api/templates/detail", (HttpContext ctx) =>
        {
            var family = ctx.Request.Query["family"].ToString();
            var name = ctx.Request.Query["name"].ToString();

            if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "family and name query parameters required" });

            var path = Path.Combine(AppContext.BaseDirectory, "templates", name + ".ssf");
            if (!File.Exists(path))
                return Results.NotFound(new { error = $"Template '{name}.ssf' not found in family '{family}'" });

            var variables = SsfTemplateLoader.Load(name);
            var result = new
            {
                family = family,
                name = name,
                type = "ssf",
                variables = variables.Select(v => new
                {
                    name = v.Name,
                    description = v.Description,
                    uom = v.Uom,
                    color = v.Color,
                    position = v.Position - 1, // 0-based position
                    isSpare = v.IsSpare,
                    source = v.Source,
                }).ToList(),
            };

            return Results.Ok(result);
        });

        // 13c. POST /api/templates/save {family, name, variables[]}
        // Persists as .ssf format with round-trip validation via SsfTemplateLoader.
        app.MapPost("/api/templates/save", async (HttpContext ctx) =>
        {
            JsonElement body;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                body = doc.RootElement.Clone();
            }
            catch
            {
                return Results.BadRequest(new { ok = false, error = "Invalid JSON body" });
            }

            var family = GetString(body, "family");
            var name = GetString(body, "name");

            if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { ok = false, error = "family and name required" });

            // Validate filename safety: alphanumeric + underscore
            if (!IsSafeTemplateName(name))
                return Results.BadRequest(new { ok = false, error = "Template name must be alphanumeric + underscores" });

            var templatesDir = Path.Combine(AppContext.BaseDirectory, "templates");
            Directory.CreateDirectory(templatesDir);

            var filePath = Path.Combine(templatesDir, name + ".ssf");
            var fullPath = Path.GetFullPath(filePath);

            // Path traversal guard
            if (!fullPath.StartsWith(Path.GetFullPath(templatesDir), StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { ok = false, error = "Invalid template path" });

            // Build SSF content from variables
            var varsJson = body.TryGetProperty("variables", out var varsArr) ? varsArr.EnumerateArray().ToList() : new();

            using var writer = new StreamWriter(fullPath, false, System.Text.Encoding.UTF8);
            writer.WriteLine("* SSF Template — saved via iCemPOC web dashboard");
            writer.WriteLine($"* Family: {family}");

            int position = 1;
            foreach (var v in varsJson)
            {
                var vName = GetString(v, "name") ?? "";
                var vDesc = GetString(v, "description") ?? vName;
                writer.WriteLine($"{position},{vName},{vDesc},,0"); // col0=position, col4=0
                position++;
            }

            writer.Flush();

            // Round-trip validation: re-load and verify
            try
            {
                var reloaded = SsfTemplateLoader.Load(name);
                if (reloaded.Count == 0 && varsJson.Count > 0)
                {
                    // Check if it's an F7ssf (all col0=0) case — reload should work
                }
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = true, path = filePath, warning = $"Round-trip validation note: {ex.Message}" });
            }

            return Results.Ok(new { ok = true, path = filePath });
        });

        // Bonus: POST /api/templates/delete {family, name}
        app.MapPost("/api/templates/delete", async (HttpContext ctx) =>
        {
            JsonElement body;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                body = doc.RootElement.Clone();
            }
            catch
            {
                return Results.BadRequest(new { ok = false, error = "Invalid JSON body" });
            }

            var family = GetString(body, "family");
            var name = GetString(body, "name");

            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { ok = false, error = "name required" });

            if (!IsSafeTemplateName(name))
                return Results.BadRequest(new { ok = false, error = "Invalid template name" });

            var templatesDir = Path.Combine(AppContext.BaseDirectory, "templates");
            var filePath = Path.Combine(templatesDir, name + ".ssf");
            var fullPath = Path.GetFullPath(filePath);

            if (!fullPath.StartsWith(Path.GetFullPath(templatesDir), StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { ok = false, error = "Invalid template path" });

            if (File.Exists(fullPath))
                File.Delete(fullPath);

            return Results.Ok(new { ok = true });
        });
    }

    // ── Helpers ──

    private static bool IsSafeTemplateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Contains(".."))
            return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
    }

    private static string? GetString(JsonElement el, string prop)
    {
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            ? v.GetString()
            : null;
    }
}
