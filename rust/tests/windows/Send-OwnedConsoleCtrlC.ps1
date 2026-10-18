param([Parameter(Mandatory=$true)][uint32]$TargetProcessId)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class OwnedConsoleSignal {
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
}
'@
# Test fixture only. The caller supplies its OWN child PID; never attach to parent.
[void][OwnedConsoleSignal]::FreeConsole()
try {
    if (![OwnedConsoleSignal]::AttachConsole($TargetProcessId)) { throw 'AttachConsole failed' }
    if (![OwnedConsoleSignal]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)) { throw 'ignore helper Ctrl+C failed' }
    if (![OwnedConsoleSignal]::GenerateConsoleCtrlEvent(0, 0)) { throw 'GenerateConsoleCtrlEvent failed' }
    Start-Sleep -Milliseconds 200
} finally {
    [void][OwnedConsoleSignal]::FreeConsole()
}
