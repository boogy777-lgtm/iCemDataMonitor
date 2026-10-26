using iCemDataMonitor.Sdk.Diagnostics;

namespace iCemDataMonitor.Data;

/// <summary>
/// ProgramData error-log wiring for the iCemDataMonitor.Data layer
/// (owner log policy 2026-08-26): error lines go to
/// C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\&lt;AppName&gt;\data-bus.log
/// through the Sdk canon <see cref="StationFileLog"/> (25 MB cap, in-place
/// rewrite keeping the newest half, UTF-8 ISO-8601 lines, NEVER throws).
///
/// DEDUP LAW: open-path pollers (ring samplers at 1 Hz, health monitor,
/// Modbus tick) retry TryOpen every cycle while the bus is absent or broken —
/// the SAME failure line is written ONCE until a different line occurs or an
/// open SUCCEEDS (<see cref="Success"/> re-arms the gate). The evidence log
/// stays readable; the persisted bus payload (Data.dat) is NOT a log and is
/// never touched by this channel.
///
/// Test seams (§15.7(2a) data-seam — paths are DATA): <see cref="LogDirOverride"/>
/// redirects the directory in unit tests; <see cref="Enabled"/> is the kill
/// switch flipped off by the test assembly's module initializer so unit runs
/// never write the real ProgramData evidence log. Production leaves both at
/// their defaults.
/// </summary>
public static class DataBusLog
{
    /// <summary>Application subdirectory per the owner policy (Logs\&lt;AppName&gt;\).
    /// Data-layer bus errors surface in the host app's log directory.</summary>
    public static string AppName { get; set; } = "iCemDataMonitor.Host";

    /// <summary>Unit-test redirect of the log directory; null ⇒ policy default.</summary>
    public static string? LogDirOverride { get; set; }

    /// <summary>Kill switch (unit-test assemblies disable this channel entirely).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Generic test-context guard: under <c>dotnet test</c> the ENTRY assembly
    /// is the VSTest execution host (<c>testhost*</c>), never a station app —
    /// so unit runs of ANY project (incl. ones without their own bootstrap)
    /// never write the operator's ProgramData evidence log. Production entry
    /// points (iCemDataMonitor.Host.exe et al.) are unaffected.
    /// </summary>
    private static readonly bool IsTestHostContext = IsTestHostName(
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name);

    /// <summary>Test seam (§15.7(2a)): null = auto-detect; true/false forces the
    /// test-context verdict. Internal — Data.Tests flips it to exercise the channel.</summary>
    internal static bool? TestContextOverride;

    private static bool IsTestHostName(string? name) =>
        name is not null &&
        (name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase));

    private const string FileName = "data-bus.log";

    private static readonly object Gate = new();
    private static string? _lastLogged;

    /// <summary>
    /// Record one error line (deduplicated per <see cref="DataBusLog"/> docs).
    /// NEVER throws — a logging fault must never hurt the bus path
    /// (evidence-channel law, canon TapLog).
    /// </summary>
    public static void Error(string line)
    {
        try
        {
            if (!Enabled || (TestContextOverride ?? IsTestHostContext) || string.IsNullOrEmpty(line))
                return;

            lock (Gate)
            {
                if (string.Equals(_lastLogged, line, StringComparison.Ordinal))
                    return; // same failure repeating every tick — already recorded
                _lastLogged = line;
            }

            var dir = LogDirOverride ?? StationFileLog.AppLogDir(AppName);
            StationFileLog.Write(Path.Combine(dir, FileName), line);
        }
        catch
        {
            // swallow-all — the evidence channel must never hurt the caller
        }
    }

    /// <summary>Re-arm the dedup gate after a SUCCESSFUL operation so the next
    /// failure is recorded again (flapping stays visible in the evidence log).</summary>
    public static void Success()
    {
        try
        {
            lock (Gate)
            {
                _lastLogged = null;
            }
        }
        catch
        {
            // swallow-all
        }
    }
}
