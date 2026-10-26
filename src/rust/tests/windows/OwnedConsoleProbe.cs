// Disposable Windows test fixture. Never included in the Host or installer payload.
using System;
using System.Runtime.InteropServices;
internal static class OwnedHostWindowSignal {
    [DllImport("user32.dll", EntryPoint="FindWindowExW", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    public static IntPtr FindOwned(uint targetPid) {
        // Null title searches all titled windows of the Host diagnostic class.
        IntPtr window = IntPtr.Zero;
        while ((window = FindWindowEx(IntPtr.Zero, window, "iCem.Host.Diagnostics.v1", null)) != IntPtr.Zero) {
            uint ownerPid;
            GetWindowThreadProcessId(window, out ownerPid);
            if (ownerPid == targetPid) return window;
        }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessage(IntPtr hwnd, uint msg, UIntPtr wp, IntPtr lp);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    public static bool PostCtrlC(IntPtr window) {
        return PostMessage(window, 0x102, new UIntPtr(3u), IntPtr.Zero);
    }
}

internal static class OwnedConsoleProbe
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3) return 2;
            uint targetPid = UInt32.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
            string action = args[1];
            long expectedHandle = Int64.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            if (action != "Probe" && action != "Close" && action != "Minimize" && action != "CtrlC") return 2;
            Console.Error.WriteLine("window helper started: target {0}, action {1}; finding target window", targetPid, action);
            // Caller owns the target PID. Never attach to or alter conhost.
            IntPtr window = OwnedHostWindowSignal.FindOwned(targetPid);
            if (window == IntPtr.Zero) throw new InvalidOperationException("Owned Host diagnostic window not found for PID " + targetPid);
            if (expectedHandle != 0 && window.ToInt64() != expectedHandle) throw new InvalidOperationException("Host window was recreated");
            switch (action)
            {
                case "Close":
                    if (!OwnedHostWindowSignal.PostMessage(window, 0x10, UIntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("WM_CLOSE failed");
                    break;
                case "Minimize":
                    if (!OwnedHostWindowSignal.ShowWindowAsync(window, 6)) throw new InvalidOperationException("Minimize failed");
                    break;
                case "CtrlC":
                    if (!OwnedHostWindowSignal.PostCtrlC(window)) throw new InvalidOperationException("Host Ctrl+C input failed");
                    break;
            }
            Console.Error.WriteLine("window helper action {0} posted; checking state", action);
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            if (action == "Close" || action == "Minimize")
            {
                while ((action == "Close" && OwnedHostWindowSignal.IsWindowVisible(window)) ||
                    (action == "Minimize" && !OwnedHostWindowSignal.IsIconic(window)))
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("Host window state deadline");
                    System.Threading.Thread.Sleep(10);
                }
                if (!OwnedHostWindowSignal.IsWindow(window)) throw new InvalidOperationException("Host window destroyed by visibility action");
            }
            // Only fixed keys, integer identities and bool literals; diagnostics stay on stderr.
            Console.WriteLine("{{\"hwnd\":{0},\"pid\":{1},\"visible\":{2},\"minimized\":{3}}}",
                window.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                targetPid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OwnedHostWindowSignal.IsWindowVisible(window) ? "true" : "false",
                OwnedHostWindowSignal.IsIconic(window) ? "true" : "false");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
