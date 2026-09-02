using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Host.Core.Endpoints;

/// <summary>
/// B5 REST endpoint 11: Modbus register map preview.
/// Contract: response shape MUST match wwwroot/mock/modbus-preview.json.
/// </summary>
public static class ModbusPreviewEndpoints
{
    public static void MapModbusPreviewEndpoints(this WebApplication app)
    {
        // 11. GET /api/modbus/preview?family=&configName=
        app.MapGet("/api/modbus/preview", (HttpContext ctx, IConfigStore config) =>
        {
            var family = ctx.Request.Query["family"].ToString();
            var configName = ctx.Request.Query["configName"].ToString();

            if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(configName))
                return Results.BadRequest(new { error = "family and configName query parameters required" });

            // Load the SSF template to get variable list.
            // Delegate to SsfTemplateLoader exclusively — it handles file-exists check
            // using AppDomain.CurrentDomain.BaseDirectory, avoiding path divergence with
            // AppContext.BaseDirectory (single-file / shadow-copy scenarios).
            var variables = SsfTemplateLoader.Load(configName);

            // Get modbus base offset for this family
            int baseOffset = 2; // default
            var registryPath = Path.Combine(AppContext.BaseDirectory, "templates", "variable_registry.json");
            if (File.Exists(registryPath))
            {
                var registry = UnifiedVariableRegistry.Load(registryPath);
                baseOffset = registry.GetModbusBase(family);
            }

            // Build register entries: order = position-1 (0-based), address = baseOffset + (order * 2)
            // Filter out spares
            var nonSpares = variables
                .Where(v => !v.IsSpare)
                .OrderBy(v => v.Position)
                .ToList();

            var entries = new List<object>(nonSpares.Count);
            for (int i = 0; i < nonSpares.Count; i++)
            {
                var v = nonSpares[i];
                var order = i; // 0-based display order
                var registerAddress = baseOffset + (order * 2);

                entries.Add(new
                {
                    order = order,
                    variableName = v.Name,
                    registerAddress = registerAddress,
                    uom = v.Uom,
                    source = v.Source,
                });
            }

            return Results.Ok(new { baseOffset, encoding = "float32", entries });
        });
    }
}
