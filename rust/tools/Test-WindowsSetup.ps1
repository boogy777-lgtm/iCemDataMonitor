[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Setup,
    [string] $ExpectedMsi,
    [ValidateSet('.NET', 'Rust')][string] $ExpectedFamily
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Setup = (Resolve-Path -LiteralPath $Setup).Path
Add-Type -AssemblyName System.Windows.Forms
$null = [Reflection.Assembly]::LoadFile($Setup)
$LegacyCode = '{11111111-1111-1111-1111-111111111111}'
$RustCode = '{22222222-2222-2222-2222-222222222222}'
$Legacy = New-Object IcemSetup.Product($LegacyCode, '.NET', 'Legacy fixture')
$Rust = New-Object IcemSetup.Product($RustCode, 'Rust', 'Rust fixture')
$Products = [IcemSetup.Product[]]@($Legacy, $Rust)
foreach ($Product in $Products) {
    if ([IcemSetup.Policy]::RemoveArguments($Product.Code, $Products) -cne ('/x ' + $Product.Code)) { throw 'Wrong removal target' }
}
$Rejected = $false
try { [IcemSetup.Policy]::RemoveArguments('{33333333-3333-3333-3333-333333333333}', $Products) | Out-Null } catch { $Rejected = $true }
if (-not $Rejected) { throw 'Foreign product accepted' }
$Rejected = $false
try { [IcemSetup.Policy]::RemoveArguments($LegacyCode, [IcemSetup.Product[]]@()) | Out-Null } catch { $Rejected = $true }
if (-not $Rejected) { throw 'Stale product accepted' }
$Rejected = $false
try { [IcemSetup.Policy]::RemoveArguments('invalid /x injected', $Products) | Out-Null } catch { $Rejected = $true }
if (-not $Rejected) { throw 'Malformed product accepted' }
foreach ($Family in @('.NET', 'Rust')) {
    $Same = [IcemSetup.Product[]]@($Products | Where-Object Family -EQ $Family)
    if (-not [IcemSetup.Policy]::CanInstall($Family, $Same)) { throw 'Same-family maintenance blocked' }
    if ([IcemSetup.Policy]::CanInstall($Family, $Products)) { throw 'Cross-family install allowed before explicit removal' }
}
$Payload = [IcemSetup.Program]::Payload()
if ($Payload.Length -lt 1024) { throw 'Embedded MSI missing' }
if ($ExpectedMsi) {
    $ExpectedMsi = (Resolve-Path -LiteralPath $ExpectedMsi).Path
    $Sha = [Security.Cryptography.SHA256]::Create()
    try { $Hash = [BitConverter]::ToString($Sha.ComputeHash($Payload)).Replace('-', '') }
    finally { $Sha.Dispose() }
    if ($Hash -cne (Get-FileHash -LiteralPath $ExpectedMsi -Algorithm SHA256).Hash) { throw 'Setup embeds a different MSI' }
}
$Installed = @([IcemSetup.Inventory]::Read())
foreach ($Product in $Installed) {
    if ([IcemSetup.Policy]::RemoveArguments($Product.Code, [IcemSetup.Product[]]$Installed) -cne ('/x ' + $Product.Code)) { throw 'Installed product cannot be removed' }
    Write-Host "Detected removable: $Product ($($Product.Code))"
}
$Assembly = [IcemSetup.Program].Assembly
$Form = [Activator]::CreateInstance($Assembly.GetType('IcemSetup.SetupForm'), $true)
try {
    $Flags = [Reflection.BindingFlags]'NonPublic,Instance'
    $List = $Form.GetType().GetField('products', $Flags).GetValue($Form)
    $Remove = $Form.GetType().GetField('remove', $Flags).GetValue($Form)
    $Install = $Form.GetType().GetField('install', $Flags).GetValue($Form)
    $Family = [IcemSetup.Program]::Resource('payload.family').Trim()
    if ($ExpectedFamily -and $Family -cne $ExpectedFamily) { throw 'Setup belongs to a different payload family' }
    if ($List.Items.Count -ne $Installed.Count) { throw 'Setup UI omitted installed products' }
    if ($Remove.Enabled -ne ($Installed.Count -gt 0)) { throw 'Setup UI removal action unavailable' }
    if ($Install.Enabled -ne [IcemSetup.Policy]::CanInstall($Family, [IcemSetup.Product[]]$Installed)) { throw 'Setup UI install policy mismatch' }
} finally { $Form.Dispose() }
Write-Host 'PASS setup: both families, selected ProductCode, stale/foreign rejection, maintenance policy, embedded MSI SHA256, real installed-product discovery. No MSI mutation executed.'
