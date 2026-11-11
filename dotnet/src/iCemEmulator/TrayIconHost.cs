// TrayIconHost — W15 (owner request 2026-08-28: «крестик у консоли —
// закрывает полностью, вместо скрытия — чтобы в трее остался жить процесс»):
// the merged app's tray presence. The window's X (and Alt+F4) now HIDE to
// the tray; the process — and the in-proc engine — keep living (streaming,
// CLI `--cli status` etc. keep working). The REAL exit paths are the tray
// menu «Выход» and the CLI `quit` (both funnel into App.RequestAppExit).
//
// Self-contained Win32 (no NuGet, no WinForms): a hidden TOP-LEVEL window on
// the UI thread (NOT HWND_MESSAGE — it must receive the TaskbarCreated
// broadcast to re-add the icon after an explorer.exe restart), classic v0
// Shell_NotifyIcon callback semantics (lParam = mouse message), popup menu
// via TPM_RETURNCMD. Created on the UI thread ⇒ the app's own message pump
// dispatches the callback messages; OpenRequested/ExitRequested therefore
// fire on the UI thread and may touch the Window directly. Every Win32 call
// is guarded: a tray failure must never take the emulator down (the window
// X then simply closes — the pre-W15 behavior).

using System.ComponentModel;
using System.Runtime.InteropServices;
using Halliburton.iCemEmule.Core;

namespace Halliburton.iCemEmulator;

internal sealed class TrayIconHost : IDisposable
{
    private const uint WM_APP_TRAY = 0x8000 + 1;          // WM_APP + 1 callback
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const uint WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    private const uint WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_NULL = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002, TPM_NONOTIFY = 0x0080;
    private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    private const int IDC_APPLICATION = 32512;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10, LR_DEFAULTSIZE = 0x40;

    private const int MenuOpen = 1;
    private const int MenuExit = 2;

    /// <summary>Fires on the UI thread: the operator asked to open the window
    /// (tray double-click or «Открыть»).</summary>
    public event Action? OpenRequested;

    /// <summary>Fires on the UI thread: the operator chose «Выход» — the REAL
    /// app exit (hide-to-tray is NOT an exit; the engine keeps streaming).</summary>
    public event Action? ExitRequested;

    private readonly WndProcDelegate _proc;   // GC root: the native class holds this pointer
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private string _tip;   // W16: live — App refreshes it with COM/LAN stream states
    private readonly uint _taskbarCreatedMsg;
    private bool _added;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName,
        string? lpWindowName, uint dwStyle, int x, int y, int w, int h,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // W16 fix (owner: «иконки в трее я не увидел»): CharSet.Unicode is
    // MANDATORY here — the default ANSI marshaling feeds an ANSI byte string
    // into the W entry point (mangled path ⇒ load fails ⇒ silent fallback to
    // IDC_APPLICATION = the «дефолтная заглушка» the owner saw). Verified
    // live: LoadImageW with CharSet.Unicode + the same .ico returns a valid
    // handle; without it — IntPtr.Zero.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInstance, string lpName, uint type,
        int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint flags, IntPtr idNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y,
        IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    public TrayIconHost(string tip = "iCemEmulator — эмулятор стенда (окно скрыто: иконка в трее)")
    {
        _tip = tip;
        _proc = WndProc;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        var hInstance = GetModuleHandleW(null);
        var className = "iCemEmulator.TrayHost." + Guid.NewGuid().ToString("N");
        var cls = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInstance,
            lpszClassName = className,
        };
        // A unique class name per instance ⇒ no "class already exists" and no
        // unregister-order hazards; the atom leaks with the process — fine.
        var atom = RegisterClassExW(ref cls);

