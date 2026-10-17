#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsPayload.psm1')

function Assert-WindowsMsiVersion([string] $Version) {
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'MSI version must be canonical x.y.z without a suffix.' }
    $Parts = $Version.Split('.')
    if ([long]$Parts[0] -gt 255 -or [long]$Parts[1] -gt 255 -or [long]$Parts[2] -gt 65535) { throw 'MSI version exceeds 255.255.65535.' }
}
function Get-NextWindowsMsiVersion([string] $Version) {
    Assert-WindowsMsiVersion $Version
    $Parsed = [version]$Version
    if ($Parsed.Build -eq 65535) { throw 'Patch version exhausted; choose an explicit higher minor/major version.' }
    return "$($Parsed.Major).$($Parsed.Minor).$($Parsed.Build + 1)"
}
function Select-WindowsReleaseVersion([string] $Requested = 'next', [string[]] $Tags = @()) {
    $Versions = @($Tags | Where-Object { $_ -like 'refs/tags/rust-v*' -or $_ -like 'rust-v*' } | ForEach-Object {
        $Value = $_ -replace '^(refs/tags/)?rust-v', ''
        Assert-WindowsMsiVersion $Value
        [version]$Value
    } | Sort-Object -Unique)
    $Last = if ($Versions.Count) { $Versions[-1].ToString() } else { $null }
    if ($Requested -eq 'next') {
        return $(if ($Last) { Get-NextWindowsMsiVersion $Last } else { '0.1.0' })
    }
    Assert-WindowsMsiVersion $Requested
    if ($Last -and [version]$Requested -le [version]$Last) { throw "Version must exceed the last Rust tag $Last; existing releases are never overwritten." }
    return $Requested
}
function Read-WindowsInstallerPolicy {
    $Policy = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'windows-installer.json') | ConvertFrom-Json
    if ($Policy.schema_version -ne 1 -or [guid]$Policy.upgrade_code -eq [guid]::Empty -or
        $Policy.upgrade_code -eq '34A0FD7C-EBD5-4E28-BA96-AE6575896F32' -or
        $Policy.directory_name -ne 'iCemDataMonitor Rust Development') { throw 'Invalid/separation-violating Rust MSI policy' }
    return $Policy
}
function Write-WindowsInstallerSource([string] $PayloadDirectory, [string] $Version, [string] $OutputFile) {
    Assert-WindowsMsiVersion $Version
    $PayloadDirectory = (Resolve-Path -LiteralPath $PayloadDirectory).Path
    $Manifest = Test-WindowsPayload $PayloadDirectory
    if ($Manifest.artifact_version -cne $Version) { throw 'MSI version must match the verified payload version.' }
    $Policy = Read-WindowsInstallerPolicy
    $Xml = [xml]::new()
    $Namespace = 'http://wixtoolset.org/schemas/v4/wxs'
    function Add-WixElement($Parent, [string] $Name, [hashtable] $Attributes = @{}) {
        $Element = $Xml.CreateElement($Name, $Namespace)
        foreach ($Key in $Attributes.Keys) { $Element.SetAttribute($Key, [string]$Attributes[$Key]) }
        $null = $Parent.AppendChild($Element)
        return $Element
    }
    function Path-Id([string] $Prefix, [string] $Path) {
        $Hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Path.ToLowerInvariant()))
        return $Prefix + [Convert]::ToHexString($Hash).Substring(0, 32)
    }
    $Wix = Add-WixElement $Xml 'Wix'
    $Package = Add-WixElement $Wix 'Package' @{
        Name = $Policy.product_name; Manufacturer = $Policy.manufacturer; Version = $Version
        UpgradeCode = $Policy.upgrade_code; ProductCode = '*'; Scope = 'perMachine'
        Compressed = 'yes'; InstallerVersion = '500'
    }
    $null = Add-WixElement $Package 'MajorUpgrade' @{
        DowngradeErrorMessage = 'A newer Rust development version is already installed.'
        Schedule = 'afterInstallInitialize'
    }
    $null = Add-WixElement $Package 'MediaTemplate' @{ EmbedCab = 'yes' }
    $ProgramFiles = Add-WixElement $Package 'StandardDirectory' @{ Id = 'ProgramFiles64Folder' }
    $Vendor = Add-WixElement $ProgramFiles 'Directory' @{ Id = 'RustVendor'; Name = $Policy.manufacturer }
    $Install = Add-WixElement $Vendor 'Directory' @{ Id = 'INSTALLFOLDER'; Name = $Policy.directory_name }
    $Directories = @{ '' = $Install }
    $Feature = Add-WixElement $Package 'Feature' @{ Id = 'Runtime'; Title = $Policy.product_name; Level = '1' }
    $Files = @($Manifest.files.path) + @('release-manifest.json')
    foreach ($Relative in ($Files | Sort-Object)) {
        $Parent = $Install
        $Prefix = ''
        $Parts = $Relative.Split('/')
        for ($Index = 0; $Index -lt $Parts.Length - 1; $Index++) {
            $Prefix = if ($Prefix) { "$Prefix/$($Parts[$Index])" } else { $Parts[$Index] }
            if (-not $Directories.ContainsKey($Prefix)) {
                $Directories[$Prefix] = Add-WixElement $Parent 'Directory' @{ Id = (Path-Id 'd' $Prefix); Name = $Parts[$Index] }
            }
            $Parent = $Directories[$Prefix]
        }
        $ComponentId = Path-Id 'c' $Relative
        $Component = Add-WixElement $Parent 'Component' @{ Id = $ComponentId; Guid = '*'; Bitness = 'always64' }
        $null = Add-WixElement $Component 'File' @{
            Id = (Path-Id 'f' $Relative); Source = (Join-Path $PayloadDirectory $Relative)
            Name = $Parts[-1]; KeyPath = 'yes'
        }
        $null = Add-WixElement $Feature 'ComponentRef' @{ Id = $ComponentId }
    }
    $Xml.Save($OutputFile)
    return $Files.Count
}
Export-ModuleMember -Function Assert-WindowsMsiVersion, Get-NextWindowsMsiVersion, Select-WindowsReleaseVersion, Read-WindowsInstallerPolicy, Write-WindowsInstallerSource
