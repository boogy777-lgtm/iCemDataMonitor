using System.Reflection;

namespace iCemDataMonitor.Transport.Tests;

public class TransportArchitectureTests
{
    private static readonly string TransportProjectDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "iCemDataMonitor.Transport");

    /// <summary>
    /// ModbusTcpPublisher (v2) MUST NOT reference SSF template loading, the v1
    /// ITimeseriesStore refresh path, the v1 14000-register family map, or any
    /// float encoding — the register file is a PURE word memcpy from the bus
    /// snapshot (CONTRACT_BusV2 §3 memcpy_law, TZ_ARCH_V2 §5).
    /// </summary>
    [Fact]
    public void ArchTest_NoModbusMapProduction()
    {
        var filePath = Path.Combine(TransportProjectDir, "ModbusTcpPublisher.cs");
        Assert.True(File.Exists(filePath), $"ModbusTcpPublisher.cs not found at {filePath}");

        var content = File.ReadAllText(filePath);

        Assert.DoesNotContain("SsfTemplateLoader", content);
        Assert.DoesNotContain("DeviceFamilyRegistry", content);
        Assert.DoesNotContain("load_template", content);
        // W3 v2 removal markers (R5): no timeseries pull, no v1 family map, no float work.
        Assert.DoesNotContain("ITimeseriesStore", content);
        Assert.DoesNotContain("GetCurrentAsync", content);
        Assert.DoesNotContain("16384", content);
        Assert.DoesNotContain("SingleToUInt32Bits", content);
        Assert.DoesNotContain("CdabCodec", content);
        Assert.Contains("BusReader", content);
    }

    /// <summary>
    /// Transport layer MUST reference only interface abstractions —
    /// no concrete database drivers (SqliteConnection, MongoDB.Driver) anywhere.
    /// </summary>
    [Fact]
    public void ArchTest_NoDirectDbAccess()
    {
        var transportDir = TransportProjectDir;
        var allFiles = Directory.GetFiles(transportDir, "*.cs", SearchOption.AllDirectories);

        var content = string.Join("\n", allFiles.Select(File.ReadAllText));

        Assert.DoesNotContain("SqliteConnection", content);
        Assert.DoesNotContain("MongoDB.Driver", content);
        Assert.DoesNotContain("SqliteCommand", content);
        Assert.DoesNotContain("MongoClient", content);
    }

    /// <summary>
    /// Both publishers MUST implement their respective SDK interfaces.
    /// </summary>
    [Fact]
    public void ArchTest_ImplementsContracts()
    {
        var transportAssembly = Assembly.Load("iCemDataMonitor.Transport");

        Assert.Contains(transportAssembly.GetTypes(),
            t => typeof(iCemDataMonitor.Sdk.IModbusPublisher).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
    }
}