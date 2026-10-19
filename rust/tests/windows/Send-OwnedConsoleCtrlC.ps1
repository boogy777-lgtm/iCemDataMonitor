param(
    [Parameter(Mandatory=$true)][uint32]$TargetProcessId,
    [ValidateSet('CtrlC','Close','Minimize','Probe')][string]$Action = 'CtrlC',
    [long]$ExpectedWindowHandle = 0
)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class OwnedHostWindowSignal {
    [DllImport("user32.dll", EntryPoint="FindWindowExW", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    public static IntPtr FindOwned(uint targetPid) {
        // Keep the null title inside C#: PowerShell coerces a null string argument
        // to an empty title, which excludes the titled Host diagnostic window.
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
    // Windows PowerShell 5 cannot cast Int32 directly to UIntPtr.
    public static bool PostCtrlC(IntPtr window) {
        return PostMessage(window, 0x102, new UIntPtr(3u), IntPtr.Zero);
    }
}
'@
# Test fixture only. Caller owns the child PID. Never attach to or alter conhost.
$window = [OwnedHostWindowSignal]::FindOwned($TargetProcessId)
if ($window -eq [IntPtr]::Zero) { throw "Owned Host diagnostic window not found for PID $TargetProcessId" }
if ($ExpectedWindowHandle -ne 0 -and $window.ToInt64() -ne $ExpectedWindowHandle) { throw 'Host window was recreated' }
switch ($Action) {
    'Close' { if (![OwnedHostWindowSignal]::PostMessage($window, 0x10, [UIntPtr]::Zero, [IntPtr]::Zero)) { throw 'WM_CLOSE failed' } }
    'Minimize' { if (![OwnedHostWindowSignal]::ShowWindowAsync($window, 6)) { throw 'Minimize failed' } }
    'CtrlC' { if (![OwnedHostWindowSignal]::PostCtrlC($window)) { throw 'Host Ctrl+C input failed' } }
}
$deadline = [DateTime]::UtcNow.AddSeconds(2)
if ($Action -in @('Close','Minimize')) {
    while (($Action -eq 'Close' -and [OwnedHostWindowSignal]::IsWindowVisible($window)) -or ($Action -eq 'Minimize' -and ![OwnedHostWindowSignal]::IsIconic($window))) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Host window state deadline' }
        Start-Sleep -Milliseconds 10
    }
    if (![OwnedHostWindowSignal]::IsWindow($window)) { throw 'Host window destroyed by visibility action' }
}
@{ hwnd=$window.ToInt64(); pid=$TargetProcessId; visible=[OwnedHostWindowSignal]::IsWindowVisible($window); minimized=[OwnedHostWindowSignal]::IsIconic($window) } | ConvertTo-Json -Compress
