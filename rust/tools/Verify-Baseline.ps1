[CmdletBinding()]
param([switch] $RequireComplete)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$SourceRoot = Split-Path $Root -Parent
$Manifest = Get-Content -Raw (Join-Path $Root 'migration/source-manifest.json') | ConvertFrom-Json
function Get-CanonicalHash([string] $FilePath, [string] $Relative) {
    $Hash = & git -C $SourceRoot hash-object "--path=$Relative" -- $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Cannot hash $Relative" }
    return $Hash.Trim()
}
$Missing = @()
$Verified = 0
foreach ($Item in $Manifest.files) {
    $Path = Join-Path $SourceRoot $Item.path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $Missing += $Item.path; continue }
    $Hash = & git hash-object --no-filters -- $Path
    if ($LASTEXITCODE -ne 0) { throw "git hash-object failed: $($Item.path)" }
    if ($Hash.Trim() -eq $Item.git_blob_sha1) {
        if ((Get-Item -Force -LiteralPath $Path).Length -ne $Item.size) { throw "Size mismatch: $($Item.path)" }
    } else {
        # The preserved upstream .gitattributes uses CRLF for .ps1 checkout.
        # Compare its canonical Git blob too; never normalize binary fixtures.
        $Relative = $Item.path
        $CanonicalHash = & git -C $SourceRoot hash-object "--path=$Relative" -- $Path
        if ($LASTEXITCODE -ne 0 -or $CanonicalHash.Trim() -ne $Item.git_blob_sha1) {
            throw "Hash mismatch: $($Item.path)"
        }
    }
    $Verified++
}
$Proto = Join-Path $Root 'contracts/proto/telemetry.proto'
$Original = Join-Path $SourceRoot 'iCemDataMonitor.Contracts/Protos/telemetry.proto'
if ((Get-FileHash $Proto).Hash -ne (Get-FileHash $Original).Hash) { throw 'Frozen proto changed' }
foreach ($Asset in @('iCemDataMonitor.ico', 'iCemEmule.ico')) {
    if ((Get-FileHash (Join-Path $Root "assets/$Asset")).Hash -ne (Get-FileHash (Join-Path $SourceRoot "assets/$Asset")).Hash) {
        throw "Original icon changed: $Asset"
    }
}
$Dashboard = Join-Path $Root 'web/dashboard'
Get-ChildItem -File -Recurse $Dashboard | ForEach-Object {
    $Relative = [IO.Path]::GetRelativePath($Dashboard, $_.FullName)
    $Source = Join-Path $SourceRoot ('iCemDataMonitor.Host/wwwroot/' + $Relative)
    # The original tree uses text=auto, while the frozen Rust SPA uses -text.
    # Compare canonical Git bytes; Windows checkout newline conversion is not a source change.
    $RustRelative = 'rust/web/dashboard/' + $Relative.Replace('\', '/')
    $SourceRelative = 'iCemDataMonitor.Host/wwwroot/' + $Relative.Replace('\', '/')
    if ((Get-CanonicalHash $_.FullName $RustRelative) -ne (Get-CanonicalHash $Source $SourceRelative)) {
        throw "Original SPA changed: $Relative"
    }
}
$Schema = Get-Content -Raw (Join-Path $Root 'contracts/http/openapi.json') | ConvertFrom-Json
$Inventory = Get-Content -Raw (Join-Path $Root 'contracts/http/route-inventory.json') | ConvertFrom-Json
$Expected = @($Inventory.routes | Where-Object status -eq 'active' | Select-Object -ExpandProperty path | Sort-Object)
$Actual = @($Schema.paths.PSObject.Properties.Name | Sort-Object)
if (($Expected -join '|') -ne ($Actual -join '|')) { throw 'Active REST inventory drift' }
if ($Missing.Count -gt 0) {
    Write-Warning ("Missing {0} baseline file(s): {1}" -f $Missing.Count, ($Missing -join ', '))
    if ($RequireComplete) { throw 'Full source copy is required before publication' }
}
Write-Host "Baseline: $Verified verified of $($Manifest.files.Count); frozen proto/assets and REST inventory OK."
