#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Archive, [string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'MSI compilation requires Windows.' }
$Root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $Root 'installer/WindowsPayload.psm1') -Force
Import-Module (Join-Path $Root 'installer/WindowsInstaller.psm1') -Force
$Archive = (Resolve-Path -LiteralPath $Archive).Path
if ((Get-Content -Raw -LiteralPath "$Archive.sha256").Split(' ')[0] -ine (Get-FileHash -LiteralPath $Archive).Hash) { throw 'Archive SHA256 mismatch' }
if (-not $OutputDirectory) { $OutputDirectory = Split-Path $Archive -Parent }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$Work = Join-Path $OutputDirectory ('.msi-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($Work) | Out-Null
try {
    $Payload = Join-Path $Work 'payload'
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Payload)
    $Manifest = Test-WindowsPayload $Payload
    $Version = $Manifest.artifact_version
    Assert-WindowsMsiVersion $Version
    $Name = "iCemDataMonitor-rust-dev-$Version-windows-x64.msi"
    $Msi = Join-Path $OutputDirectory $Name
    if ((Test-Path -LiteralPath $Msi) -or (Test-Path -LiteralPath "$Msi.sha256")) { throw "Release already exists: $Msi" }
    $Source = Join-Path $Work 'Package.wxs'
    $Count = Write-WindowsInstallerSource $Payload $Version $Source
    $RepositoryRoot = Split-Path $Root -Parent
    $PayloadSource = Write-WindowsInstallerPayload $Payload $RepositoryRoot $Work
    $Branding = New-WindowsInstallerAssets $RepositoryRoot $Work
    & (Join-Path $Branding 'gen-ui-bmps.ps1')
    Push-Location (Join-Path $Root 'installer')
    try {
        & dotnet tool restore --tool-manifest .config/dotnet-tools.json | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Pinned WiX tool restore failed.' }
        $WixVersion = (Get-Content -Raw .config/dotnet-tools.json | ConvertFrom-Json).tools.wix.version
        $Extensions = @('WixToolset.UI.wixext')
        $ExtensionArgs = @()
        foreach ($Extension in $Extensions) {
            & dotnet tool run wix -- extension add -g "$Extension/$WixVersion" | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Pinned WiX extension restore failed: $Extension" }
            $ExtensionArgs += @('-ext', "$Extension/$WixVersion")
        }
        & dotnet tool run wix -- build $Source $PayloadSource -arch x64 -wx -dcl high @ExtensionArgs -define "AssetsDir=$(Join-Path $Branding 'assets')" -intermediateFolder (Join-Path $Work 'obj') -o (Join-Path $Work $Name) | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'WiX MSI build/validation failed.' }
    } finally { Pop-Location }
    $Built = Join-Path $Work $Name
    $Hash = (Get-FileHash -LiteralPath $Built -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $Work 'checksum'), "$Hash  $Name`n")
    [IO.File]::Move($Built, $Msi)
    [IO.File]::Move((Join-Path $Work 'checksum'), "$Msi.sha256")
    Write-Host "Windows MSI: $Msi ($Count files, version $Version)"
    return [pscustomobject]@{ msi = $Msi; checksum = "$Msi.sha256"; sha256 = $Hash; version = $Version }
} finally { if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force } }
