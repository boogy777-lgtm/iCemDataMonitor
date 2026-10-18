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
        [guid]$Policy.upgrade_code -eq [guid]'34A0FD7C-EBD5-4E28-BA96-AE6575896F32' -or
        $Policy.product_name -notlike '*Rust Development') { throw 'Invalid/separation-violating Rust MSI policy' }
    return $Policy
}
function Get-WindowsComponentGuid([string] $Identity) {
    $Policy = Read-WindowsInstallerPolicy
    $Hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$($Policy.upgrade_code):$Identity"))
    return [guid]::new([byte[]]$Hash[0..15]).ToString('B')
}
function Write-WindowsInstallerSource([string] $PayloadDirectory, [string] $Version, [string] $OutputFile) {
    Assert-WindowsMsiVersion $Version
    $Manifest = Test-WindowsPayload $PayloadDirectory
    if ($Manifest.artifact_version -cne $Version) { throw 'MSI version must match the verified payload version.' }
    $Policy = Read-WindowsInstallerPolicy
    $Xml = [xml](Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Product.wxs'))
    $Ns = [Xml.XmlNamespaceManager]::new($Xml.NameTable)
    $Ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
    $Package = $Xml.SelectSingleNode('/w:Wix/w:Package', $Ns)
    $Package.SetAttribute('Name', $Policy.product_name)
    $Package.SetAttribute('Manufacturer', $Policy.manufacturer)
    $Package.SetAttribute('Version', $Version)
    $Package.SetAttribute('UpgradeCode', $Policy.upgrade_code)
    $HostBinary = @($Manifest.binaries | Where-Object role -CEQ 'host')[0]
    $ShellBinary = @($Manifest.binaries | Where-Object role -CEQ 'shell')[0]
    $Xml.SelectSingleNode('//w:Shortcut[@Id="DesktopMonitorShortcut"]', $Ns).SetAttribute('Target', '[MONITORROOT]' + $ShellBinary.destination)
    $Xml.SelectSingleNode('//w:RegistryValue[@Name="iCemDataMonitorHost"]', $Ns).SetAttribute('Value', '"[MONITORROOT]' + $HostBinary.destination + '" --autostart')
    foreach ($Component in $Xml.SelectNodes('//w:Component', $Ns)) {
        $Component.SetAttribute('Guid', (Get-WindowsComponentGuid $Component.Id))
    }
    $Xml.Save($OutputFile)
    return @($Manifest.files).Count + 1
}
function Write-WindowsInstallerPayload([string] $PayloadDirectory, [string] $RepositoryRoot, [string] $WorkDirectory) {
    if (-not $IsWindows) { throw 'The unchanged legacy payload harvester requires Windows paths.' }
    $Manifest = Test-WindowsPayload $PayloadDirectory
    $Stage = Join-Path $WorkDirectory 'stage'
    foreach ($Relative in (@($Manifest.files.path) + @('release-manifest.json'))) {
        $Target = if ($Relative -ceq 'addin/Burservis.iCem.DataMonitor.dll') { Join-Path $Stage $Relative }
                  else { Join-Path $Stage "runtime/$Relative" }
        [IO.Directory]::CreateDirectory((Split-Path $Target -Parent)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PayloadDirectory $Relative) -Destination $Target
    }
    $Wxs = Join-Path $WorkDirectory 'payload.wxs'
    & (Join-Path $RepositoryRoot 'installer/gen-payload.ps1') -StageRoot $Stage -OutputPath $Wxs -RootFolders @('runtime') -EmulatorFolder '' | Out-Host
    $Xml = [xml](Get-Content -Raw -LiteralPath $Wxs)
    $Ns = [Xml.XmlNamespaceManager]::new($Xml.NameTable); $Ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
    $Files = @($Xml.SelectNodes('//w:File', $Ns))
    $Sources = @($Files | ForEach-Object { (Resolve-Path -LiteralPath $_.Source).Path } | Sort-Object -Unique)
    $Expected = @(Get-ChildItem -LiteralPath $Stage -Recurse -File)
    if ($Files.Count -ne $Manifest.files.Count + 1 -or $Sources.Count -ne $Expected.Count -or
        $Xml.SelectNodes('//w:ComponentGroup[@Id="Payload.Addin"]/w:Component/w:File', $Ns).Count -ne 1) { throw 'Legacy harvester omitted/duplicated payload files or AddIn mount' }
    foreach ($File in $Expected) { if ($Sources -cnotcontains $File.FullName) { throw "Unharvested file: $($File.FullName)" } }
    # Legacy harvesting is reused only as inventory-to-components translation.
    # Separate Rust ownership, including the vendor-path AddIn component, must
    # never share a component GUID with a legacy package.
    foreach ($Component in $Xml.SelectNodes('//w:Component', $Ns)) {
        $Component.SetAttribute('Guid', (Get-WindowsComponentGuid $Component.Id))
    }
    $Xml.Save($Wxs)
    return $Wxs
}
function New-WindowsInstallerAssets([string] $RepositoryRoot, [string] $WorkDirectory) {
    $Legacy = Join-Path $RepositoryRoot 'installer'
    $Adapter = Join-Path $WorkDirectory 'branding'
    $Assets = Join-Path $Adapter 'assets'
    [IO.Directory]::CreateDirectory($Assets) | Out-Null
    foreach ($Name in @('Burservis_logo_cyr_red.png', 'app-icon.ico')) {
        Copy-Item -LiteralPath (Join-Path $Legacy "assets/$Name") -Destination (Join-Path $Assets $Name)
    }
    Copy-Item -LiteralPath (Join-Path $Legacy 'gen-ui-bmps.ps1') -Destination $Adapter
    return $Adapter
}
Export-ModuleMember -Function Assert-WindowsMsiVersion, Get-NextWindowsMsiVersion, Select-WindowsReleaseVersion, Read-WindowsInstallerPolicy, Write-WindowsInstallerSource, Write-WindowsInstallerPayload, New-WindowsInstallerAssets
