namespace iCemDataMonitor.Core.Config;

/// <summary>Station theme text, shared by Host and Shell. Cross-process writes
/// are last-commit-wins; no cross-process read/modify/write transaction is implied.</summary>
public sealed class UiThemePreferenceStore
{
    public const string FileName = "ui-theme";
    public const string ChangedEventName = @"Local\iCemDataMonitorTray_v1_UiTheme";
    public static string DefaultFilePath => Path.Combine(Path.GetDirectoryName(IniConfig.ProgramDataPath)!, FileName);
    private readonly object _mutation = new();
    private readonly Action? _notify;
    private readonly Action<string>? _log;
    public string FilePath { get; }

    public UiThemePreferenceStore(string? filePath = null, Action? notify = null, Action<string>? log = null)
    {
        FilePath = filePath ?? DefaultFilePath;
        _notify = notify;
        _log = log;
    }

    public bool Read()
    {
        try { return File.ReadAllText(FilePath).Trim() == "light"; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public readonly record struct WriteResult(bool Written, bool CurrentLight, Exception? Error);

    public WriteResult Write(bool light) => Commit(light, overwrite: true);

    private WriteResult Commit(bool light, bool overwrite)
    {
        WriteResult result;
        lock (_mutation)
        {
            string? temp = null;
            try
            {
                // Adjacent unique file: a failed preparation/rename cannot truncate
                // the previous truth. Parent creation belongs to station setup.
                temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(light ? "light" : "dark");
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, FilePath, overwrite);
                result = new(true, Read(), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                result = new(false, Read(), ex);
            }
            finally
            {
                if (temp is not null)
                    try { File.Delete(temp); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
        }
        if (!result.Written)
            _log?.Invoke($"ui-theme write FAILED ({FilePath}): {result.Error?.Message} — answering current truth");
        else
        {
            // External callbacks must never execute under the mutation lock.
            try { _notify?.Invoke(); }
            catch (Exception ex) { _log?.Invoke($"ui-theme poke FAILED: {ex.Message} (file committed)"); }
        }
        return result;
    }

    /// <summary>Shell-only compatibility: copy a missing station value, preserve
    /// the source, and apply actual station/default truth if adoption fails.</summary>
    public bool ReadWithLegacyAdoption(string legacyPath)
    {
        if (!File.Exists(FilePath) && File.Exists(legacyPath))
        {
            try { Commit(File.ReadAllText(legacyPath).Trim() == "light", overwrite: false); }
            catch (IOException ex) { _log?.Invoke($"ui-theme legacy read FAILED: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { _log?.Invoke($"ui-theme legacy read FAILED: {ex.Message}"); }
        }
        return Read();
    }
}
