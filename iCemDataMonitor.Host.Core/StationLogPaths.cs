using iCemDataMonitor.Sdk.Diagnostics;

namespace iCemDataMonitor.Host.Core;

/// <summary>
/// Owner decree (W22, ProgramData logging): every application writes its
/// error/crash/fatal logs under
/// C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\&lt;AppName&gt;\.
/// The root and per-app directories are OWNED by the canonical Sdk
/// (<see cref="StationFileLog.LogsRoot"/> / <see cref="StationFileLog.AppLogDir"/>) —
/// this class only names the host's concrete files. All consumers write through
/// <see cref="StationFileLog"/> (25 MB cap, newest-half in-place truncation,
/// best effort).
/// </summary>
public static class StationLogPaths
{
    /// <summary>Per-application log directory root (ProgramData).</summary>
    public static string Root => StationFileLog.LogsRoot;

    /// <summary>Log directory of one application (created lazily by the writer).</summary>
    public static string ForApp(string appName) => StationFileLog.AppLogDir(appName);

    /// <summary>The headless host's log directory.</summary>
    public static string HostDir => ForApp("iCemDataMonitor.Host");

    /// <summary>Host startup log (redirected from the legacy exe-side startup.log).</summary>
    public static string HostStartupLog => Path.Combine(HostDir, "startup.log");

    // 2026-08-27: HostEngineSupervisorLog REMOVED — engine supervision is
    // deleted (the emulator is a separate independent app with its own logs).
}
