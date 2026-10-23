using iCemDataMonitor.Contracts;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Data.Telemetry;
using iCemDataMonitor.Host.Core.Health;
using iCemDataMonitor.Host.Grpc;
using iCemDataMonitor.Host.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace iCemDataMonitor.Host.Tests;

public sealed class UiThemeRpcTests
{
    [Fact]
    public async Task SetUiTheme_FailedCommitAnswersCurrentDarkWithoutPokeAndRecovers()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tsvc_theme_fault_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ui-theme");
        Directory.CreateDirectory(path); // deterministic fault, including Linux root
        var pokes = 0;
        var logs = new List<string>();
        using var reader = new BusReader(Path.Combine(dir, "unopened-bus"));
        using var provider = new ServiceCollection().BuildServiceProvider();
        var prefs = new UiPrefsStore(path, () => pokes++, logs.Add);
        var svc = new TelemetryServiceImpl(new StationHealthState(), reader, new MetricsSampler(reader),
            new AutostartManager(runKeyPath: "unused", valueName: "unused", exePath: "unused"),
            new ConsoleVisibilityService(null, () => ConsolePresence.None, (_, _) => { }),
            prefs, provider, NullLogger<TelemetryServiceImpl>.Instance);
        try
        {
            Assert.False(reader.IsOpen);
            var reply = await svc.SetUiTheme(new SetUiThemeRequest { ThemeLight = true }, new TestCallContext());
            Assert.False(reply.ThemeLight);
            Assert.Equal(0, pokes);
            Assert.Contains(logs, x => x.Contains("write FAILED"));
            Assert.DoesNotContain(logs, x => x.Contains("persisted") || x.Contains("saved"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Directory.Delete(path);
            Assert.True((await svc.SetUiTheme(new SetUiThemeRequest { ThemeLight = true }, new TestCallContext())).ThemeLight);
            Assert.Equal(1, pokes);
            Assert.Equal("light", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

}
