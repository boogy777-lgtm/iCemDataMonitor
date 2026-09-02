// iCemDataMonitor.Sdk.Diagnostics — station-wide crash/error file log
// (owner directive 2026-08-26: every station app writes errors/crashes to
// C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs\<AppName>\ with a
// 25 MB growth cap).
//
// CANON: Burservis.iCem.DataMonitor\TapLog.cs (net48 AddIn evidence channel).
// Semantics are mirrored 1:1 where the owner directive pins them:
//   - cap MaxBytes = 25 MB; on exceed the file is rewritten IN PLACE keeping
//     only the tail KeepBytes (12.5 MB) — the newest half survives, the oldest
//     half is dropped (the tail may start mid-line: acceptable for an
//     evidence log);
//   - line format: UTC ISO-8601 "O" (invariant) + space + line + NewLine,
//     UTF-8;
//   - swallow-all: a logging failure NEVER hurts the caller (evidence channel);
//   - rotation check amortized: once per RotationCheckInterval-th write.
//
// DELIBERATE DEVIATIONS from TapLog (dictated by this task):
//   - path-parameterized API (TapLog has one compiled-in DefaultPath): the log
//     directory is per-app (AppLogDir), so the serialization gate is PER FILE,
//     not one global lock — writers of different files never contend;
//   - the parent directory is created lazily on first write (best-effort).

using System.Collections.Concurrent;

namespace iCemDataMonitor.Sdk.Diagnostics;

/// <summary>
/// Station-wide error/crash file log (owner log policy 2026-08-26, canon
/// TapLog). Append-only text log with a hard size cap: when the file exceeds
/// <see cref="MaxBytes"/> it is rewritten in place keeping only the newest
/// <c>KeepBytes</c> bytes (tail half) — newest evidence survives, oldest is
/// dropped.
///
/// Guarantees:
///  - NEVER throws: every IO fault (bad path, locked file, full disk) is
///    swallowed best-effort — logging must never hurt the host;
///  - thread-safe: concurrent writers of the SAME file are serialized by a
///    per-file gate (lines are never torn); different files proceed in
///    parallel (no global bottleneck);
///  - the parent directory is created lazily on first successful write.
///
/// BCL-only, no dependencies (Sdk zero-dependency law).
/// </summary>
public static class StationFileLog
{
    /// <summary>Station log root: every app gets a subdirectory here.</summary>
    public const string LogsRoot = @"C:\ProgramData\Halliburton\iCem\DataMonitoring\Logs";

    /// <summary>Size cap: on exceed ⇒ rewrite in place, keep only the tail half.</summary>
    public const long MaxBytes = 25L * 1024 * 1024; // 25 MB

    /// <summary>Tail kept after truncation (newest half) — canon TapLog.</summary>
    internal const long KeepBytes = 12L * 1024 * 1024 + 512L * 1024; // 12.5 MB

    /// <summary>Rotation check runs once every N writes (cheap amortization;
    /// worst-case overshoot ≈ N lines past the cap).</summary>
    internal const int RotationCheckInterval = 60;

    /// <summary>Per-file state: serialization gate + amortized write counter.</summary>
    private sealed record FileGate
    {
        public readonly object Lock = new();
        public long Writes;
    }

    private static readonly ConcurrentDictionary<string, FileGate> Gates = new();

    /// <summary>
    /// Log directory of an application: <see cref="LogsRoot"/>\&lt;appName&gt;.
    /// Pure string join — no IO, no validation (the writer creates it lazily).
    /// </summary>
    public static string AppLogDir(string appName) => Path.Combine(LogsRoot, appName);

    /// <summary>
    /// Append ONE timestamped line (UTC ISO-8601 "O" + space + <paramref name="line"/>,
    /// UTF-8, canon TapLog format). Creates the parent directory lazily. Runs the
    /// amortized rotation check once per <see cref="RotationCheckInterval"/> writes
    /// to THIS file. Never throws: any failure is swallowed.
    /// <paramref name="line"/> = null is a no-op (canon TapLog).
    /// Writers of the same file are serialized (no torn lines); different files
    /// do not contend.
    /// </summary>
    public static void Write(string path, string line)
    {
        if (path is null || line is null)
        {
            return;
        }

        try
        {
            // Normalize so both spellings of one path share a gate. If the path
            // is illegal, fall back to the raw string — the append below will
            // fail and be swallowed anyway.
            string key;
            try { key = Path.GetFullPath(path); }
            catch { key = path; }

            FileGate gate = Gates.GetOrAdd(key, _ => new FileGate());

            lock (gate.Lock)
            {
                if ((++gate.Writes % RotationCheckInterval) == 0)
                {
                    try { TruncateIfNeeded(path); }
                    catch
                    {
                        // rotation is best-effort — logging continues
                    }
                }

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir); // no-op when it exists
                }

                File.AppendAllText(
                    path,
                    DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                    + " " + line + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // swallow-all — the evidence channel must never hurt the host
        }
    }

    /// <summary>
    /// Enforce the size cap on one log file (canon TapLog semantics): when the
    /// file exceeds <see cref="MaxBytes"/>, rewrite it IN PLACE keeping only the
    /// last <c>KeepBytes</c> bytes (newest half survives; the kept block may
    /// start mid-line — acceptable for an evidence log). Under the cap ⇒ no-op.
    /// Never throws; a missing file is treated as empty (nothing to keep).
    /// Callers writing through <see cref="Write"/> are already serialized by the
    /// per-file gate; direct callers should not race <see cref="Write"/> on the
    /// same path.
    /// </summary>
    public static void TruncateIfNeeded(string path)
    {
        try
        {
            using FileStream s = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
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
        catch
        {
            // swallow-all — rotation failure never hurts the host
        }
    }
}
