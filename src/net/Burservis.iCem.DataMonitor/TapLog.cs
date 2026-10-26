using System;
using System.IO;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>Best-effort bounded AddIn evidence channel; retains only its explicitly owned file.</summary>
    public static class TapLog
    {
        /// <summary>
        /// Operator log policy (2026-08-26): error/diagnostic logs live under
        /// C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\&lt;AppName&gt;\.
        /// The directory is created lazily on the first write (default path only —
        /// PathOverride keeps raw swallow semantics for tests).
        /// </summary>
        private const string DefaultPath =
            @"C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\Burservis.iCem.DataMonitor\TapOut.log";

        /// <summary>Size cap: on exceed ⇒ keep only the tail half.</summary>
        internal const long MaxBytes = 25L * 1024 * 1024;        // 25 MB

        /// <summary>Tail kept after truncation (newest half).</summary>
        internal const long KeepBytes = 12L * 1024 * 1024 + 512L * 1024;  // 12.5 MB

        /// <summary>Rotation check runs once every N writes (cheap amortization).</summary>
        internal const int RotationCheckInterval = 60;

        /// <summary>The explicitly owned current log expires after this many days.</summary>
        internal const int MaxAgeDays = 7;

        /// <summary>Test hook: when non-null, Log() writes to this path (production = null).</summary>
        internal static string PathOverride;

        private static readonly object Gate = new object();
        private static bool _defaultDirTried;

        public static void Log(string line)
        {
            if (line == null) return;
            try
            {
                lock (Gate)
                {
                    string path = PathOverride ?? DefaultPath;
                    if (PathOverride == null && !_defaultDirTried)
                    {
                        _defaultDirTried = true;
                        try
                        {
                            string dir = Path.GetDirectoryName(DefaultPath);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        }
                        catch { }
                    }
                    iCemDataMonitor.Sdk.Diagnostics.OwnedFileLog.WriteRecord(path, line,
                        false, MaxAgeDays, iCemDataMonitor.Sdk.Diagnostics.FileLogFormat.Utc);
                }
            }
            catch { }
        }

        internal static void DeleteOldLogs(string currentLogPath)
        {
            iCemDataMonitor.Sdk.Diagnostics.OwnedFileLog.DeleteOlderThan(
                new[] { currentLogPath }, DateTime.UtcNow.AddDays(-MaxAgeDays));
        }

        internal static void TruncateIfNeeded(string path)
        {
            iCemDataMonitor.Sdk.Diagnostics.OwnedFileLog.TruncateIfNeeded(path);
        }
    }
}
