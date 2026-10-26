using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Sdk;
using Xunit;

namespace iCemDataMonitor.Data.Tests;

/// <summary>
/// Bus v2 architecture invariants (v1 storage/Mongo asserts deleted with the v1
/// god structure — R5: v1 tests are deleted/rewritten, not fixed).
/// </summary>
public class ArchitectureTests
{
    /// <summary>Bus v2 = BCL-only data layer: the bus surface (BusReader/BusMapping) references no external packages.</summary>
    [Fact]
    public void ArchTest_BusLayer_BclOnly()
    {
        var projectDir = FindProjectDir();
        var files = new[]
        {
            Path.Combine(projectDir, "Bus", "BusReader.cs"),
            Path.Combine(projectDir, "Bus", "BusMapping.cs"),
        };

        foreach (var file in files)
        {
            Assert.True(File.Exists(file), $"File not found: {file}");
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("Microsoft.Data", content);
            Assert.DoesNotContain("MongoDB", content);
        }
    }

    /// <summary>
    /// PRODUCER-OWNED BUS (INV-39, 2026-08-27): the host data layer carries NO
    /// write machinery — the shipped bus sources must not contain write-capable
    /// file/mapping opens (the AddIn is the only Data.dat writer).
    /// </summary>
    [Fact]
    public void ArchTest_BusLayer_NoWriteSurface()
    {
        var projectDir = FindProjectDir();
        var busDir = Path.Combine(projectDir, "Bus");
        foreach (var file in Directory.GetFiles(busDir, "*.cs"))
        {
            var content = File.ReadAllText(file);
            Assert.False(content.Contains("FileAccess.ReadWrite", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} must not open Data.dat for writing (producer-only, INV-39)");
            Assert.False(content.Contains("MemoryMappedFileAccess.ReadWrite", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} must not map ReadWrite views (producer-only, INV-39)");
            Assert.False(content.Contains("FileMode.Create", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} must not create the bus file (producer-owned, INV-39)");
        }
    }

    /// <summary>All concrete bus classes implement the correct contracts.</summary>
    [Fact]
    public void ArchTest_ImplementsContracts()
    {
        Assert.True(typeof(InMemoryConfigStore).IsAssignableTo(typeof(IConfigStore)),
            "InMemoryConfigStore must implement IConfigStore");
    }

    /// <summary>BusReader is the shared read surface — sealed, standalone, no BackgroundService heritage.</summary>
    [Fact]
    public void ArchTest_BusReader_IsStandaloneReadSurface()
    {
        var type = typeof(BusReader);
        Assert.True(type.IsSealed);
        Assert.False(type.IsSubclassOf(typeof(Microsoft.Extensions.Hosting.BackgroundService)),
            "BusReader must not be a hosted service — callers poll Snapshot() themselves");
        Assert.Equal(typeof(object), type.BaseType);
    }

    private static string FindProjectDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "iCemDataMonitor.Data");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "iCemDataMonitor.Data.csproj")))
                return candidate;

            var sibling = Path.Combine(dir.FullName, "..", "iCemDataMonitor.Data");
            if (Directory.Exists(sibling) && File.Exists(Path.Combine(sibling, "iCemDataMonitor.Data.csproj")))
                return Path.GetFullPath(sibling);

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Cannot find iCemDataMonitor.Data project directory.");
    }
}
