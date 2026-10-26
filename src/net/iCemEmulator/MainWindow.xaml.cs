using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Halliburton.iCemEmulator;

public sealed partial class MainWindow : Window
{
    // W14: скругления углов — Win11-only (на Win10 19045 attr 33 = E_INVALIDARG,
    // autopsy 2026-08-20: per-window rounding на Win10 невозможен)
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(
        System.IntPtr hwnd, int attr, ref int value, int size);

    // W15 (owner request 2026-08-28): false ⇒ Closing HIDES to the tray (the
    // process and the in-proc engine keep living); true ⇒ the window really
    // closes. Set ONLY by App.RequestAppExit (tray «Выход» / pipe `quit`).
    private bool _realClose;

    public MainWindow()
    {
        InitializeComponent();

        // Merged app (2026-08-27): the engine runs IN-PROCESS — this window is
        // the WHOLE app (no --embed/--parent-pid child modes anymore; the
        // shell no longer embeds the emulator UI).
        Title = "iCemEmulator — эмулятор стенда";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(860, 720));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "iCemEmule.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // W15: the X button (and Alt+F4) must NOT kill the process — the
        // owner's law «чтобы в трее остался жить процесс». Cancel the close
        // and hide; the engine keeps streaming, the CLI keeps working. A real
        // close happens only through App.RequestAppExit.
        AppWindow.Closing += (_, e) =>
        {
            if (_realClose) return;
            e.Cancel = true;
            HideToTray();
        };

        // W15 diagnostic: window-level pointer probe — sees clicks the page
        // subtree may never receive (title-bar strip, root overlays).
        RootGrid.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(RootGrid).Position;
            iCemDataMonitor.Emulator.WinUI.UiTrace.Log($"RootGrid.PointerPressed {pt.X:0},{pt.Y:0}");
            try
            {
                var hits = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(pt, RootGrid);
                var top = string.Join(" <- ", hits.Take(10)
                    .Select(h => h is Microsoft.UI.Xaml.FrameworkElement fe
                        ? $"{h.GetType().Name}'{fe.Name}'" : h.GetType().Name));
                iCemDataMonitor.Emulator.WinUI.UiTrace.Log("root hit-test: " + top);
            }
            catch (Exception ex)
            {
                iCemDataMonitor.Emulator.WinUI.UiTrace.Log("root hit-test failed: " + ex.Message);
            }
        };

        // W14: КАСТОМНЫЙ title bar. Системный капшн на Win10 (19045) игнорирует
        // AppWindow.TitleBar.*Color (DWMWA_CAPTION_COLOR = Win11), immersive
        // dark рисует системный ЧЁРНЫЙ и фреймворк гоняется за его флагом
        // (autopsy: reset ПОСЛЕ Activated). Решение без гонок и хардкода:
        // ExtendsContentIntoTitleBar + своя XAML-полоса на токенах темы;
        // кнопки min/max/close рисует фреймворк цветами из ApplyTitleBarTheme.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        RootGrid.Loaded += (_, _) =>
        {
            ApplyTitleBarTheme();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };
    }

    /// <summary>W15: hide the window without ending the process — the tray
    /// icon (App) remains the handle to bring it back («Открыть»/double-click)
    /// or to exit («Выход»).</summary>
    internal void HideToTray()
    {
        try { AppWindow.Hide(); } catch { /* never take the app down for a hide */ }
    }

    /// <summary>W15: bring the window back from the tray (tray menu/double-click).</summary>
    internal void ShowFromTray()
    {
        try
        {
            AppWindow.Show();
            Activate();
        }
        catch { /* best-effort re-show */ }
    }

    /// <summary>W15: authorize the NEXT close (App.RequestAppExit only) — the
    /// Closing handler lets it through instead of hiding to the tray.</summary>
    internal void AllowRealClose() => _realClose = true;

    /// <summary>W14: цвета кнопок капшна из токенов темы (MacWindow/MacLabel/
    /// MacSurface2 — один источник с ThemeResources.xaml). Фон полосы — XAML
    /// ({ThemeResource MacWindowBrush}), поэтому гонки «текст vs фон» нет.
    /// Токены читаем из УЖЕ загруженного словаря Application.Resources
    /// (Source'd ResourceDictionary парсится лениво — ThemeDictionaries пусты
    /// до встраивания, autopsy 2026-08-20).</summary>
    private void ApplyTitleBarTheme()
    {
        try
        {
            var themeKey = RootGrid.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            ResourceDictionary? themed = null;
            foreach (var md in Application.Current.Resources.MergedDictionaries)
            {
                if (md.ThemeDictionaries.TryGetValue(themeKey, out var t) &&
                    t is ResourceDictionary td && td.ContainsKey("MacWindowColor"))
                {
                    themed = td;
                    break;
                }
            }
            if (themed is null) return;
            if (themed.TryGetValue("MacWindowColor", out var w) && w is Windows.UI.Color win &&
                themed.TryGetValue("MacLabelColor", out var l) && l is Windows.UI.Color label)
            {
                Windows.UI.Color hover = win;
                if (themed.TryGetValue("MacSurface2Color", out var s) && s is Windows.UI.Color s2)
                    hover = s2;
                var tb = AppWindow.TitleBar;
                tb.BackgroundColor = win;
                tb.InactiveBackgroundColor = win;
                tb.ForegroundColor = label;
                tb.InactiveForegroundColor = label;
                tb.ButtonBackgroundColor = win;
                tb.ButtonInactiveBackgroundColor = win;
                tb.ButtonForegroundColor = label;
                tb.ButtonInactiveForegroundColor = label;
                tb.ButtonHoverBackgroundColor = hover;
                tb.ButtonHoverForegroundColor = label;
                tb.ButtonPressedBackgroundColor = hover;
                tb.ButtonPressedForegroundColor = label;
            }
        }
        catch
        {
            // theme fallback: ОС красит кнопки капшна по умолчанию
        }
    }
}
