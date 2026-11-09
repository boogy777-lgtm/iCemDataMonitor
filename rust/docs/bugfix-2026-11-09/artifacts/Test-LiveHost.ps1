#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Package)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Package = (Resolve-Path -LiteralPath $Package).Path
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class IcemOwnedWindowProbe {
    delegate bool EnumWindow(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", SetLastError=true)] static extern bool PostMessageW(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    public static IntPtr[] Visible(uint pid) {
        var found = new List<IntPtr>();
        EnumWindows((handle, parameter) => {
            uint owner; GetWindowThreadProcessId(handle, out owner);
            if (owner == pid && IsWindowVisible(handle)) found.Add(handle);
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }
    public static void Close(uint pid, IntPtr handle) {
        uint owner; GetWindowThreadProcessId(handle, out owner);
        if (owner != pid || !PostMessageW(handle, 0x0010, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Owned window WM_CLOSE failed");
    }
}
'@
$Root = Join-Path $env:LOCALAPPDATA ('Temp\opencode\icem-live-bugfix-' + [guid]::NewGuid().ToString('N'))
$ProgramData = Join-Path $Root 'ProgramData'
$LocalData = Join-Path $Root 'LocalAppData'
$Config = Join-Path $ProgramData 'Halliburton\iCem\DataMonitoring\config.ini'
[IO.Directory]::CreateDirectory($ProgramData) | Out-Null
[IO.Directory]::CreateDirectory($LocalData) | Out-Null
$Reservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$Reservation.Start()
$Port = $Reservation.LocalEndpoint.Port
$Reservation.Stop()
$Info = [Diagnostics.ProcessStartInfo]::new((Join-Path $Package 'icem-host-dev.exe'))
$Info.UseShellExecute = $false
$Info.CreateNoWindow = $true
$Info.WorkingDirectory = $Root
$Info.RedirectStandardOutput = $true
$Info.RedirectStandardError = $true
$Info.StandardOutputEncoding = [Text.Encoding]::UTF8
$Info.StandardErrorEncoding = [Text.Encoding]::UTF8
$Info.Environment['PROGRAMDATA'] = $ProgramData
$Info.Environment['LOCALAPPDATA'] = $LocalData
$Info.Environment['ICEM_HOST_CONSOLE_VISIBLE'] = '1'
$PipeName = 'icem-live-bugfix-' + [guid]::NewGuid().ToString('N')
foreach ($Argument in @('--station-config', $Config, '--tray-icon', (Join-Path $Package 'assets\iCemDataMonitor.ico'), '--rpc-pipe', $PipeName, '--bind', '127.0.0.1:0', '--web-bind', "127.0.0.1:$Port", '--duration-ms', '60000')) {
    $Info.ArgumentList.Add($Argument)
}
$Process = [Diagnostics.Process]::new()
$Process.StartInfo = $Info
$Client = [Net.Http.HttpClient]::new()
$Client.Timeout = [TimeSpan]::FromSeconds(2)
$OutputTask = $null
$ErrorTask = $null
$Started = $false
$Shell = $null
$ShellStarted = $false
$ShellError = $null
$ShellOutput = $null
try {
    if (-not $Process.Start()) { throw 'Owned Host did not start' }
    $Started = $true
    $OutputTask = $Process.StandardOutput.ReadToEndAsync()
    $ErrorTask = $Process.StandardError.ReadToEndAsync()
    $Deadline = [DateTime]::UtcNow.AddSeconds(15)
    $Status = $null
    do {
        if ($Process.HasExited) { throw "Owned Host exited $($Process.ExitCode): $($ErrorTask.GetAwaiter().GetResult())" }
        try {
            $Text = $Client.GetStringAsync("http://127.0.0.1:$Port/api/status").GetAwaiter().GetResult()
            $Status = $Text | ConvertFrom-Json
            if ($Status.busOpen) { break }
        } catch [Net.Http.HttpRequestException] { }
        if ([DateTime]::UtcNow -ge $Deadline) { throw 'Live MMF was not available before deadline' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    if (-not (Test-Path -LiteralPath $Config -PathType Leaf)) { throw 'First-run config was not published' }
    $Windows = @([IcemOwnedWindowProbe]::Visible([uint32]$Process.Id))
    if ($Windows.Count -ne 1) { throw "Expected one owned visible diagnostic window; found $($Windows.Count)" }
    [IcemOwnedWindowProbe]::Close([uint32]$Process.Id, $Windows[0])
    $Deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([IcemOwnedWindowProbe]::IsWindowVisible($Windows[0])) {
        if ($Process.HasExited) { throw 'Host exited after diagnostic X' }
        if ([DateTime]::UtcNow -ge $Deadline) { throw 'Diagnostic X did not hide its window' }
        Start-Sleep -Milliseconds 25
    }
    $ShellGuiTested = $false
    $ExistingShell = $false
    foreach ($Name in @('Local\iCemDataMonitorTray_v1_ShowWindow', 'Local\iCemDataMonitorTray_v1_Shutdown')) {
        $Existing = $null
        if ([Threading.EventWaitHandle]::TryOpenExisting($Name, [ref]$Existing)) {
            $ExistingShell = $true
            $Existing.Dispose()
        }
    }
    if ($ExistingShell) {
        Write-Warning 'An existing Shell owns shared UI events: actual Shell-X probe skipped without signalling it.'
    } else {
        $ShellInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $Package 'icem-shell-dev.exe'))
        $ShellInfo.UseShellExecute = $false
        $ShellInfo.CreateNoWindow = $true
        $ShellInfo.WorkingDirectory = $Root
        $ShellInfo.RedirectStandardOutput = $true
        $ShellInfo.RedirectStandardError = $true
        $ShellInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
        $ShellInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
        $ShellInfo.Environment['PROGRAMDATA'] = $ProgramData
        $ShellInfo.Environment['LOCALAPPDATA'] = $LocalData
        $ShellInfo.ArgumentList.Add('--pipe')
        $ShellInfo.ArgumentList.Add($PipeName)
        $Shell = [Diagnostics.Process]::new()
        $Shell.StartInfo = $ShellInfo
        if (-not $Shell.Start()) { throw 'Owned Shell did not start' }
        $ShellStarted = $true
        $ShellOutput = $Shell.StandardOutput.ReadToEndAsync()
        $ShellError = $Shell.StandardError.ReadToEndAsync()
        $Deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            if ($Shell.HasExited) { throw "Owned Shell exited before UI: $($ShellError.GetAwaiter().GetResult())" }
            $ShellWindows = @([IcemOwnedWindowProbe]::Visible([uint32]$Shell.Id))
            if ($ShellWindows.Count -eq 1) { break }
            if ([DateTime]::UtcNow -ge $Deadline) { throw 'Owned Shell did not show one window before deadline' }
            Start-Sleep -Milliseconds 100
        } while ($true)
        [IcemOwnedWindowProbe]::Close([uint32]$Shell.Id, $ShellWindows[0])
        $Deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (-not $Shell.HasExited) {
            if ($Process.HasExited) { throw 'Host exited when Shell window closed' }
            if ([DateTime]::UtcNow -ge $Deadline) { throw 'Owned Shell did not exit after X' }
            Start-Sleep -Milliseconds 50
        }
        if ($Shell.ExitCode -ne 0) { throw "Owned Shell close failed: $($ShellError.GetAwaiter().GetResult())" }
        $ShellGuiTested = $true
    }
    $InitialHeartbeat = $Status.heartbeat
    $Deadline = [DateTime]::UtcNow.AddSeconds(6)
    do {
        if ($Process.HasExited) { throw 'Host exited after diagnostic X' }
        $After = $Client.GetStringAsync("http://127.0.0.1:$Port/api/status").GetAwaiter().GetResult() | ConvertFrom-Json
        if ($After.busOpen -and $After.heartbeat -ne $InitialHeartbeat) { break }
        if ([DateTime]::UtcNow -ge $Deadline) { throw 'Live heartbeat did not advance after diagnostic X' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    $Registers = $Client.GetStringAsync("http://127.0.0.1:$Port/api/regs").GetAwaiter().GetResult() | ConvertFrom-Json
    $Result = [pscustomobject]@{
        package = $Package
        pid = $Process.Id
        first_run_config_created = $true
        bus_open = $After.busOpen
        heartbeat_before = $InitialHeartbeat
        heartbeat_after_window_close = $After.heartbeat
        diagnostic_x_hides_window = $true
        host_serves_after_diagnostic_x = $true
        shell_gui_x_tested = $ShellGuiTested
        visual_tray_acceptance = 'not tested'
        registers_stale = $Registers.stale
        config = $Config
    }
    $Result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Root 'result.json') -Encoding utf8
    $Result | Format-List
    Write-Host "PASS live Rust Host read-only MMF and diagnostic X; evidence: $Root"
} finally {
    if ($ShellStarted -and -not $Shell.HasExited) { $Shell.Kill(); $null = $Shell.WaitForExit(5000) }
    if ($null -ne $ShellOutput -and $ShellOutput.IsCompleted) { $ShellOutput.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Root 'shell.stdout.log') -Encoding utf8 }
    if ($null -ne $ShellError -and $ShellError.IsCompleted) { $ShellError.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Root 'shell.stderr.log') -Encoding utf8 }
    if ($null -ne $Shell) { $Shell.Dispose() }
    if ($Started -and -not $Process.HasExited) {
        $Process.Kill()
        if (-not $Process.WaitForExit(5000)) { Write-Warning 'Owned Host did not exit before cleanup deadline' }
    }
    if ($null -ne $OutputTask -and $OutputTask.IsCompleted) { $OutputTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Root 'stdout.log') -Encoding utf8 }
    if ($null -ne $ErrorTask -and $ErrorTask.IsCompleted) { $ErrorTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Root 'stderr.log') -Encoding utf8 }
    $Client.Dispose()
    $Process.Dispose()
}
