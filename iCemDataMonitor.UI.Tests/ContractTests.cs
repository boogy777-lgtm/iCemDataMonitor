using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.UI.Tests;

public class ContractTests : IAsyncLifetime
{
    private readonly Mock<ITimeseriesStore> _timeseriesMock = new();
    private readonly Mock<IConfigStore> _configMock = new();
    private readonly Mock<IDataSource> _dataSourceMock = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = await CreateClientAsync();
    }

    private async Task<HttpClient> CreateClientAsync()
    {
        // ─── build a test host that mirrors Program.cs routes with mocked services ───
        // W22: WebHostBuilder is obsolete on net10 (ASPDEPR004) — minimal
        // hosting + UseTestServer, same shape as B5ContractTests.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton(_timeseriesMock.Object);
        builder.Services.AddSingleton(_configMock.Object);
        builder.Services.AddSingleton(_dataSourceMock.Object);

        var app = builder.Build();

        // endpoint 2: GET /api/data
        app.MapGet("/api/data", async (HttpContext ctx) =>
        {
            var names = ctx.Request.Query["names"].ToString();
            var nameList = ParseNames(names);
            var store = ctx.RequestServices.GetRequiredService<ITimeseriesStore>();
            var history = await store.GetHistoryAsync(nameList, limit: 500);
            var result = history.Select(s => new { ts = s.Timestamp, values = s.Values });
            await ctx.Response.WriteAsJsonAsync(result);
        });

        // endpoint 3: GET /api/variables
        app.MapGet("/api/variables", (HttpContext ctx) =>
        {
            var config = ctx.RequestServices.GetRequiredService<IConfigStore>();
            var vars = config.GetActiveVariables();
            return ctx.Response.WriteAsJsonAsync(vars);
        });

        // endpoint 4: GET /api/devices
        app.MapGet("/api/devices", (HttpContext ctx) =>
        {
            var config = ctx.RequestServices.GetRequiredService<IConfigStore>();
            var families = config.GetAllFamilies();
            var result = families.Select(f => new
            {
                family = f.Name,
                configs = f.Configs.Select(c => new { name = c.Name })
            });
            return ctx.Response.WriteAsJsonAsync(result);
        });

        // endpoint 5: GET /api/config/active
        app.MapGet("/api/config/active", (HttpContext ctx) =>
        {
            var config = ctx.RequestServices.GetRequiredService<IConfigStore>();
            var active = config.GetActiveConfig();
            if (active is null)
            {
                return ctx.Response.WriteAsJsonAsync(new { family = "", name = "" });
            }
            var family = FindFamilyForConfig(config, active.Value.Name);
            return ctx.Response.WriteAsJsonAsync(new { family = family ?? "", name = active.Value.Name });
        });

        // endpoint 7: GET /api/status
        app.MapGet("/api/status", async (HttpContext ctx) =>
        {
            // In tests, we mock IDataSource — DiscoverAsync success/failure dictates db_ok
            // Skip real Windows service checks; test only DB path
            var ds = ctx.RequestServices.GetRequiredService<IDataSource>();
            bool dbOk;
            try
            {
                await ds.DiscoverAsync();
                dbOk = true;
            }
            catch
            {
                dbOk = false;
            }
            await ctx.Response.WriteAsJsonAsync(new { db = dbOk, icem = false });
        });

        // endpoint 8: GET /api/export/csv
        app.MapGet("/api/export/csv", async (HttpContext ctx) =>
        {
            var names = ctx.Request.Query["names"].ToString();
            var nameList = ParseNames(names);
            var store = ctx.RequestServices.GetRequiredService<ITimeseriesStore>();
            var history = await store.GetHistoryAsync(nameList, limit: 10000);

            var sb = new System.Text.StringBuilder();
            var headerNames = nameList ?? new List<string>();
            sb.Append("Timestamp,");
            sb.AppendLine(string.Join(",", headerNames));

            foreach (var sample in history)
            {
                sb.Append(sample.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(',');
                var values = headerNames.Select(n =>
                    sample.Values.TryGetValue(n, out var v) ? v.ToString("F4") : "");
                sb.AppendLine(string.Join(",", values));
            }

            ctx.Response.ContentType = "text/csv";
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename=icem_export.csv";
            await ctx.Response.WriteAsync(sb.ToString());
        });

        await app.StartAsync();
        var server = (TestServer)app.Services.GetRequiredService<IServer>();
        return server.CreateClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Contract Tests ───

    [Fact]
    public async Task ContractTest_GetDataReturnsJson()
    {
        var now = DateTime.UtcNow;
        var sample = new TimeSeriesSample(
            now,
            new Dictionary<string, double> { { "FLOW", 12.5 }, { "PRESS", 100.0 } }
        );
        _timeseriesMock
            .Setup(s => s.GetHistoryAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(new[] { sample });

        var response = await _client.GetAsync("/api/data?names=FLOW,PRESS");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        Assert.NotNull(json);
        Assert.Contains("FLOW", json);
        Assert.Contains("12.5", json);
        // Verify Python-compatible shape: { ts, values }
        Assert.Contains("\"ts\"", json);
        Assert.Contains("\"values\"", json);
    }

    [Fact]
    public async Task ContractTest_StatusReturnsDbAndIcem()
    {
        _dataSourceMock.Setup(d => d.DiscoverAsync())
            .ReturnsAsync(new List<VariableDefinition>());

        var response = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(body);
        // db_ok = true because DiscoverAsync succeeds
        Assert.True((bool)((System.Text.Json.JsonElement)body["db"]).GetBoolean());
    }

    [Fact]
    public async Task ContractTest_CsvExportReturnsAttachment()
    {
        _timeseriesMock
            .Setup(s => s.GetHistoryAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(Array.Empty<TimeSeriesSample>());

        var response = await _client.GetAsync("/api/export/csv?names=A,B");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Contains("icem", disposition.FileName ?? "");
    }

    [Fact]
    public void ContractTest_WebAppDist_IsGone()
    {
        // 2026-08-27 (owner decree): the web SPA is REMOVED — the WebApp
        // project (WebApp\dist committed artifact) was deleted. Walk up from
        // the test output directory (depth varies between AnyCPU and x64
        // layouts — D2-class defect, fixed by upward search) and assert the
        // SPA bundle is nowhere in the tree.
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? fullPath = null;
        while (dir is not null && fullPath is null)
        {
            var candidate = Path.Combine(dir.FullName, "WebApp", "dist", "index.html");
            if (File.Exists(candidate))
                fullPath = candidate;
            dir = dir.Parent;
        }
        Assert.True(fullPath is null, $"WebApp\\dist\\index.html still present at {fullPath} — the SPA must stay removed");
    }

    // ─── helpers (mirror Program.cs) ───

    private static IReadOnlyList<string>? ParseNames(string? names)
    {
        if (string.IsNullOrWhiteSpace(names)) return null;
        return names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? FindFamilyForConfig(IConfigStore config, string configName)
    {
        foreach (var f in config.GetAllFamilies())
        {
            if (f.Configs.Any(c => string.Equals(c.Name, configName, StringComparison.OrdinalIgnoreCase)))
                return f.Name;
        }
        return null;
    }

}
