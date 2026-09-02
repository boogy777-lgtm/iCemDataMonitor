using System.Diagnostics;
using System.Runtime.Versioning;
using iCemDataMonitor.Transport.Models;

namespace iCemDataMonitor.Transport;

/// <summary>
/// Monitors the com0com null-modem driver: service state, port pairs, topology.
/// Degrades gracefully when com0com is not installed (all bools false, empty lists).
/// Windows-only by design (com0com kernel driver, %ProgramFiles(x86)%, setupc.exe —
/// debt_5: attribute, ¬ suppression).
///
/// Seams:
///   - ISystemProcess — abstraction over Process.Start (test seam for degradation tests)
///   - Expected topology: COM1↔COM2 (configurable via ExpectedPairs)
/// </summary>
[SupportedOSPlatform("windows")]
public interface ICom0ComMonitor
{
    Com0ComStatus GetStatus();
    Task<Com0ComStatus> RefreshAsync();
    /// <summary>Launch com0com config tool. Whitelist: setup, setupg, setupc.</summary>
    LaunchResult LaunchConfigTool(string tool);
}

public interface ISystemProcess
{
    int RunAndCapture(string exePath, string[] args, out string stdout, out string stderr);
    int LaunchDetached(string exePath);
}

public sealed record LaunchResult(bool Ok, string Message, int? ProcessId = null);

