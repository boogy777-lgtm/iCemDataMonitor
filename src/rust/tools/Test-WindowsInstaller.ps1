#requires -Version 7.4
# Real MSI authoring/install contracts, with explicitly synthetic runtime bytes.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows Installer contracts require a disposable Windows runner' }
$Root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $Root 'installer/WindowsPayload.psm1') -Force
Import-Module (Join-Path $Root 'installer/WindowsInstaller.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WindowsPackagingFixture.psm1') -Force
$Work = Join-Path ([IO.Path]::GetTempPath()) ('icem-msi-contract-' + [guid]::NewGuid().ToString('N'))
try {
    $Fixture = New-WindowsPackagingFixture $Work
    $Archive = New-WindowsArchive -RepositoryRoot $Fixture.repo -BinaryDirectory $Fixture.bins -AddinDirectory $Fixture.addin -OutputDirectory (Join-Path $Work 'out') -Version 0.1.0 -Commit ('1' * 40) -Compiler 'SYNTHETIC CONTRACT FIXTURE; NOT A RELEASE'
    $Payload = Join-Path $Work 'payload'
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive.archive, $Payload)
    $First = Join-Path $Work 'harvest-one'; $Second = Join-Path $Work 'harvest-two'
    $One = [xml](Get-Content -Raw (Write-WindowsInstallerPayload $Payload $Fixture.repo $First))
    $Two = [xml](Get-Content -Raw (Write-WindowsInstallerPayload $Payload $Fixture.repo $Second))
    $LegacyFile = Join-Path $Work 'legacy-payload.wxs'
    & (Join-Path $Fixture.repo 'installer/gen-payload.ps1') -StageRoot (Join-Path $First 'stage') -OutputPath $LegacyFile -RootFolders @('runtime') -EmulatorFolder '' | Out-Host
    $Legacy = [xml](Get-Content -Raw $LegacyFile)
    $Ns = [Xml.XmlNamespaceManager]::new($One.NameTable); $Ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
    $LegacyGuids = @($Legacy.SelectNodes('//w:Component', $Ns) | ForEach-Object { [guid]$_.Guid })
    $IdentityOne = @($One.SelectNodes('//w:Component', $Ns) | ForEach-Object {
        if ($LegacyGuids -contains [guid]$_.Guid) { throw 'Rust/legacy payload component ownership overlaps' }
        $_.Id + ':' + $_.Guid
    } | Sort-Object)
    $IdentityTwo = @($Two.SelectNodes('//w:Component', $Ns) | ForEach-Object { $_.Id + ':' + $_.Guid } | Sort-Object)
    if (($IdentityOne -join ',') -cne ($IdentityTwo -join ',')) { throw 'Payload component identity depends on build paths' }
    $Msi = & (Join-Path $PSScriptRoot 'Build-WindowsMsi.ps1') -Archive $Archive.archive
    & (Join-Path $PSScriptRoot 'Test-WindowsMsiUpgrade.ps1') -Msi $Msi.msi -RepositoryRoot $Fixture.repo -BinaryDirectory $Fixture.bins
    Write-Host 'PASS installer contracts with fixture bytes only. No release MSI is uploaded.'
} finally { if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force } }
