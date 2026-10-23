// Compiled by the Windows test with exact production sources, C# 7.3 / net48.
using System;
using System.IO;
using System.Text;
using Burservis.iCem.DataMonitor.Bus;
using iCemDataMonitor.Sdk.Diagnostics;

internal static class TapLogGolden
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static int Main(string[] args)
    {
        string directory = args[0];
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Я owned.log");
        TapLog.PathOverride = path;
        TapLog.Log("é Я");
        string line = File.ReadAllText(path).TrimEnd();
        Check(line.Length > 29 && line.Substring(28) == " é Я", "UTC O single timestamp / UTF8 payload");
        Check(DateTime.Parse(line.Substring(0, 28), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime().Date == DateTime.UtcNow.Date, "UTC timestamp");
        string missing = Path.Combine(directory, "missing", "tap.log");
        TapLog.PathOverride = missing;
        TapLog.Log("must swallow");
        Check(!Directory.Exists(Path.GetDirectoryName(missing)), "override must not create directory");
        TapLog.PathOverride = path;
        string foreign = Path.Combine(directory, "foreign.log");
        File.WriteAllText(foreign, "foreign");
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        TapLog.DeleteOldLogs(path);
        Check(!File.Exists(path) && File.Exists(foreign), "explicit ownership retention");
        using (var stream = File.Create(path))
        {
            stream.SetLength(TapLog.MaxBytes);
        }
        TapLog.TruncateIfNeeded(path);
        Check(new FileInfo(path).Length == TapLog.MaxBytes, "exact cap untouched");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(TapLog.MaxBytes + 1);
            stream.Position = stream.Length - TapLog.KeepBytes;
            stream.WriteByte(0xAB);
            stream.Position = stream.Length - 1;
            stream.WriteByte(0xCD);
        }
        TapLog.TruncateIfNeeded(path);
        byte[] tail = File.ReadAllBytes(path);
        Check(tail.Length == TapLog.KeepBytes && tail[0] == 0xAB && tail[tail.Length - 1] == 0xCD, "exact retained tail");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            OwnedFileLog.Clear(new[] { path, "\0bad", foreign });
            Check(File.Exists(path) && !File.Exists(foreign), "locked clear continues to other owned path");
        }
        OwnedFileLog.Clear(new[] { path });
        TapLog.Log("after clear");
        Check(File.ReadAllLines(path).Length == 1, "append after clear whole line");
        // Fresh path owns a fresh cadence counter, independent of prior vectors.
        path = Path.Combine(directory, "cadence.log");
        TapLog.PathOverride = path;
        using (var stream = File.Create(path)) stream.SetLength(TapLog.MaxBytes + 1);
        for (int i = 0; i < 59; i++) TapLog.Log("é");
        Check(new FileInfo(path).Length > TapLog.MaxBytes, "no early rotation");
        TapLog.Log("é");
        Check(new FileInfo(path).Length > TapLog.KeepBytes && new FileInfo(path).Length < TapLog.KeepBytes + 100, "60th append rotation");
        Console.WriteLine("net48 TapLog golden vectors passed");
        return 0;
    }
}
