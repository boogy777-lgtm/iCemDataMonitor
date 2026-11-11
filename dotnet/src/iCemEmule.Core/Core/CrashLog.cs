using System;
using System.IO;

namespace Halliburton.iCemEmule.Core;

public static class CrashLog
{
    private static string DefaultPath =
        @"C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\iCemEmule\crash.log";

    /// <summary>
    /// 2026-08-27 (merged app): retarget the default log folder to another app
    /// name under the operator log policy root
    /// (C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\<AppName>\crash.log).
    /// iCemEmulator.exe calls UseAppName("iCemEmulator") at startup; the bare
    /// engine (dev console hosting) keeps the iCemEmule folder.
    /// </summary>
    public static void UseAppName(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return;
        DefaultPath = Path.Combine(
            @"C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs",
            appName.Trim(), "crash.log");
    }

    /// <summary>Size cap: on exceed ⇒ keep only the tail half.</summary>
    internal const long MaxBytes = 25L * 1024 * 1024;        // 25 MB

    /// <summary>Tail kept after truncation (newest half).</summary>
    internal const long KeepBytes = 12L * 1024 * 1024 + 512L * 1024;  // 12.5 MB

    /// <summary>Rotation check runs once every N writes (cheap amortization).</summary>
    internal const int RotationCheckInterval = 60;

    /// <summary>The explicitly owned current log expires after this many days.</summary>
    internal const int MaxAgeDays = 7;

    /// <summary>Test hook: when non-null, Log() writes to this path (production = null).</summary>
    internal static string? PathOverride { get; set; }

    private static readonly object Gate = new object();
    private static bool _defaultDirTried;

    public static void Log(string? line)
    {
        if (line == null) return;
        try
        {
            lock (Gate)
            {
                string path = PathOverride ?? DefaultPath;
                if (PathOverride is null && !_defaultDirTried)
                {
                    _defaultDirTried = true;
                    try
                    {
                        var dir = Path.GetDirectoryName(DefaultPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    }
                    catch { }
                }
                iCemDataMonitor.Sdk.Diagnostics.StationFileLog.Write(path, line, createParent: false, retentionDays: MaxAgeDays);
            }
        }
        catch { }
    }

    internal static void DeleteOldLogs(string currentLogPath) =>
        iCemDataMonitor.Sdk.Diagnostics.StationFileLog.DeleteOwnedOlderThan(
            new[] { currentLogPath }, DateTime.UtcNow.AddDays(-MaxAgeDays));

    internal static void TruncateIfNeeded(string path) =>
        iCemDataMonitor.Sdk.Diagnostics.StationFileLog.TruncateIfNeeded(path);
}
