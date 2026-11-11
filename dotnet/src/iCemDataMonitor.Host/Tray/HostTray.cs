using System.Diagnostics;
using System.Drawing;
using H.NotifyIcon.Core;

namespace iCemDataMonitor.Host.Tray;

/// <summary>
/// E.2.2 (W19): the ONLY visual surface added by the host — a tray icon with
/// a control menu. E.2.0 principle: NO new UI (no windows, no dashboard, no
/// WebView) — all rich visuals live in the existing WinUI shell, spawned by
/// [Открыть панель]. The icon reuses the EXISTING app asset (embedded
/// iCemDataMonitor.ico), no new artwork.
///
/// 2026-09-01 (owner decree «Блок 2 п.4: миграция на winui-трей или
/// H.NotifyIcon»): the WinForms NotifyIcon + ContextMenuStrip implementation
/// is REPLACED by H.NotifyIcon.Core (TrayIconWithContextMenu + PopupMenu —
/// pure Win32 Shell_NotifyIcon / TrackPopupMenuEx, declared trimming and
/// NativeAOT compatible). WinForms is not trim-compatible and blocked the
/// −25-30 MB trimming lever on the self-contained single-file host.
///
/// Message-loop doctrine (console application): TrayIconWithContextMenu
/// spawns its OWN message-loop thread inside Create() (a foreground thread —
/// Dispose MUST run, and it does: ghost-icon lesson puts the dispose FIRST
/// in the shutdown path). The main thread keeps running Kestrel. The loop
/// ends via PostThreadMessage(WM_QUIT) on Dispose.
///
/// Dynamic-captions doctrine: H.NotifyIcon's PopupMenu is REBUILT from the
/// managed item objects at every Show() — PopupMenuItem.Text is read at
/// render time, so a caption assigned after a state change is already the
/// honest one at the next menu-open (the P3 2026-08-27 «dead toggle» fix
/// carries over for free: ContextMenuOpening + post-click updates both
/// re-derive the caption from the ACTUAL ConsoleWindow.Visible).
///
/// Shutdown order (Windows 11 ghost-icon lesson): on StopApplication the
/// icon is removed FIRST (Dispose → TryRemove → WM_QUIT → thread join), only
/// then does the host drain its layers.
///
/// Menu is ADDITIVE by construction (owner decree): items are appended to a
/// PopupMenu list — new commands never disturb existing ones.
/// 2026-08-27 RESTORED (corrected decree «Web - удали (контрол sim)» = remove
/// the sim control from the page, not the page): «Открыть Web» is BACK between
/// [Открыть панель] and the console toggle (precedent W21.2). «Статус» stays
/// REMOVED. Final menu: Открыть панель · Открыть Web · Показать/Скрыть консоль ·
/// Перезапуск · Закрыть.
/// </summary>
public sealed class HostTray : IDisposable
{
    public const string OpenPanelItemText = "Открыть панель";
    public const string OpenWebItemText = "Открыть Web";
    public const string ShowConsoleItemText = "Показать консоль";
    public const string HideConsoleItemText = "Скрыть консоль";
    public const string RestartItemText = "Перезапуск";
    public const string CloseItemText = "Закрыть";

    /// <summary>
    /// 2026-09-01 tray-GUID law (live autopsy, same day): a FIXED tray Guid
    /// (name- or path-derived) is a poison-on-crash trap — proven on this
    /// machine with raw Shell_NotifyIcon: a process that dies between NIM_ADD
    /// and NIM_DELETE leaves the Guid bound to a DEAD hwnd; every later
    /// NIM_ADD/NIM_DELETE for that Guid returns E_FAIL (0x80004005) until
    /// Explorer garbage-collects it. The H.NotifyIcon TryDelete-before-create
    /// dance cannot heal it (deletion requires the original hwnd). The old
    /// WinForms tray never hit this (no NIF_GUID at all). So the tray takes a
    /// PER-PROCESS Guid (path + pid): a crash can never block the next
    /// launch. Trade-off accepted by design: tray pin-settings don't persist
    /// across restarts — the WinForms predecessor persisted nothing either,
    /// and the host is a long-lived station process, not a pinned desktop app.
    /// The graceful paths (tray [Закрыть]/[Перезапуск], CLI stop) all dispose
    /// → TryRemove with a LIVE hwnd → clean removal; only a hard kill leaves
    /// a ghost until Explorer prunes dead-hwnd icons.
    /// </summary>
    private static Guid TrayIdentityForProcess() =>
        TrayIcon.CreateUniqueGuidForProcessPath(Environment.ProcessId.ToString());