[SupportedOSPlatform("windows")]
public sealed class Com0ComMonitor : ICom0ComMonitor
{
    private const string Com0ComServiceName = "com0com";
    private static readonly string Com0ComDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "com0com");

    private static readonly HashSet<string> WhitelistedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "setupg", "setupc"
    };

    /// <summary>Expected port pairs for topology validation. Key=expected port name, Value=expected role.</summary>
    private static readonly (string PortA, string PortB)[] ExpectedPairs =
    {
        ("COM1", "COM2"),
    };

    private readonly ISystemProcess _processRunner;
    private readonly object _lock = new();
    private Com0ComStatus _cached = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromSeconds(10);

    public Com0ComMonitor() : this(new RealSystemProcess()) { }

    internal Com0ComMonitor(ISystemProcess processRunner)
    {
        _processRunner = processRunner;
    }

    public Com0ComStatus GetStatus()
    {
        lock (_lock) return CloneStatus(_cached);
    }

    public async Task<Com0ComStatus> RefreshAsync()
    {
        Com0ComStatus status;
        lock (_lock)
        {
            var sinceLast = DateTime.UtcNow - _lastRefresh;
            if (sinceLast < RefreshDebounce)
                return CloneStatus(_cached);
        }

        status = await Task.Run(() => BuildStatus());

        lock (_lock)
        {
            _cached = status;
            _lastRefresh = DateTime.UtcNow;
        }
        return CloneStatus(status);
    }

    public LaunchResult LaunchConfigTool(string tool)
    {
        tool = tool.Trim().ToLowerInvariant();
        if (!WhitelistedTools.Contains(tool))
            return new LaunchResult(false, $"Tool '{tool}' is not whitelisted. Allowed: setup, setupg, setupc");

        if (!Directory.Exists(Com0ComDir))
            return new LaunchResult(false, $"com0com directory not found: {Com0ComDir}");

        var exeName = tool + ".exe";
        var exePath = Path.Combine(Com0ComDir, exeName);

        // Path traversal guard: resolved path must be under Com0ComDir
        var fullPath = Path.GetFullPath(exePath);
        if (!fullPath.StartsWith(Com0ComDir, StringComparison.OrdinalIgnoreCase))
            return new LaunchResult(false, $"Path traversal rejected: {exeName}");

        if (!File.Exists(fullPath))
            return new LaunchResult(false, $"Tool not found: {fullPath}");

        try
        {
            int pid = _processRunner.LaunchDetached(fullPath);
            return new LaunchResult(true, $"{exeName} launched (PID {pid})", pid);
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, $"Failed to launch {exeName}: {ex.Message}");
        }
    }

    // ═══ Status builder ═══

    private Com0ComStatus BuildStatus()
    {
        var status = new Com0ComStatus { InstallDir = Com0ComDir };

        // 1. Check com0com driver service
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(Com0ComServiceName);
            status.DriverInstalled = true;
            status.DriverRunning = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException) // service not installed
        {
            status.DriverInstalled = false;
            status.DriverRunning = false;
            status.LastError = $"Service '{Com0ComServiceName}' not installed on this host";
            // Drivers not found — return early with degraded state (empty pairs, no match)
            if (!Directory.Exists(Com0ComDir))
                status.LastError += $"; directory '{Com0ComDir}' not found";
            return status;
        }

        // 2. Driver version (from file version)
        try
        {
            var driverPath = Path.Combine(Com0ComDir, "com0com.sys");
            if (File.Exists(driverPath))
            {
                var fvi = FileVersionInfo.GetVersionInfo(driverPath);
                status.DriverVersion = fvi.FileVersion ?? "";
            }
        }
        catch { /* non-critical */ }

        // 3. Parse `setupc.exe list` for port pairs
        if (status.DriverRunning)
        {
            try
            {
                var setupcPath = Path.Combine(Com0ComDir, "setupc.exe");
                if (File.Exists(setupcPath))
                {
                    int exitCode = _processRunner.RunAndCapture(setupcPath, new[] { "list" },
                        out var stdout, out var stderr);
                    if (exitCode == 0)
                        status.Pairs = ParseSetupcList(stdout);
                    else if (!string.IsNullOrWhiteSpace(stderr))
                        status.LastError = stderr.Trim();
                }
            }
            catch (Exception ex)
            {
                status.LastError = $"setupc.exe list failed: {ex.Message}";
            }
        }

        // 4. Validate topology
        status.TopologyMatch = ValidateTopology(status.Pairs);

        return status;
    }

    /// <summary>
    /// Parse `setupc.exe list` output lines like:
    ///   CNCA0 PortName=COM1,dsr=on,dcd=on
    /// </summary>
    internal static List<Com0ComPair> ParseSetupcList(string stdout)
    {
        var pairs = new List<Com0ComPair>();
        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var busLines = new Dictionary<int, List<string>>(); // busId → lines

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                continue;

            // Extract bus ID: typically "CNCA0" → bus 0, port A
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"^(?<bus>\w+?)(?<side>[AB])(?<busId>\d+)\s+(?<attrs>.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!match.Success) continue;

            var side = match.Groups["side"].Value.ToUpperInvariant();
            int busId = int.Parse(match.Groups["busId"].Value);
            var busName = match.Groups["bus"].Value + side + match.Groups["busId"].Value;

            var attrs = ParseAttrs(match.Groups["attrs"].Value);

            if (!busLines.ContainsKey(busId))
                busLines[busId] = new List<string>();

            // Store as serialized entry; we collect pairs after
            var entry = $"{side}|{busName}|{System.Text.Json.JsonSerializer.Serialize(attrs)}";
            busLines[busId].Add(entry);
        }

        foreach (var (busId, entries) in busLines.OrderBy(kv => kv.Key))
        {
            var aEntries = entries.Where(e => e.StartsWith("A|")).ToList();
            var bEntries = entries.Where(e => e.StartsWith("B|")).ToList();

            for (int i = 0; i < Math.Max(aEntries.Count, bEntries.Count); i++)
            {
                var pair = new Com0ComPair { BusId = busId };

                if (i < aEntries.Count)
                {
                    var parts = aEntries[i].Split('|');
                    pair.BusNameA = parts[1];
                    pair.AttrsA = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(parts[2]) ?? new();
                    pair.PortNameA = pair.AttrsA.TryGetValue("PortName", out var pa) ? pa : "";
                }
                if (i < bEntries.Count)
                {
                    var parts = bEntries[i].Split('|');
                    pair.BusNameB = parts[1];
                    pair.AttrsB = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(parts[2]) ?? new();
                    pair.PortNameB = pair.AttrsB.TryGetValue("PortName", out var pb) ? pb : "";
                }

                pairs.Add(pair);
            }
        }

        return pairs;
    }

    private static Dictionary<string, string> ParseAttrs(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
                dict[kv[0].Trim()] = kv[1].Trim();
        }
        return dict;
    }

    internal static TopologyMatchStatus ValidateTopology(IReadOnlyList<Com0ComPair> pairs)
    {
        var status = new TopologyMatchStatus();
        var actualPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pairs)
        {
            if (!string.IsNullOrEmpty(pair.PortNameA)) actualPorts.Add(pair.PortNameA);
            if (!string.IsNullOrEmpty(pair.PortNameB)) actualPorts.Add(pair.PortNameB);
        }

        var expectedPorts = ExpectedPairs.SelectMany(p => new[] { p.PortA, p.PortB }).ToList();
        foreach (var ep in expectedPorts)
        {
            if (!actualPorts.Contains(ep))
                status.Missing.Add(ep);
        }

        foreach (var ap in actualPorts)
        {
            if (!expectedPorts.Contains(ap, StringComparer.OrdinalIgnoreCase))
                status.Unexpected.Add(ap);
        }

        // Check that expected pairs are on the same bus
        foreach (var (portA, portB) in ExpectedPairs)
        {
            var pair = pairs.FirstOrDefault(p =>
                (string.Equals(p.PortNameA, portA, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(p.PortNameB, portB, StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(p.PortNameA, portB, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(p.PortNameB, portA, StringComparison.OrdinalIgnoreCase)));
            if (pair is null && !status.Missing.Contains(portA) && !status.Missing.Contains(portB))
            {
                // Both exist but not paired to each other
                status.MismatchedRoles.Add($"{portA}↔{portB}");
            }
        }

        status.Matched = status.Missing.Count == 0
                         && status.Unexpected.Count == 0
                         && status.MismatchedRoles.Count == 0;

        return status;
    }

    private static Com0ComStatus CloneStatus(Com0ComStatus src)
    {
        return new Com0ComStatus
        {
            DriverInstalled = src.DriverInstalled,
            DriverRunning = src.DriverRunning,
            DriverVersion = src.DriverVersion,
            InstallDir = src.InstallDir,
            Hub4ComInstalled = src.Hub4ComInstalled,
            Hub4ComRunning = src.Hub4ComRunning,
            Pairs = src.Pairs.Select(p => new Com0ComPair
            {
                BusId = p.BusId,
                BusNameA = p.BusNameA,
                BusNameB = p.BusNameB,
                PortNameA = p.PortNameA,
                PortNameB = p.PortNameB,
                AttrsA = new Dictionary<string, string>(p.AttrsA, StringComparer.OrdinalIgnoreCase),
                AttrsB = new Dictionary<string, string>(p.AttrsB, StringComparer.OrdinalIgnoreCase),
            }).ToList(),
            LastError = src.LastError,
            TopologyMatch = new TopologyMatchStatus
            {
                Matched = src.TopologyMatch.Matched,
                Missing = src.TopologyMatch.Missing.ToList(),
                Unexpected = src.TopologyMatch.Unexpected.ToList(),
                MismatchedRoles = src.TopologyMatch.MismatchedRoles.ToList(),
            },
        };
    }

    // ═══ Real process runner ═══

    /// <summary>
    /// Real Process.Start runner. Internal (¬ private) as the test seam for the
    /// concurrent-drain deadlock regression (Com0ComMonitorTests).
    /// </summary>
    internal sealed class RealSystemProcess : ISystemProcess
    {
        public int RunAndCapture(string exePath, string[] args, out string stdout, out string stderr)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var arg in args)
                proc.StartInfo.ArgumentList.Add(arg);

            proc.Start();

            // Drain BOTH pipes CONCURRENTLY. Sequential ReadToEnd(stdout) →
            // ReadToEnd(stderr) deadlocks forever when the child fills the
            // stderr pipe buffer (~4 KB) while we are still blocked on stdout:
            // the child then blocks on its stderr write and never closes stdout
            // ⇒ our stdout ReadToEnd never returns and the WaitForExit timeout
            // below is never reached.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(); } catch { /* already gone */ }
                proc.WaitForExit(5000);
            }

            // Post-Kill both pipes close ⇒ both readers complete; a bounded
            // wait guards against pathological driver-level stalls anyway.
            try { Task.WaitAll(new[] { stdoutTask, stderrTask }, 5000); }
            catch { /* timeouts/aggregate — fall through to partial results */ }

            stdout = stdoutTask.Status == TaskStatus.RanToCompletion ? stdoutTask.Result : "";
            stderr = stderrTask.Status == TaskStatus.RanToCompletion ? stderrTask.Result : "";
            return proc.HasExited ? proc.ExitCode : -1;
        }

        public int LaunchDetached(string exePath)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    CreateNoWindow = false,
                },
            };
            proc.Start();
            return proc.Id;
        }
    }
}