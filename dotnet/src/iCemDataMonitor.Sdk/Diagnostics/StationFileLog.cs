namespace iCemDataMonitor.Sdk.Diagnostics;

/// <summary>Best-effort bounded diagnostic files. Operations on one path share an
/// in-process coordinator; different files proceed independently. Rotation keeps
/// the newest bytes and may start mid-line. This is not cross-process locking.</summary>
public static class StationFileLog
{
    public const string LogsRoot = @"C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs";
    public const long MaxBytes = OwnedFileLog.MaxBytes;
    internal const long KeepBytes = OwnedFileLog.KeepBytes;
    internal const int RotationCheckInterval = OwnedFileLog.RotationCheckInterval;
    public static string AppLogDir(string appName) => Path.Combine(LogsRoot, appName);

    /// <summary>Append UTC O timestamp, space, payload and platform newline as UTF-8.
    /// Optionally retain only this explicitly owned file; no directory scan occurs.</summary>
    public static void Write(string path, string line) => Write(path, line, true, 0);

    public static void Write(string path, string line, bool createParent, int retentionDays) =>
        OwnedFileLog.WriteRecord(path, line, createParent, retentionDays, FileLogFormat.Utc);

    /// <summary>UTC O and platform newline, UTF-8 without BOM.</summary>
    public static void WriteUtcWithoutBom(string path, string line) =>
        OwnedFileLog.WriteRecord(path, line, true, 0, FileLogFormat.UtcWithoutBom);

    /// <summary>Local HH:mm:ss.fff and platform newline, UTF-8 without BOM.</summary>
    public static void WriteLocalTime(string path, string line) =>
        OwnedFileLog.WriteRecord(path, line, true, 0, FileLogFormat.LocalTime);

    /// <summary>[local HH:mm:ss.fff tid=N] and CRLF, UTF-8 without BOM.</summary>
    public static void WriteLocalThread(string path, string line, int threadId) =>
        OwnedFileLog.WriteRecord(path, line, true, 0, FileLogFormat.LocalThread, threadId);

    public static void TruncateIfNeeded(string path) => OwnedFileLog.TruncateIfNeeded(path);

    /// <summary>Delete only the supplied owned paths, independently best-effort.</summary>
    public static void ClearOwned(IEnumerable<string> ownedPaths) => OwnedFileLog.Clear(ownedPaths);

    /// <summary>Delete stale files only from the supplied explicit ownership set.</summary>
    public static void DeleteOwnedOlderThan(IEnumerable<string> ownedPaths, DateTime cutoffUtc) =>
        OwnedFileLog.DeleteOlderThan(ownedPaths, cutoffUtc);
}