    private readonly Func<string> _tooltipProvider;
    private readonly Action _openPanel;
    private readonly Action _openWeb;
    private readonly Action _showConsole;
    private readonly Action _hideConsole;
    private readonly Action _restart;
    private readonly Action _close;
    private readonly Action<string> _log;

    private TrayIconWithContextMenu? _tray;
    private Icon? _icon; // the HICON owner for the tray's lifetime
    private Timer? _tooltipTimer;
    private volatile bool _started;

    public HostTray(
        Func<string> tooltipProvider,
        Action openPanel,
        Action openWeb,
        Action showConsole,
        Action hideConsole,
        Action restart,
        Action close,
        Action<string>? log = null)
    {
        _tooltipProvider = tooltipProvider;
        _openPanel = openPanel;
        _openWeb = openWeb;
        _showConsole = showConsole;
        _hideConsole = hideConsole;
        _restart = restart;
        _close = close;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Create the tray icon and WAIT until it is actually created (fail-loud:
    /// a host that promised a tray must not silently run without). H.NotifyIcon
    /// spins its message-loop thread inside Create(); a Win32 failure on that
    /// thread never surfaces as a return value, so we poll IsCreated bounded.
    /// </summary>
    public void Start()
    {
        if (_started)
            throw new InvalidOperationException("HostTray already started");

        _icon = LoadHostIcon();
        _tray = new TrayIconWithContextMenu(TrayIdentityForProcess())
        {
            Icon = _icon.Handle,
            ToolTip = TrayTooltipFormatter.Clamp(_tooltipProvider()),
            ContextMenu = BuildMenu(out var consoleItem),
        };

        // P3 2026-08-27 (owner complaint «Показать консоль не работает»): the
        // caption must be fresh at RENDER time too — anything outside the tray
        // (RPC SetConsoleVisible, console window minimized/externally hidden)
        // changes the real state between clicks. H.NotifyIcon 2.4.1 detail:
        // PopupMenu.Show() builds the NATIVE menu from the managed Text values
        // on IconRightMouseUp, so the honest-caption refresh must run BEFORE
        // that — IconRightMouseDown is the last event guaranteed to precede
        // the menu build (WM_INITMENUPOPUP arrives too late: inside Track).
        _tray.MessageWindow.MouseEventReceived += (_, args) =>
        {
            if (args.MouseEvent == MouseEvent.IconRightMouseDown)
                consoleItem.Text = ConsoleCaption(ConsoleWindow.Visible);
        };

        _tray.Create();

        var deadline = Environment.TickCount64 + 5000;
        while (!_tray.IsCreated)
        {
            if (Environment.TickCount64 >= deadline)
                throw new InvalidOperationException("host tray icon failed to appear within 5 s");
            Thread.Sleep(10);
        }

        // 1 Hz dynamic tooltip (E.2.3) — UpdateToolTip is a bare NIM_MODIFY,
        // safe from a threadpool thread.
        _tooltipTimer = new Timer(
            _ =>
            {
                try { _tray?.UpdateToolTip(TrayTooltipFormatter.Clamp(_tooltipProvider())); }
                catch (Exception ex) { _log($"tooltip update failed: {ex.Message}"); }
            },
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        _started = true;
    }

    private PopupMenu BuildMenu(out PopupMenuItem consoleItem)
    {
        // Null-forgiving seed + reassign: the click lambda reads the item
        // itself (caption refresh post-action) and C# definite-assignment
        // analysis rejects a captured read of a not-yet-assigned local.
        PopupMenuItem toggle = null!;
        toggle = new PopupMenuItem(ConsoleCaption(ConsoleWindow.Visible), (_, _) =>
        {
            // Toggle + flip own caption (E.2.2 contract). P3 2026-08-27: the
            // caption is re-derived from the POST-ACTION state, not from the
            // intent — a failed demand-attach keeps the honest «Показать»
            // label. With H.NotifyIcon the menu is rebuilt per Show(), so the
            // assigned Text is the next render's caption.
            if (ConsoleWindow.Visible)
            {
                Guarded(_hideConsole, HideConsoleItemText);
            }
            else
            {
                Guarded(_showConsole, ShowConsoleItemText);
            }

            toggle.Text = ConsoleCaption(ConsoleWindow.Visible);
        });
        consoleItem = toggle;

        var menu = new PopupMenu();
        menu.Items.Add(new PopupMenuItem(OpenPanelItemText, (_, _) => Guarded(_openPanel, OpenPanelItemText)));
        menu.Items.Add(new PopupMenuItem(OpenWebItemText, (_, _) => Guarded(_openWeb, OpenWebItemText)));
        menu.Items.Add(toggle);
        menu.Items.Add(new PopupMenuSeparator());
        menu.Items.Add(new PopupMenuItem(RestartItemText, (_, _) => Guarded(_restart, RestartItemText)));
        menu.Items.Add(new PopupMenuItem(CloseItemText, (_, _) => Guarded(_close, CloseItemText)));
        return menu;
    }

    /// <summary>
    /// Pure caption table (unit-tested): the item always shows the command for
    /// the state the operator is NOT in — a visible console reads «Скрыть
    /// консоль», a hidden/absent one reads «Показать консоль». Derived from
    /// the ACTUAL post-action state at every click (P3 2026-08-27), so a
    /// failed demand-attach keeps the honest «Показать» label.
    /// </summary>
    public static string ConsoleCaption(bool consoleVisible) =>
        consoleVisible ? HideConsoleItemText : ShowConsoleItemText;

    /// <summary>A menu handler must NEVER kill the tray pump thread — log and carry on.</summary>
    private void Guarded(Action action, string name)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log($"tray command '{name}' FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// E.2.0 asset-reuse law: the embedded EXISTING application icon
    /// (assets\iCemDataMonitor.ico). A missing resource is a build/packaging
    /// defect — fail loud, no silent fallback artwork.
    /// </summary>
    internal static Icon LoadHostIcon()
    {
        const string resourceName = "iCemDataMonitor.Host.tray.ico";
        using var stream = typeof(HostTray).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"embedded tray icon resource '{resourceName}' missing — packaging defect (E.2.0: reuse of the existing app asset)");
        return new Icon(stream);
    }

    /// <summary>
    /// Remove the icon from ANY thread (production call sites: Program finally
    /// on StopApplication, HostLifecycle.RequestRestart pre-exit chore). Ghost-
    /// icon lesson: this runs FIRST in every shutdown path. H.NotifyIcon's
    /// Dispose removes the icon (TryRemove), posts WM_QUIT to the pump thread
    /// and joins it; called from the pump thread itself it skips the join by
    /// design.
    /// </summary>
    public void Dispose()
    {
        if (!_started)
            return;
        _started = false;
        try { _tooltipTimer?.Dispose(); } catch { /* best effort */ }
        try { _tray?.Dispose(); } catch { /* icon removal is best-effort on the drain path */ }
        try { _icon?.Dispose(); } catch { /* best effort */ }
        _tray = null;
        _icon = null;
    }
}
