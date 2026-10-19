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

# New controls stay separate from the frozen 176-control legacy inventory.
$Extensions = @($Mapping.native_extensions)
if ($Extensions.Count -ne 6) { throw 'Expected six logging extension controls' }
$Keys = @($Extensions | ForEach-Object { "$($_.surface)|$($_.index)" })
if (@($Keys | Sort-Object -Unique).Count -ne $Keys.Count) { throw 'Duplicate native extension identity' }
foreach ($Control in $Extensions) {
    if ($Control.action -notin @('set','clear') -or $Control.index -notin @(0,1)) { throw 'Invalid logging extension action/index' }
    $Implementation = Get-Content -Raw (Join-Path $Root $Control.implementation)
    $Composition = Get-Content -Raw (Join-Path $Root $Control.composition)
    if (-not $Composition.Contains('"' + $Control.surface + '"') -or -not $Implementation.Contains('"' + $Control.action + '"')) { throw "Unimplemented native extension: $($Control.name)" }
}
Write-Host 'Native logging extensions: 6; visual acceptance NOT_RUN.'

# E15 owns a separate diagnostic surface, not another GPUI/logging control.
$Console = @($Mapping.host_console)
if ($Console.Count -ne 2 -or ($Console.name -join ',') -cne 'HostDiagnosticWindow,HostDiagnosticTranscript') { throw 'Host console inventory drift' }
foreach ($Control in $Console) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $Control.implementation) -PathType Leaf)) { throw 'Missing Host diagnostic implementation' }
}
Write-Host 'Host diagnostic surface: window + readonly transcript; visual acceptance NOT_RUN.'
