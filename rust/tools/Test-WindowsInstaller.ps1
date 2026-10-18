#requires -Version 7.4
# Real MSI authoring/install contracts, with explicitly synthetic runtime bytes.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows Installer contracts require a disposable Windows runner' }
$Root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $Root 'installer/WindowsPayload.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WindowsPackagingFixture.psm1') -Force
$Work = Join-Path ([IO.Path]::GetTempPath()) ('icem-msi-contract-' + [guid]::NewGuid().ToString('N'))
try {
    $Fixture = New-WindowsPackagingFixture $Work
    $Archive = New-WindowsArchive -RepositoryRoot $Fixture.repo -BinaryDirectory $Fixture.bins -AddinDirectory $Fixture.addin -OutputDirectory (Join-Path $Work 'out') -Version 0.1.0 -Commit ('1' * 40) -Compiler 'SYNTHETIC CONTRACT FIXTURE; NOT A RELEASE'
    $Msi = & (Join-Path $PSScriptRoot 'Build-WindowsMsi.ps1') -Archive $Archive.archive
    & (Join-Path $PSScriptRoot 'Test-WindowsMsiUpgrade.ps1') -Msi $Msi.msi -RepositoryRoot $Fixture.repo -BinaryDirectory $Fixture.bins
    Write-Host 'PASS installer contracts with fixture bytes only. No release MSI is uploaded.'
} finally { if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force } }
