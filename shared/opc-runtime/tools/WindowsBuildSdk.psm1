# Build-time SDK selection only; never edits the checkout's global.json.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Initialize-WindowsBuildSdk([string] $Directory) {
    if (-not [IO.Path]::IsPathFullyQualified($Directory) -or -not (Test-Path -LiteralPath $Directory -PathType Container)) { throw 'SDK context requires an existing owned absolute build directory' }
    $Global = Join-Path $Directory 'global.json'
    if (Test-Path -LiteralPath $Global) { throw 'Refusing to replace an existing SDK context' }
    [IO.File]::WriteAllText($Global, '{"sdk":{"version":"8.0.423","rollForward":"disable"}}')
}
function Assert-WindowsBuildSdk {
    $Version = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $Version -cne '8.0.423') { throw 'Windows managed/WiX build requires SDK 8.0.423 in its owned context' }
    return $Version
}
Export-ModuleMember -Function Initialize-WindowsBuildSdk, Assert-WindowsBuildSdk
