namespace iCemDataMonitor.Data;

/// <summary>
/// Logs-policy helper (operator directive): startup-time cleanup of the
/// &lt;exe&gt;\logs\ directory — every *.log file older than 7 days is deleted.
/// BCL-only, swallow-all (log hygiene must never break startup).
/// </summary>
public static class LogsCleanup
{
    /// <summary>Default retention: 7 days.</summary>
    public const int DefaultMaxAgeDays = 7;

    /// <summary>
    /// Delete *.log files older than <paramref name="maxAgeDays"/> in the given
    /// directory (default: &lt;exe dir&gt;\logs). Never throws; no-op when the
    /// directory does not exist.
    /// </summary>
    public static void DeleteOldLogs(string? logsDir = null, int maxAgeDays = DefaultMaxAgeDays)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(logsDir)
                ? Path.Combine(AppContext.BaseDirectory, "logs")
                : logsDir;
            if (!Directory.Exists(dir))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, maxAgeDays));
            foreach (var file in Directory.EnumerateFiles(dir, "*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // swallow-all — a single locked file must not abort the sweep
                }
            }
        }
        catch
        {
            // swallow-all — log hygiene must never break startup
        }
    }
}
