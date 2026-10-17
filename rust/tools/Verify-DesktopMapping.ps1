[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Inventory = Get-Content -Raw (Join-Path $Root 'migration/desktop-ui-inventory.json') | ConvertFrom-Json
$Mapping = Get-Content -Raw (Join-Path $Root 'migration/desktop-gpui-mapping.json') | ConvertFrom-Json
$Expected = @()
foreach ($Source in $Inventory.sources) {
    for ($i=0; $i -lt $Source.named_controls.Count; $i++) {
        $Expected += "$($Source.source)|$i|$($Source.named_controls[$i].name)"
    }
}
$Actual = @($Mapping.controls | ForEach-Object { "$($_.source)|$($_.index)|$($_.name)" })
if ($Actual.Count -ne @($Actual | Sort-Object -Unique).Count) { throw 'Duplicate desktop mapping keys' }
if ((($Expected | Sort-Object) -join "`n") -cne (($Actual | Sort-Object) -join "`n")) { throw 'Desktop inventory/mapping drift' }
foreach ($Control in $Mapping.controls) {
    if ($Control.status -notin @('implemented','partial','pending')) { throw "Unknown control status: $($Control.name)" }
    if (-not (Test-Path -LiteralPath (Join-Path $Root $Control.implementation) -PathType Leaf)) { throw "Missing implementation: $($Control.implementation)" }
}
Write-Host "Desktop mapping: $($Actual.Count) entries; completeness only, visual parity NOT PROVEN."

# The native desktop decision is an invariant, not a documentation-only promise.
$DesktopRoot = Join-Path $Root 'crates/icem-desktop'
$Cargo = Get-Content -Raw (Join-Path $DesktopRoot 'Cargo.toml')
if ($Cargo -match '(?m)^\s*(wry|tao|webview2(?:-com)?)\s*=') { throw 'Browser dependency in native desktop' }
foreach ($File in Get-ChildItem -Path (Join-Path $DesktopRoot 'src') -Filter '*.rs' -Recurse -File) {
    if ((Get-Content -Raw $File.FullName) -match 'wry::|WebViewBuilder|build_as_child') { throw "Embedded browser in $($File.FullName)" }
}
