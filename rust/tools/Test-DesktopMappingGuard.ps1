[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Temp = Join-Path ([IO.Path]::GetTempPath()) ('icem-mapping-tests-' + [guid]::NewGuid().ToString('N'))
$Fixture = Join-Path $Temp 'rust'
function Write-Fixture([string] $Path, [string] $Text) {
    $Target = Join-Path $Fixture $Path
    [IO.Directory]::CreateDirectory((Split-Path $Target -Parent)) | Out-Null
    [IO.File]::WriteAllText($Target, $Text)
}
function Save-Mapping {
    Write-Fixture 'migration/desktop-gpui-mapping.json' ($script:Mapping | ConvertTo-Json -Depth 16)
}
function New-Fixture {
    if (Test-Path $Fixture) { Remove-Item -LiteralPath $Fixture -Recurse -Force }
    $script:Mapping = Get-Content -Raw (Join-Path $Root 'migration/desktop-gpui-mapping.json') | ConvertFrom-Json
    $Paths = @('tools/Verify-DesktopMapping.ps1', 'migration/desktop-ui-inventory.json',
        'migration/desktop-gpui-mapping.json', 'crates/icem-desktop/Cargo.toml')
    $Paths += @($script:Mapping.controls | ForEach-Object { $_.implementation })
    $Paths += @($script:Mapping.native_extensions | ForEach-Object { $_.implementation; $_.composition })
    $Paths += @($script:Mapping.host_console | ForEach-Object { $_.implementation })
    foreach ($Path in ($Paths | Sort-Object -Unique)) {
        Write-Fixture $Path ([IO.File]::ReadAllText((Join-Path $Root $Path)))
    }
}
function Change-Renderer([string] $Before, [string] $After) {
    $Path = 'crates/icem-desktop/src/logs.rs'
    $Text = [IO.File]::ReadAllText((Join-Path $Fixture $Path))
    if (-not $Text.Contains($Before)) { throw 'Missing mutation target in real renderer fixture' }
    Write-Fixture $Path ($Text.Replace($Before, $After))
}
function Check-Case([string] $Name, [scriptblock] $Mutate, [string] $ExpectedError = '') {
    New-Fixture
    & $Mutate
    $Failure = $null
    try { & (Join-Path $Fixture 'tools/Verify-DesktopMapping.ps1') }
    catch { $Failure = $_.Exception.Message }
    if ($ExpectedError) {
        if (-not $Failure -or -not $Failure.Contains($ExpectedError)) { throw "$Name expected '$ExpectedError', got '$Failure'" }
    } elseif ($Failure) { throw "$Name unexpectedly failed: $Failure" }
    Write-Host "PASS $Name"
}
try {
    Check-Case 'actual 176+6 mapping and typed renderer' {}
    Check-Case 'missing typed SetEnabled' { Change-Renderer 'Command::SetEnabled' 'Command::Get' } 'Invalid typed logging renderer action/index pairing'
    Check-Case 'missing typed Clear' { Change-Renderer 'Command::Clear' 'Command::Get' } 'Invalid typed logging renderer action/index pairing'
    Check-Case 'swapped renderer indices' {
        Change-Renderer 'Command::SetEnabled' 'Command::SWAP'
        Change-Renderer 'Command::Clear' 'Command::SetEnabled'
        Change-Renderer 'Command::SWAP' 'Command::Clear'
    } 'Invalid typed logging renderer action/index pairing'
    Check-Case 'wrong mapping action/index pairing' {
        $script:Mapping.native_extensions[0].index = 1
        $script:Mapping.native_extensions[1].index = 0
        Save-Mapping
    } 'Invalid logging extension action/index pairing'
    Check-Case 'unknown mapping action' {
        $script:Mapping.native_extensions[0].action = 'get'
        Save-Mapping
    } 'Invalid logging extension action/index pairing'
    Check-Case 'missing composition surface' {
        $script:Mapping.native_extensions[0].surface = 'UnwiredLogs'
        Save-Mapping
    } 'Unimplemented native extension:'
    Check-Case 'duplicate extension identity' {
        $script:Mapping.native_extensions[1].index = 0
        Save-Mapping
    } 'Duplicate native extension identity'
    Write-Host 'Desktop mapping guard regression suite passed (8 cases).'
} finally {
    if (Test-Path $Temp) { Remove-Item -LiteralPath $Temp -Recurse -Force }
}
