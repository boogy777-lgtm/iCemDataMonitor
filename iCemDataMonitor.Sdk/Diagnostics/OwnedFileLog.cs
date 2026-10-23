// Linked verbatim into the net48 AddIn. Keep this file BCL-only / C# 7.3.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;

namespace iCemDataMonitor.Sdk.Diagnostics
{
    internal enum FileLogFormat { Utc, UtcWithoutBom, LocalTime, LocalThread }

    internal static class OwnedFileLog
    {
        internal const long MaxBytes = 25L * 1024 * 1024;
        internal const long KeepBytes = 12L * 1024 * 1024 + 512L * 1024;
        internal const int RotationCheckInterval = 60;
        private sealed class FileGate
        {
            internal readonly object Sync = new object();
            internal long Writes;
        }
        private static readonly ConcurrentDictionary<string, FileGate> Gates =
            new ConcurrentDictionary<string, FileGate>(Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private static FileGate Gate(string path) { return Gates.GetOrAdd(Path.GetFullPath(path), _ => new FileGate()); }

        // The sink alone captures and formats time. Policies are the existing record
        // shapes, not arbitrary caller formatters; raw Write is internal IO machinery.
        internal static void WriteRecord(string path, string line, bool createParent, int retentionDays,
            FileLogFormat format, int threadId = 0)
        {
            if (path == null || line == null) return;
            try
            {
                lock (Gate(path).Sync)
                {
                    var timestamp = format == FileLogFormat.LocalTime || format == FileLogFormat.LocalThread
                        ? DateTime.Now : DateTime.UtcNow;
                    Write(path, FormatRecord(line, timestamp, format, threadId), createParent, retentionDays,
                        format == FileLogFormat.Utc ? Encoding.UTF8 : new UTF8Encoding(false));
                }
            }
            catch { }
        }

        internal static string FormatRecord(string line, DateTime timestamp, FileLogFormat format, int threadId)
        {
            switch (format)
            {
                case FileLogFormat.Utc:
                case FileLogFormat.UtcWithoutBom:
                    return timestamp.ToString("O", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine;
                case FileLogFormat.LocalTime:
                    return timestamp.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture) + " " + line + Environment.NewLine;
                case FileLogFormat.LocalThread:
                    return "[" + timestamp.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture)
                        + " tid=" + threadId.ToString(CultureInfo.CurrentCulture) + "] " + line + "\r\n";
                default: throw new ArgumentOutOfRangeException(nameof(format));
            }
        }

        internal static void Write(string path, string text, bool createParent, int retentionDays, Encoding encoding)
        {
            if (path == null || text == null) return;
            try
            {
                var state = Gate(path);
                lock (state.Sync)
                {
                    if (++state.Writes % RotationCheckInterval == 0)
                    {
                        TruncateIfNeeded(path);
                        if (retentionDays > 0) DeleteOlderThan(new[] { path }, DateTime.UtcNow.AddDays(-retentionDays));
                    }
                    if (createParent)
                    {
                        var directory = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    }
                    File.AppendAllText(path, text, encoding);
                }
            }
            catch { /* evidence must never hurt its caller */ }
        }

        internal static void TruncateIfNeeded(string path)
        {
            if (path == null) return;
            try
            {
                lock (Gate(path).Sync)
                using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    if (stream.Length <= MaxBytes) return;
                    var tail = new byte[KeepBytes];
                    stream.Position = stream.Length - KeepBytes;
                    int read = 0;
                    while (read < tail.Length)
                    {
                        int count = stream.Read(tail, read, tail.Length - read);
                        if (count == 0) break;
                        read += count;
                    }
                    stream.SetLength(0);
                    stream.Position = 0;
                    stream.Write(tail, 0, read);
                    stream.Flush();
                }
            }
            catch { }
        }

        // An explicit path set is the ownership boundary. Never scan a directory.
        internal static void DeleteOlderThan(IEnumerable<string> ownedPaths, DateTime cutoffUtc)
        {
            Visit(ownedPaths, path =>
            {
                lock (Gate(path).Sync)
                    if (File.Exists(path) && File.GetLastWriteTimeUtc(path) < cutoffUtc) File.Delete(path);
            });
        }

        internal static void Clear(IEnumerable<string> ownedPaths)
        {
            Visit(ownedPaths, path => { lock (Gate(path).Sync) File.Delete(path); });
        }

        private static void Visit(IEnumerable<string> paths, Action<string> action)
        {
            if (paths == null) return;
            try { foreach (string path in paths) { try { if (path != null) action(path); } catch { } } }
            catch { }
        }
    }
}
