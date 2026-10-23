using System.Globalization;
using System.Text;

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

    public static void Write(string path, string line, bool createParent, int retentionDays)
    {
        if (line is null) return;
        OwnedFileLog.Write(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            + " " + line + Environment.NewLine, createParent, retentionDays, Encoding.UTF8);
    }

    /// <summary>Append an already formatted record verbatim, UTF-8 without BOM.
    /// The caller owns timestamp, prefix and newline policy; size policy is shared.</summary>
    public static void WriteFormatted(string path, string record) =>
        OwnedFileLog.Write(path, record, true, 0, new UTF8Encoding(false));

    public static void TruncateIfNeeded(string path) => OwnedFileLog.TruncateIfNeeded(path);

    /// <summary>Delete only the supplied owned paths, independently best-effort.</summary>
    public static void ClearOwned(IEnumerable<string> ownedPaths) => OwnedFileLog.Clear(ownedPaths);

    /// <summary>Delete stale files only from the supplied explicit ownership set.</summary>
    public static void DeleteOwnedOlderThan(IEnumerable<string> ownedPaths, DateTime cutoffUtc) =>
        OwnedFileLog.DeleteOlderThan(ownedPaths, cutoffUtc);
}
