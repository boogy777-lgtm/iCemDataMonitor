using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Host.Core.Endpoints;

/// <summary>
/// B5 REST endpoints 12: unified variable registry.
/// Contract: response shapes MUST match wwwroot/mock/registry*.json.
/// </summary>
public static class RegistryEndpoints
{
    public static void MapRegistryEndpoints(this WebApplication app)
    {
        // 12a. GET /api/registry → {families: {family: {modbusBase}}, variables: [...]}
        app.MapGet("/api/registry", () =>
        {
            var registryPath = Path.Combine(AppContext.BaseDirectory, "templates", "variable_registry.json");
            if (!File.Exists(registryPath))
                return Results.Ok(new { families = new Dictionary<string, object>(), variables = Array.Empty<object>() });

            var registry = UnifiedVariableRegistry.Load(registryPath);

            // Build families dict: {flecs: {modbusBase: 2}, plc: {modbusBase: 200}, ...}
            var families = new Dictionary<string, object>();
            foreach (var fam in registry.Families)
                families[fam] = new { modbusBase = registry.GetModbusBase(fam) };

            // Build merged variable list: one entry per canonical name, all families merged
            var fullList = new List<RegistryEntry>();
            foreach (var fam in registry.Families)
                fullList.AddRange(registry.GetFamilyVariables(fam));

            var variables = fullList
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var first = g.First();
                    var fams = new Dictionary<string, object>();
                    foreach (var e in g)
                    {
                        var famEntry = new Dictionary<string, object>
                        {
                            ["alias"] = e.Alias,
                            ["index"] = e.Index,
                        };
                        if (!string.IsNullOrEmpty(e.WireName) && e.WireName != e.Name)
                            famEntry["wireName"] = e.WireName;
                        fams[e.Family] = famEntry;
                    }

                    return (object)new
                    {
                        name = first.Name,
                        display = first.Display,
                        uom = first.Uom,
                        color = first.Color,
                        families = fams,
                    };
                })
                .ToList();

            return Results.Ok(new { families, variables });
        });

        // 12b. GET /api/registry/mapping?templateFamily=&templateName=
        app.MapGet("/api/registry/mapping", (HttpContext ctx) =>
        {
            var family = ctx.Request.Query["templateFamily"].ToString();
            var templateName = ctx.Request.Query["templateName"].ToString();

            if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(templateName))
                return Results.BadRequest(new { error = "templateFamily and templateName required" });

            var registryPath = Path.Combine(AppContext.BaseDirectory, "templates", "variable_registry.json");
            if (!File.Exists(registryPath))
                return Results.Ok(new { mappings = Array.Empty<object>() });

            var registry = UnifiedVariableRegistry.Load(registryPath);

            // Load the SSF template to get positions
            var tplPath = Path.Combine(AppContext.BaseDirectory, "templates", templateName + ".ssf");
            var templateVars = File.Exists(tplPath)
                ? SsfTemplateLoader.Load(templateName)
                : Array.Empty<VariableDefinition>();

            var registryFamilyVars = registry.GetFamilyVariables(family)
                .ToDictionary(e => e.Alias, e => e, StringComparer.OrdinalIgnoreCase);

            var mappings = new List<object>();
            foreach (var tv in templateVars.OrderBy(v => v.Position))
            {
                string? canonicalName = null;
                string status = "unmatched";

                if (registryFamilyVars.TryGetValue(tv.Name, out var re))
                {
                    canonicalName = re.Name;
                    status = "matched";
                }

                mappings.Add(new
                {
                    templatePosition = tv.Position - 1,
                    templateName = tv.Name,
                    canonicalName = canonicalName,
                    status = status,
                });
            }

            return Results.Ok(new { mappings });
        });
    }
}
