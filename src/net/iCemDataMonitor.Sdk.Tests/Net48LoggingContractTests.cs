using System.Diagnostics;
using System.Runtime.InteropServices;

namespace iCemDataMonitor.Sdk.Tests;

public sealed class WindowsLoggingFactAttribute : FactAttribute
{
    public WindowsLoggingFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires installed Windows .NET Framework 4.8 runtime/reference assemblies";
    }
}

public sealed class Net48LoggingContractTests
{
    [WindowsLoggingFact]
    public void ExactLinkedCoreAndTapFacade_CompileWithRoslynCSharp73_AndRunOnNet48()
    {
        var result = Net48ContractRunner.CompileAndRun("TapLogGolden", new[] { "OwnedFileLog.cs", "TapLog.cs", "TapLogGolden.cs" });
        Assert.Contains("net48 CLR 4.", result);
        Assert.Contains("net48 TapLog golden vectors passed", result);
    }
}

internal static class Net48ContractRunner
{
    internal static string CompileAndRun(string executableName, string[] sourceFiles)
    {
        var directory = Path.Combine(Path.GetTempPath(), "contract_net48_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var dotnetRoot = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar))!.Parent!.Parent!.FullName;
            var compiler = Directory.GetDirectories(Path.Combine(dotnetRoot, "sdk"))
                .Select(sdk => Path.Combine(sdk, "Roslyn", "bincore", "csc.dll"))
                .Where(File.Exists).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).First();
            var references = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8");
            if (!Directory.Exists(references))
                references = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319");
            var output = Path.Combine(directory, executableName + ".exe");
            var compile = new ProcessStartInfo(Path.Combine(dotnetRoot, "dotnet.exe"));
            foreach (var argument in new[] { compiler, "/nologo", "/noconfig", "/nostdlib+", "/langversion:7.3", "/target:exe", "/out:" + output })
                compile.ArgumentList.Add(argument);
            foreach (var name in new[] { "mscorlib.dll", "System.dll", "System.Core.dll" })
            {
                var reference = Path.Combine(references, name);
                Assert.True(File.Exists(reference), "Missing .NET Framework reference: " + reference);
                compile.ArgumentList.Add("/reference:" + reference);
            }
            foreach (var name in sourceFiles)
                compile.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Net48", name));
            Run(compile);
            var execute = new ProcessStartInfo(output);
            execute.ArgumentList.Add(Path.Combine(directory, "vectors"));
            var result = Run(execute);
            return result;
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    private static string Run(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // Process hang guard, not a throughput assertion. Kill before disposing fixtures.
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("net48 contract helper did not finish: " + start.FileName);
        }
        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, output);
        return output;
    }
}
