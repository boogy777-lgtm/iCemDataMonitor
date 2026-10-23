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
    public const string DefaultThemeFileName = UiThemePreferenceStore.FileName;

    private readonly UiThemePreferenceStore _store;

    public UiPrefsStore(string? filePath = null, Action? poke = null, Action<string>? log = null)
    {
        _store = new UiThemePreferenceStore(filePath, poke ?? PokeShellThemeEvent, log);
    }

    public static string DefaultFilePath => UiThemePreferenceStore.DefaultFilePath;
    public string FilePath => _store.FilePath;
    public bool IsThemeLight() => _store.Read();
    public void SetThemeLight(bool light) => _store.Write(light);

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
