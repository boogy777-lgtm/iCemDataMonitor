using System.Collections;
using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Plugins.Icem.Tests;

public sealed class CatalogSnapshotTests
{
    internal const string SyntheticJson = """
        {"version":1,"families":{"flecs":{"modbusBase":702},"plc":{"modbusBase":902},"lan":{"modbusBase":1102},"extra":{"modbusBase":1302}},"variables":[
          {"name":"Late","display":"Not alias","uom":"psi","color":"#123456","families":{"flecs":{"index":7,"alias":"Friendly"}}},
          {"name":"Hidden","excludeFromTelemetry":true,"families":{"flecs":{"index":2,"alias":"Invisible on bus"}}},
          {"name":"CustomPLC","families":{"plc":{"index":3,"alias":"PLC alias"}}},
          {"name":"CustomLAN","families":{"lan":{"index":9,"alias":"LAN alias"}}},
          {"name":"ExtraSignal","families":{"extra":{"index":5,"alias":"Extra alias"}}}]}
        """;

    internal static UnifiedVariableRegistry Read(string json)
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, json); return UnifiedVariableRegistry.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InjectedSnapshot_OwnsAllFourApisAndReadOnlyCachedProjections()
    {
        var registry = new DeviceCatalogRegistry(Read(SyntheticJson));
        Assert.Equal(new[] { "extra", "flecs", "lan", "plc" }, registry.GetFamilies().Order());
        Assert.Same(registry.GetFamilies(), registry.GetFamilies());
        foreach (var (family, name, offset, address) in new[]
        {
            ("flecs", "Late", 8, 716), ("plc", "CustomPLC", 103, 908),
            ("lan", "CustomLAN", 409, 1120), ("extra", "ExtraSignal", 0, 1312)
        })
        {
            var catalog = registry.GetCatalog(family);
            Assert.Same(catalog, registry.GetCatalog(family.ToUpperInvariant()));
            Assert.Same(catalog.GetCatalog(), catalog.GetCatalog());
            var map = registry.GetRegisterEntries(family);
            Assert.Same(map, registry.GetRegisterEntries(family.ToUpperInvariant()));
            Assert.Equal(address, Assert.Single(map).RegisterAddress);
            Assert.Equal(name, Assert.Single(map).VariableName);
            AssertReadOnly(map);
            Assert.Equal(address, registry.GetModbusBase(family.ToUpperInvariant()) + (family == "flecs" ? 7 : family == "plc" ? 3 : family == "lan" ? 9 : 5) * 2);
            if (family == "extra") { Assert.Empty(catalog.GetCatalog()); continue; }
            var before = catalog.Lookup(name.ToLowerInvariant());
            Assert.Equal(offset, before!.Value.ModbusOffset);
            AssertReadOnly(catalog.GetCatalog());
            Assert.Equal(before, catalog.Lookup(name));
        }
        var flecs = registry.GetCatalog("flecs");
        Assert.Equal(new[] { "Hidden", "Late" }, flecs.GetCatalog().Select(v => v.Name));
        Assert.Equal(("Friendly", "psi", (int?)8), flecs.Lookup("lAtE"));
        Assert.Null(flecs.Lookup("Friendly"));
        Assert.Same(registry.GetCatalog("unknown"), registry.GetCatalog("another"));
        Assert.Equal(2, registry.GetModbusBase("unknown"));
        Assert.Empty(registry.GetRegisterEntries("unknown"));
        AssertReadOnly((IReadOnlyList<string>)registry.GetFamilies());
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> entries)
    {
        var list = Assert.IsAssignableFrom<IList<T>>(entries);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(default!));
        if (entries.Count > 0)
            Assert.Throws<NotSupportedException>(() => list[0] = default!);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    [InlineData("unreadable")]
    public void OptionalFailure_IsOneAttemptAndFixedForLifetime_RecoveryNeedsNewInstance(string failure)
    {
        var attempts = 0;
        var recovered = false;
        UnifiedVariableRegistry Load()
        {
            attempts++;
            if (recovered) return Read(SyntheticJson);
            return failure switch
            {
                "missing" => throw new FileNotFoundException(),
                "unreadable" => throw new UnauthorizedAccessException(),
                "unsupported" => Read("{\"version\":99}"),
                _ => Read("{")
            };
        }
        var registry = DeviceCatalogRegistry.FromLoader(Load);
        recovered = true;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(34, registry.GetCatalog("flecs").GetCatalog().Count);
            Assert.Equal(10, registry.GetCatalog("plc").GetCatalog().Count);
            Assert.Equal(8, registry.GetCatalog("lan").GetCatalog().Count);
            Assert.Empty(registry.GetFamilies());
            Assert.Empty(registry.GetRegisterEntries("flecs"));
            Assert.Equal(2, registry.GetModbusBase("flecs"));
        }
        Assert.Equal(1, attempts);
        var next = DeviceCatalogRegistry.FromLoader(Load);
        Assert.Equal(702, next.GetModbusBase("flecs"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void FileMutationAndDeletion_DoNotChangeOwnedSnapshot()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, SyntheticJson);
            var calls = 0;
            var registry = DeviceCatalogRegistry.FromLoader(() => { calls++; return UnifiedVariableRegistry.Load(path); });
            var catalog = registry.GetCatalog("flecs");
            File.WriteAllText(path, "{\"version\":1}");
            Assert.Equal(702, registry.GetModbusBase("flecs"));
            File.Delete(path);
            Assert.Equal("Late", Assert.Single(registry.GetRegisterEntries("flecs")).VariableName);
            Assert.Equal(2, catalog.GetCatalog().Count);
            Assert.Same(catalog, registry.GetCatalog("FLECS"));
            Assert.Equal(1, calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SuccessfulEmptyFamily_DoesNotUseStaticFallback()
    {
        var registry = new DeviceCatalogRegistry(Read("{\"version\":1,\"families\":{\"flecs\":{\"modbusBase\":66}}}"));
        Assert.Empty(registry.GetCatalog("flecs").GetCatalog());
        Assert.Empty(registry.GetCatalog("plc").GetCatalog());
        Assert.Empty(registry.GetCatalog("lan").GetCatalog());
        Assert.Equal(new[] { "flecs" }, registry.GetFamilies());
        Assert.Equal(66, registry.GetModbusBase("flecs"));
    }

    [Fact]
    public void VariableOnlyFamily_KeepsEntriesWithDefaultBaseWithoutBecomingDeclared()
    {
        var registry = new DeviceCatalogRegistry(Read("""
            {"version":1,"variables":[{"name":"Orphan","families":{"orphan":{"index":6,"alias":"Variable only"}}}]}
            """));
        Assert.Empty(registry.GetFamilies());
        Assert.Equal(2, registry.GetModbusBase("ORPHAN"));
        Assert.Equal(new ModbusRegisterEntry("Orphan", 14), Assert.Single(registry.GetRegisterEntries("ORPHAN")));
    }

    [Fact]
    public void HealthyDeployment_AllEntriesKeepLegacyMetadataAndLookupPolicy()
    {
        var snapshot = ModbusRegistryFixture.LoadRegistry();
        var registry = new DeviceCatalogRegistry(snapshot);
        foreach (var (family, offset) in new[] { ("flecs", 1), ("plc", 100), ("lan", 400) })
        {
            var expected = snapshot.GetFamilyVariables(family);
            var catalog = registry.GetCatalog(family);
            Assert.Equal(expected.Count, catalog.GetCatalog().Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                Assert.Equal(new VariableDefinition(e.Name, e.Alias, e.Uom, e.Color, offset + e.Index, false, "registry"), catalog.GetCatalog()[i]);
                Assert.Equal((e.Alias, e.Uom, (int?)(offset + e.Index)), catalog.Lookup(e.Name.ToUpperInvariant()));
            }
        }
    }
}