        // Hidden top-level window: never shown, exists for the tray callback
        // + the TaskbarCreated broadcast (message-only windows miss broadcasts).
        _hwnd = CreateWindowExW(0, className, "iCemEmulator tray host", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        // W16 diagnostics (owner: «иконки в трее я не увидел»): every failure
        // point is evidence-logged — silent catch-all hid the cause.
        if (_hwnd == IntPtr.Zero)
            CrashLog.Log($"TrayIconHost: CreateWindowExW failed, err={Marshal.GetLastWin32Error()} (atom={atom})");

        var iconPath = Path.Combine(AppContext.BaseDirectory, "iCemEmule.ico");
        // W16 2026-08-29 (owner: «добавь иконку у трея — сейчас дефолтная
        // заглушка»): LoadImageW with LR_DEFAULTSIZE asks Windows to stretch
        // the .ico to the tray's system metric — on 96-DPI stations the stretch
        // of a multi-size .ico produces the blurry/degraded look that reads as
        // «заглушка». Load at the NATIVE 32x32 size (LR_DEFAULTSIZE off) — the
        // shell scales it to the tray cell crisply.
        _icon = File.Exists(iconPath)
            ? LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE)
            : IntPtr.Zero;
        if (_icon == IntPtr.Zero)
        {
            CrashLog.Log($"TrayIconHost: ico load failed, path='{iconPath}' exists={File.Exists(iconPath)} err={Marshal.GetLastWin32Error()}");
            _icon = LoadIconW(IntPtr.Zero, (IntPtr)IDC_APPLICATION);
        }

        AddIcon();
        CrashLog.Log($"TrayIconHost: hwnd={_hwnd != IntPtr.Zero} icon={_icon != IntPtr.Zero} added={_added}");
    }

    private NOTIFYICONDATAW BuildData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = _tip,
        };
    }

    private void AddIcon()
    {
        try
        {
            var data = BuildData();
            _added = Shell_NotifyIconW(NIM_ADD, ref data);
        }
        catch
        {
            _added = false;   // tray unavailable — the window keeps working
        }
    }

    /// <summary>W16 (owner: «оставь подсказки — COM on/off и LAN on/off»):
    /// update the tray tooltip in place (NIM_MODIFY). Called on the UI thread
    /// by App's 1 s refresh. Never throws.</summary>
    public void UpdateTip(string tip)
    {
        if (_disposed || !_added || tip == _tip) return;
        try
        {
            _tip = tip;
            var data = BuildData();
            data.uFlags = NIF_TIP;
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch { /* tray cosmetics must never fault the app */ }
    }

    private void RemoveIcon()
    {
        if (!_added) return;
        try
        {
            var data = BuildData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
        }
        catch { /* best-effort */ }
        _added = false;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            switch ((uint)lParam.ToInt64())
            {
                case WM_LBUTTONDBLCLK:
                    SafeInvoke(OpenRequested);
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    ShowMenu();
                    return IntPtr.Zero;
            }
        }
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            // explorer restarted: the old icon died with its taskbar — re-add.
            RemoveIcon();
            AddIcon();
            return IntPtr.Zero;
        }
        else if (msg == WM_CLOSE)
        {
            DestroyWindow(hWnd);
            return IntPtr.Zero;
        }
        else if (msg == WM_DESTROY)
        {
            RemoveIcon();
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, (IntPtr)MenuOpen, "Открыть iCemEmulator");
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (IntPtr)MenuExit, "Выход");
            SetForegroundWindow(_hwnd);   // classic: lets the menu dismiss on outside click
            GetCursorPos(out var pt);
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd == MenuOpen) SafeInvoke(OpenRequested);
            else if (cmd == MenuExit) SafeInvoke(ExitRequested);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>Invoked from inside the native WndProc — an exception here
    /// would cross the native boundary. Swallow + log to CrashLog instead.</summary>
    private static void SafeInvoke(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { CrashLog.Log($"TrayIconHost handler: {ex}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        // Same thread created the window (UI thread) ⇒ DestroyWindow directly;
        // WM_DESTROY re-runs RemoveIcon — idempotent.
        if (_hwnd != IntPtr.Zero) { try { DestroyWindow(_hwnd); } catch { } }
        if (_icon != IntPtr.Zero)
        {
            try { DestroyIcon(_icon); } catch { }
        }
    }
}
