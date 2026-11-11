using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk;
using iCemDataMonitor.Sdk.Models;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

public class CatalogSnapshotIntegrationTests
{
    [Fact]
    public async Task InjectedSnapshot_DrivesActiveCatalogAndCombinedMap_AfterSourceRemoval()
    {
        var path = Path.Combine(Path.GetTempPath(), $"icem-catalog-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "version": 1,
                  "families": {
                    "flecs": { "modbusBase": 300 },
                    "plc": { "modbusBase": 700 },
                    "future": { "modbusBase": 900 }
                  },
                  "variables": [
                    { "name": "SyntheticFlow", "display": "Unused display", "uom": "bpm", "color": "#123456",
                      "families": { "flecs": { "index": 3, "alias": "Synthetic flow" } } },
                    { "name": "SyntheticPressure", "uom": "psi", "color": "#654321",
                      "families": { "plc": { "index": 5, "alias": "Synthetic pressure" } } },
                    { "name": "HiddenStatus", "excludeFromTelemetry": true,
                      "families": { "flecs": { "index": 0, "alias": "Hidden status" } } },
                    { "name": "FutureValue", "families": { "future": { "index": 2, "alias": "Future value" } } }
                  ]
                }
                """);
            var snapshot = UnifiedVariableRegistry.Load(path);
            File.Delete(path);
            var registry = new DeviceCatalogRegistry(snapshot);
            var store = new InMemoryConfigStore(registry.GetCatalog("flecs"), registry: registry);
            var activeCatalog = new FamilyAwareCatalog(registry, new ConfigProvider(store));

            Assert.Equal(("Synthetic flow", "bpm", (int?)4), activeCatalog.Lookup("syntheticflow"));
            Assert.Null(activeCatalog.Lookup("SlurryDensity"));

            await store.ActivateConfigAsync("plc", "plc");
            Assert.Equal(("Synthetic pressure", "psi", (int?)105), activeCatalog.Lookup("syntheticpressure"));
            Assert.Null(activeCatalog.Lookup("SyntheticFlow"));

            // The combined wire map includes every declared family, independently of UI selection.
            Assert.Equal(
                new[]
                {
                    new ModbusRegisterEntry("SyntheticFlow", 306),
                    new ModbusRegisterEntry("SyntheticPressure", 710),
                    new ModbusRegisterEntry("FutureValue", 904)
                },
                store.GetModbusRegisterMap().OrderBy(entry => entry.RegisterAddress).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class ConfigProvider(IConfigStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IConfigStore) ? store : null;
    }
}
