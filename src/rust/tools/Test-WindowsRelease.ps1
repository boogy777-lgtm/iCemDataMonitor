#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Archive)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The executable smoke test requires Windows.' }
$Root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $Root 'installer/WindowsPayload.psm1') -Force
$Archive = (Resolve-Path -LiteralPath $Archive).Path
$Expected = (Get-Content -Raw -LiteralPath "$Archive.sha256").Split(' ')[0]
if ($Expected -ine (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash) { throw 'Archive SHA256 mismatch' }
$Temp = Join-Path ([IO.Path]::GetTempPath()) ("iCem release $([char]0x0442) " + [guid]::NewGuid().ToString('N'))
$Package = Join-Path $Temp 'unpacked'
[IO.Directory]::CreateDirectory($Temp) | Out-Null
$Children = [Collections.Generic.List[object]]::new()
function Start-Child([string] $Executable, [string[]] $Arguments) {
    $Info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $Info.UseShellExecute = $false
    $Info.WorkingDirectory = $Temp # intentionally outside the extracted package
    $Info.RedirectStandardOutput = $true
    $Info.RedirectStandardError = $true
    foreach ($Argument in $Arguments) { $Info.ArgumentList.Add($Argument) }
    $Process = [Diagnostics.Process]::new()
    $Process.StartInfo = $Info
    if (-not $Process.Start()) { throw "Cannot start $Executable" }
    $Child = [pscustomobject]@{ process = $Process; output = $Process.StandardOutput.ReadToEndAsync(); error = $Process.StandardError.ReadToEndAsync() }
    $Children.Add($Child)
    return $Child
}
function Wait-Child($Child, [int] $Timeout = 15000, [int] $ExpectedExit = 0) {
    if (-not $Child.process.WaitForExit($Timeout)) { throw "Process timeout: $($Child.process.StartInfo.FileName)" }
    $Output = $Child.output.GetAwaiter().GetResult()
    $ErrorOutput = $Child.error.GetAwaiter().GetResult()
    if ($Child.process.ExitCode -ne $ExpectedExit) { throw "Exit $($Child.process.ExitCode): $ErrorOutput $Output" }
    return [pscustomobject]@{ output = $Output; error = $ErrorOutput }
}
try {
    # Kept only until the browser step completes. The workflow's always() cleanup
    # owns this exact per-run directory, including failures on persistent SDK runners.
    if ($env:GITHUB_ENV) { Add-Content -LiteralPath $env:GITHUB_ENV -Value "ICEM_RELEASE_SMOKE_DIRECTORY=$Temp" }
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Package)
    $Manifest = Test-WindowsPayload $Package
    $HostTemplates = ($Manifest.binaries | Where-Object role -EQ 'host').templates_directory
    $EmulatorTemplates = ($Manifest.binaries | Where-Object role -EQ 'emulator').templates_directory
    $HostExe = Join-Path $Package 'icem-host-dev.exe'
    $Engine = Join-Path $Package 'icem-emulator-engine-dev.exe'
    # Standalone dispatch before any settings/startup, using only extracted assets.
    $SelfTest = Start-Child $Engine @('--selftest')
    $SelfTestResult = Wait-Child $SelfTest 45000
    $SelfTestIds = [regex]::Matches($SelfTestResult.output, '(?m)^T(\d+) PASS\r?$') | ForEach-Object { [int] $_.Groups[1].Value }
    if (($SelfTestIds -join ',') -cne ((1..26) -join ',') -or $SelfTestResult.output -match '(?m)^T\d+ FAIL' -or $SelfTestResult.output -notmatch '(?m)^ALL TESTS PASSED\r?$') { throw 'Packaged emulator selftest incomplete' }
    if (Test-Path -LiteralPath (Join-Path $Temp 'emule_config.xml')) { throw 'Selftest created settings' }
    $Fixture = Join-Path $Temp 'bus.bin'
    Copy-Item -LiteralPath (Join-Path $Root 'tests/fixtures/bus-v5-minimal.bin') -Destination $Fixture
    $HostRun = Start-Child $HostExe @('--modbus-fixture', $Fixture, '--bind', '127.0.0.1:0', '--web-bind', '127.0.0.1:0', '--duration-ms', '1000', '--registry', (Join-Path $Package "$HostTemplates/variable_registry.json"), '--theme-file', (Join-Path $Temp 'ui-theme'), '--rpc-pipe', ('icem-package-host-' + [guid]::NewGuid().ToString('N')))
    $HostResult = Wait-Child $HostRun
    if ($HostResult.error -notmatch 'Web http://127\.0\.0\.1:') { throw 'Packaged Host did not bind Web' }
    $Pipe = 'icem-package-emulator-' + [guid]::NewGuid().ToString('N')
    $EngineRun = Start-Child $Engine @('--config', (Join-Path $Temp 'emule_config.xml'), '--templates', (Join-Path $Package $EmulatorTemplates), '--pipe', $Pipe, '--duration-ms', '15000')
    $Readiness = [Diagnostics.Stopwatch]::StartNew()
    $Status = $null
    do {
        # Read-only readiness probe; the quit mutation below is never retried.
        $Probe = Start-Child $Engine @('--pipe', $Pipe, '--cli', 'status')
        if (-not $Probe.process.WaitForExit(4000)) { throw 'Status probe timeout' }
        if ($Probe.process.ExitCode -eq 0) { $Status = (Wait-Child $Probe).output | ConvertFrom-Json; break }
        if ($EngineRun.process.HasExited) { $null = Wait-Child $EngineRun; throw 'Engine stopped before readiness' }
        Start-Sleep -Milliseconds 100
    } while ($Readiness.Elapsed.TotalSeconds -lt 10)
    if (-not $Status -or -not $Status.ok) { throw 'Packaged engine not ready before deadline' }
    if ($Status.data.com.streaming -or $Status.data.lan.streaming) { throw 'Fresh package config unexpectedly started hardware streams' }
    $Quit = Start-Child $Engine @('--pipe', $Pipe, '--cli', 'quit')
    $Reply = (Wait-Child $Quit 5000).output | ConvertFrom-Json
    if (-not $Reply.ok) { throw 'Packaged engine did not acknowledge quit' }
    $null = Wait-Child $EngineRun 5000
    # Missing sidecar must fail before GPUI initialization and still join the
    # engine worker/drain its pipe. The shipped ZIP itself is never modified.
    $Icon = Join-Path $Package 'assets/iCemEmule.ico'
    $HeldIcon = "$Icon.hold"
    Move-Item -LiteralPath $Icon -Destination $HeldIcon
    try {
        $MissingAsset = Start-Child $Engine @('--desktop', '--config', (Join-Path $Temp 'missing-asset.xml'), '--templates', (Join-Path $Package $EmulatorTemplates), '--pipe', ('icem-package-missing-' + [guid]::NewGuid().ToString('N')), '--duration-ms', '1000')
        $MissingResult = Wait-Child $MissingAsset 10000 1
        if ($MissingResult.error -notmatch 'application asset missing:') { throw 'Missing icon did not produce an explicit asset diagnostic' }
    } finally { Move-Item -LiteralPath $HeldIcon -Destination $Icon }
    $null = Test-WindowsPayload $Package
    # Check loader/startup error behavior without requiring a GPU/interactive desktop.
    $ShellRun = Start-Child (Join-Path $Package 'icem-shell-dev.exe') @('--web-url', 'invalid')
    $ShellResult = Wait-Child $ShellRun 10000 1
    if ($ShellResult.error -notmatch '--web-url requires') { throw 'Unexpected Shell startup diagnostic' }
    if ($env:GITHUB_ENV) { Add-Content -LiteralPath $env:GITHUB_ENV -Value "ICEM_HOST_EXE=$HostExe" }
    Write-Host "PASS extracted Windows release: $($Manifest.source_commit), Unicode/space relocation, unrelated cwd, Host bind, emulator status/quit ACK, missing-icon cleanup, Shell loader."
    # Keep successful extraction for the release-browser check in the same CI job.
    Write-Host "Extracted payload: $Package"
} finally {
    foreach ($Child in $Children) {
        try { if (-not $Child.process.HasExited) { $Child.process.Kill($true); $null = $Child.process.WaitForExit(5000) } }
        finally { $Child.process.Dispose() }
    }
    if (-not $env:GITHUB_ENV -and (Test-Path -LiteralPath $Temp)) { Remove-Item -LiteralPath $Temp -Recurse -Force }
}
