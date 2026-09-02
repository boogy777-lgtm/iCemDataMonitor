using iCemDataMonitor.Core.Config;

namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// 2026-08-31 (owner decree «даже смена темы — тоже CLI: один bool для
/// одновременного использования в web и winui»): the station-wide UI-theme
/// truth behind the GetUiPrefs/SetUiTheme RPCs.
///
/// TRUTH LAW: the truth is the text file <see cref="DefaultThemeFileName"/>
/// ("light"/"dark") in the ProgramData STATION ROOT — the SAME file the WinUI
/// shell already writes/reads (Shell.WinUI MainWindow.ThemeFilePath, the
/// folder of IniConfig.ProgramDataPath). One station bool, every UI surface
/// sees the same truth: the shell and the SPA read it at start; the host
/// writes it in client mode (SetUiTheme RPC).
///
/// POKE LAW: after a successful write the store SETs the named event
/// <see cref="CrossProcessNames.UiThemeChangedEvent"/> — the same event-poke
/// pattern the shell's own writer uses. OWNER of the event is the shell
/// (it creates + waits in MainWindow.StartThemeListener); the host is only a
/// WRITER (like the shell's own toggle), so OpenExisting + Set — nobody
/// listening is fine (the next UI start adopts the file anyway).
///
/// TRUTH IS READ AT CALL TIME (sensor-at-send, AutostartManager law): both
/// RPC answers re-read the file — never a cached projection.
///
/// Test seam (§15.7(a) data seam, AutostartManager precedent): the file PATH,
/// the poke ACTION and the log sink are ctor parameters. Unit tests inject a
/// temp-dir path and a recorded poke — the REAL ProgramData ui-theme file is
/// NEVER touched by tests (the live owner's shell listens on the real event).
/// </summary>
public sealed class UiPrefsStore
{
    /// <summary>The station-wide theme file name — MUST match the WinUI
    /// shell's MainWindow.ThemeFilePath literal ("ui-theme").</summary>
    public const string DefaultThemeFileName = "ui-theme";

    private readonly string _filePath;
    private readonly Action? _poke;
    private readonly Action<string>? _log;

    /// <param name="filePath">Absolute path of the ui-theme file
    /// (tests inject a temp path; production = the ProgramData station root).</param>
    /// <param name="poke">Post-write poke action (tests record; production
    /// = <see cref="PokeShellThemeEvent"/>).</param>
    /// <param name="log">Optional startup-log sink (host log-line style).</param>
    public UiPrefsStore(string? filePath = null, Action? poke = null, Action<string>? log = null)
    {
        _filePath = filePath ?? DefaultFilePath;
        _poke = poke ?? PokeShellThemeEvent;
        _log = log;
    }

    /// <summary>Production truth path: ui-theme in the folder of the canonical
    /// config.ini (the ProgramData station root) — byte-identical derivation
    /// to Shell.WinUI MainWindow.ThemeFilePath.</summary>
    public static string DefaultFilePath =>
        Path.Combine(Path.GetDirectoryName(IniConfig.ProgramDataPath)!, DefaultThemeFileName);

    /// <summary>The poke event the store SETs after writing (mirrored literal
    /// of Shell.WinUI MainWindow.UiThemeChangedEventName; OWNER: the shell).</summary>
    public string FilePath => _filePath;

    /// <summary>TRUTH probe at call time: file text trimmed == "light".
    /// Missing file / unreadable file / any other text ⇒ dark (false) — the
    /// station default, same semantics as the shell's LoadThemeIsLight.</summary>
    public bool IsThemeLight()
    {
        try
        {
            return File.ReadAllText(_filePath).Trim() == "light";
        }
        catch (FileNotFoundException) { return false; } // absent = default dark
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return false; }           // unreadable = honest default, ¬exception into the RPC
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>SetUiTheme body: write the bool (best-effort — a failed write
    /// is logged and the caller still answers the CURRENT truth, never a fake
    /// success), then poke the shell-owned event so a live WinUI flips
    /// instantly. Idempotent: repeated identical writes are the same state.</summary>
    public void SetThemeLight(bool light)
    {
        try
        {
            File.WriteAllText(_filePath, light ? "light" : "dark");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"ui-theme write FAILED ({_filePath}): {ex.Message} — answering current truth");
        }

        // 2026-08-31: poke AFTER the bool is durable — listeners re-read the
        // file (the shell's SaveTheme ordering law, mirrored).
        try { _poke?.Invoke(); }
        catch (Exception ex)
        {
            _log?.Invoke($"ui-theme poke FAILED: {ex.Message} (file state is already durable)");
        }
    }

    /// <summary>Production poke: SET the shell-owned theme event. A missing
    /// event means the shell is not running — fine (the next shell start
    /// adopts the file; the SPA reads it at page load).</summary>
    private static void PokeShellThemeEvent()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(CrossProcessNames.UiThemeChangedEvent);
            ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { /* nobody listening — ok */ }
        catch (UnauthorizedAccessException) { /* open-for-write denied — cosmetic */ }
    }
}
