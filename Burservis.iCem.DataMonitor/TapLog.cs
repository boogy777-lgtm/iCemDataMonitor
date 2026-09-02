using System;
using System.IO;
using System.Text;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// FileTap debug evidence channel (TZ_ARCH_V2 §4: pipe/TCP removed, debug log optional).
    /// Lock-guarded, swallow-all, best-effort.
    ///
    /// ROTATION RULE (operator log policy, 2026-08-14):
    ///   cap = MaxBytes (25 MB); on exceed, the file is rewritten IN PLACE keeping
    ///   only the tail KeepBytes (12.5 MB) — the newest half survives, oldest half
    ///   is dropped (tail may start mid-line: acceptable for an evidence log).
    ///   Check cadence: every RotationCheckInterval (60th) write — worst-case
    ///   overshoot ≈ 60 lines.
    ///
    /// OLD-FILE DELETION (operator log policy): at the same rotation check,
    /// *.log files in the log directory older than MaxAgeDays (7 days) are
    /// deleted (BCL only, swallow-all — a deletion failure never hurts the host).
    /// Swallow-all overall: rotation/deletion failure never hurts the host.
    /// </summary>
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

        /// <summary>*.log files older than this are deleted at the rotation check.</summary>
        internal const int MaxAgeDays = 7;

        /// <summary>Test hook: when non-null, Log() writes to this path (production = null).</summary>
        internal static string PathOverride;

        private static readonly object Gate = new object();
        private static long _linesWritten;
        private static bool _defaultDirTried;

        public static void Log(string line)
        {
            if (line == null)
            {
                return;
            }
            try
            {
                lock (Gate)
                {
                    string path = PathOverride ?? DefaultPath;
                    if ((++_linesWritten % RotationCheckInterval) == 0)
                    {
                        try
                        {
                            TruncateIfNeeded(path);
                        }
                        catch
                        {
                            // rotation is best-effort — logging continues
                        }
                        try
                        {
                            DeleteOldLogs(path);
                        }
                        catch
                        {
                            // deletion is best-effort — logging continues
                        }
                    }
                    if (PathOverride == null && !_defaultDirTried)
                    {
                        // Lazy default-directory creation (operator log policy:
                        // "папка лениво"). Override paths (tests) keep the raw
                        // swallow semantics — a missing directory stays missing.
                        _defaultDirTried = true;
                        try
                        {
                            string dir = Path.GetDirectoryName(DefaultPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }
                        }
                        catch
                        {
                            // best-effort — AppendAllText below swallows too
                        }
                    }
                    File.AppendAllText(path, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                        + " " + line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // swallow — evidence channel must never hurt the host
            }
        }

        /// <summary>
        /// Delete *.log files in the log directory older than MaxAgeDays (BCL
        /// only, per-file swallow-all). The current log itself is included if
        /// stale (AppendAllText recreates it right after).
        /// </summary>
        internal static void DeleteOldLogs(string currentLogPath)
        {
            string dir;
            try
            {
                dir = Path.GetDirectoryName(currentLogPath);
            }
            catch
            {
                return;
            }
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.log");
            }
            catch
            {
                return;
            }
            DateTime cutoffUtc = DateTime.UtcNow.AddDays(-MaxAgeDays);
            foreach (string file in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // per-file swallow-all — one locked log must not stop the rest
                }
            }
        }

        /// <summary>
        /// If the log exceeds MaxBytes, rewrite it in place keeping the last
        /// KeepBytes (tail half). Must be called under Gate.
        /// </summary>
        internal static void TruncateIfNeeded(string path)
        {
            using (FileStream s = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                if (s.Length <= MaxBytes)
                {
                    return;
                }
                long keepStart = s.Length - KeepBytes;
                byte[] tail = new byte[KeepBytes];
                s.Position = keepStart;
                int read = 0;
                while (read < tail.Length)
                {
                    int n = s.Read(tail, read, tail.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }
                    read += n;
                }
                s.SetLength(0);
                s.Position = 0;
                s.Write(tail, 0, read);
                s.Flush();
            }
        }
    }
}
