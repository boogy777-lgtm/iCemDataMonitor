#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $Root 'installer/WindowsPayload.psm1') -Force
Import-Module (Join-Path $Root 'installer/WindowsInstaller.psm1') -Force
Import-Module (Join-Path $Root 'installer/LegacyAddin.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WindowsPackagingFixture.psm1') -Force
$Temp = Join-Path ([IO.Path]::GetTempPath()) ('icem-package-tests-' + [guid]::NewGuid().ToString('N'))
$Repo = Join-Path $Temp 'repo'
$Bins = Join-Path $Temp 'bins'
$Output = Join-Path $Temp 'out'
function Assert-Rejected([scriptblock] $Operation, [string] $Message) {
    $Failed = $false
    try { & $Operation | Out-Null } catch {
        if (-not $_.Exception.Message.Contains($Message)) { throw }
        $Failed = $true
    }
    if (-not $Failed) { throw "Expected rejection: $Message" }
}
function Package([string] $Version) {
    New-WindowsArchive -RepositoryRoot $Repo -BinaryDirectory $Bins -AddinDirectory $Fixture.addin -OutputDirectory $Output -Version $Version -Commit ('1' * 40) -Compiler 'synthetic packaging fixture'
}
try {
    $Fixture = New-WindowsPackagingFixture $Temp
    $Pe = $Fixture.pe
    $ExpectedCount = $Fixture.plan.files.Count + $Fixture.plan.binaries.Count + 2
    foreach ($Folder in $Fixture.plan.directories) { $ExpectedCount += @(Get-ChildItem -LiteralPath (Join-Path $Repo $Folder.source) -Filter $Folder.pattern -File).Count }
    $Result = Package '0.1.0-test'
    $Unpack = Join-Path $Temp 'relocated space'
    [IO.Compression.ZipFile]::ExtractToDirectory($Result.archive, $Unpack)
    $Verified = Test-WindowsPayload $Unpack
    if ($Verified.files.Count -ne $ExpectedCount) { throw 'Template directory was not packaged completely' }
    $HostRegistry = Join-Path $Unpack ((($Verified.binaries | Where-Object role -EQ 'host').templates_directory) + '/variable_registry.json')
    $EmulatorRegistry = Join-Path $Unpack ((($Verified.binaries | Where-Object role -EQ 'emulator').templates_directory) + '/variable_registry.json')
    if ((Get-FileHash $HostRegistry).Hash -cne (Get-FileHash (Join-Path $Repo 'templates/variable_registry.json')).Hash -or
        (Get-FileHash $EmulatorRegistry).Hash -cne (Get-FileHash (Join-Path $Repo 'iCemEmulator/templates/variable_registry.json')).Hash -or
        $HostRegistry -ceq $EmulatorRegistry) { throw 'Host/emulator canonical registries were merged or substituted' }
    # The installer is rendered from the already verified inventory, including
    # the integrity manifest itself. Adding a payload file cannot miss the MSI.
    $Numeric = Package '0.1.0'
    $NumericPayload = Join-Path $Temp 'msi payload'
    [IO.Compression.ZipFile]::ExtractToDirectory($Numeric.archive, $NumericPayload)
    $Wxs = Join-Path $Temp 'Package.wxs'
    if ((Write-WindowsInstallerSource $NumericPayload '0.1.0' $Wxs) -ne ($ExpectedCount + 1)) { throw 'MSI omitted a payload file' }
    $Xml = [xml](Get-Content -Raw -LiteralPath $Wxs)
    $Ns = [Xml.XmlNamespaceManager]::new($Xml.NameTable)
    $Ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
    if ($Xml.SelectNodes('//w:WixVariable[@Id="WixUIBannerBmp" or @Id="WixUIDialogBmp"]', $Ns).Count -ne 2 -or
        $Xml.SelectNodes('//w:Dialog[@Id="IcemRunningCheckDlg" or @Id="OptionsDlg"]', $Ns).Count -ne 2 -or
        $Xml.SelectSingleNode('//w:StandardDirectory[@Id="CommonAppDataFolder"]//w:Directory[@Id="MONITORROOT"]', $Ns).Name -ne 'DataMonitoring' -or
        $Xml.SelectSingleNode('//w:Shortcut[@Id="DesktopMonitorShortcut"]', $Ns).Target -cne '[MONITORROOT]icem-shell-dev.exe' -or
        $Xml.SelectNodes('//w:Component[@Id="Cmp.DesktopShortcut"]/w:CreateFolder', $Ns).Count -ne 1 -or
        $Xml.SelectSingleNode('//w:RegistryValue[@Name="iCemDataMonitorHost"]', $Ns).Value -cne '"[MONITORROOT]icem-host-dev.exe" --autostart' -or
        $Xml.SelectSingleNode('//w:SetProperty[@Id="WipeMonitorRoot"]', $Ns).Value -cne '[MONITORROOT]' -or
        $Xml.SelectSingleNode('//w:UpgradeVersion[@Property="LEGACY_MONITOR_PRESENT"]', $Ns).OnlyDetect -ne 'yes') { throw 'Legacy UI/paths/options/ownership projection failed' }
    if ($Xml.SelectSingleNode('//w:MajorUpgrade', $Ns).Schedule -ne 'afterInstallInitialize' -or
        $Xml.SelectSingleNode('//w:Package', $Ns).Scope -ne 'perMachine') { throw 'MSI transaction policy failed' }
    $Ids = @($Xml.SelectNodes('//w:Component', $Ns) | ForEach-Object { $_.Id })
    $Relocated = Join-Path $Temp 'relocated msi payload'
    Copy-Item -LiteralPath $NumericPayload -Destination $Relocated -Recurse
    $null = Write-WindowsInstallerSource $Relocated '0.1.0' $Wxs
    $RelocatedXml = [xml](Get-Content -Raw -LiteralPath $Wxs)
    if (($Ids -join ',') -cne (($RelocatedXml.SelectNodes('//w:Component', $Ns) | ForEach-Object { $_.Id }) -join ',')) { throw 'MSI component identity depends on build directory' }
    Assert-Rejected { Write-WindowsInstallerSource $NumericPayload '0.1.1' $Wxs } 'must match'
    foreach ($BadVersion in @('0.1.0-dev', '01.0.0', '256.0.0', '0.256.0', '0.1.65536', '1.2', '1.2.3.4')) {
        Assert-Rejected { Assert-WindowsMsiVersion $BadVersion } 'MSI version'
    }
    if ((Select-WindowsReleaseVersion 'next' @()) -cne '0.1.0' -or
        (Select-WindowsReleaseVersion 'next' @('v99.0.0', 'rust-v0.1.9', 'refs/tags/rust-v0.1.10')) -cne '0.1.11' -or
        (Select-WindowsReleaseVersion '0.2.0' @('rust-v0.1.10')) -cne '0.2.0') { throw 'Rust N+1 version selection failed' }
    Assert-Rejected { Select-WindowsReleaseVersion '0.1.10' @('rust-v0.1.10') } 'must exceed'
    Assert-Rejected { Select-WindowsReleaseVersion '0.1.9' @('rust-v0.1.10') } 'must exceed'
    Assert-Rejected { Get-NextWindowsMsiVersion '0.1.65535' } 'Patch version exhausted'
    if ((Get-Content -Raw -LiteralPath $Result.checksum).Split(' ')[0] -ne (Get-FileHash -LiteralPath $Result.archive).Hash.ToLowerInvariant()) { throw 'Bad ZIP checksum' }
    Assert-Rejected { Package '0.1.0-test' } 'already exists'
    [IO.File]::WriteAllText((Join-Path $Unpack 'assets/iCemDataMonitor.ico'), 'tampered')
    Assert-Rejected { Test-WindowsPayload $Unpack } 'hash/size mismatch'
    Copy-Item -LiteralPath (Join-Path $Repo 'rust/assets/iCemDataMonitor.ico') -Destination (Join-Path $Unpack 'assets/iCemDataMonitor.ico') -Force
    [IO.File]::WriteAllText((Join-Path $Unpack 'extra.txt'), 'unexpected')
    Assert-Rejected { Test-WindowsPayload $Unpack } 'Unexpected payload file count'
    Remove-Item -LiteralPath (Join-Path $Unpack 'extra.txt')
    Remove-Item -LiteralPath (Join-Path $Unpack 'icem-host-dev.exe')
    Assert-Rejected { Test-WindowsPayload $Unpack } 'Missing payload file'
    $AddinManifestPath = Join-Path $Fixture.addin 'addin-manifest.json'
    $Provenance = Get-Content -Raw -LiteralPath $AddinManifestPath
    $BadProvenance = $Provenance | ConvertFrom-Json
    $BadProvenance.sources[0].sha256 = '0' * 64
    [IO.File]::WriteAllText($AddinManifestPath, ($BadProvenance | ConvertTo-Json -Depth 8))
    Assert-Rejected { Test-LegacyAddinBundle $Fixture.addin $Repo } 'AddIn source mismatch'
    [IO.File]::WriteAllText($AddinManifestPath, $Provenance)
    $BadProvenance = $Provenance | ConvertFrom-Json
    $BadProvenance.framework = '.NETCoreApp,Version=v10.0'
    [IO.File]::WriteAllText($AddinManifestPath, ($BadProvenance | ConvertTo-Json -Depth 8))
    Assert-Rejected { Test-LegacyAddinBundle $Fixture.addin $Repo } 'Invalid AddIn provenance'
    [IO.File]::WriteAllText($AddinManifestPath, $Provenance)
    [IO.File]::WriteAllText((Join-Path $Fixture.addin 'vendor.dll'), 'must not redistribute SDK')
    Assert-Rejected { Test-LegacyAddinBundle $Fixture.addin $Repo } 'exactly DLL and provenance'
    Remove-Item -LiteralPath (Join-Path $Fixture.addin 'vendor.dll')
    $Adapter = New-WindowsInstallerAssets $Repo (Join-Path $Temp 'ui-work') $Verified
    if ((Get-FileHash (Join-Path $Adapter 'assets/Burservis_logo_cyr_red.png')).Hash -cne (Get-FileHash (Join-Path $Repo 'installer/assets/Burservis_logo_cyr_red.png')).Hash) { throw 'Original Burservis logo changed' }
    $Js = Get-Content -Raw (Join-Path $Adapter 'assets/CheckConflicts.js')
    foreach ($Binary in $Verified.binaries) { if (-not $Js.Contains($Binary.destination)) { throw 'Missing adapted conflict process name' } }
    $PlanPath = Join-Path $Repo 'rust/installer/windows-payload.json'
    $Original = Get-Content -Raw -LiteralPath $PlanPath
    foreach ($Bad in @('../escape.ico', '/absolute.ico', 'C:\escape.ico', 'assets//empty.ico', 'release-manifest.json')) {
        $Plan = $Original | ConvertFrom-Json
        $Plan.files[0].destination = $Bad
        [IO.File]::WriteAllText($PlanPath, ($Plan | ConvertTo-Json -Depth 8))
        Assert-Rejected { Package '0.1.1-test' } $(if ($Bad -eq 'release-manifest.json') { 'reserved payload path' } else { 'relative payload path' })
    }
    $Plan = $Original | ConvertFrom-Json
    $Plan.files += $Plan.files[0]
    [IO.File]::WriteAllText($PlanPath, ($Plan | ConvertTo-Json -Depth 8))
    Assert-Rejected { Package '0.1.1-test' } 'Duplicate/reserved payload path'
    [IO.File]::WriteAllText($PlanPath, $Original)
    $Pe[68] = 0x4C; $Pe[69] = 1 # reject x86 rather than pretending Windows x64
    [IO.File]::WriteAllBytes((Join-Path $Bins 'icem-host-dev.exe'), $Pe)
    Assert-Rejected { Package '0.1.1-test' } 'AMD64 PE'
    [IO.File]::WriteAllText((Join-Path $Bins 'icem-host-dev.exe'), 'ELF is not a Windows executable')
    Assert-Rejected { Package '0.1.1-test' } 'Not a Windows PE'
    [IO.File]::WriteAllBytes((Join-Path $Bins 'icem-host-dev.exe'), [byte[]]@(0x4D))
    Assert-Rejected { Package '0.1.1-test' } 'Not a Windows PE'
    [IO.File]::WriteAllBytes((Join-Path $Bins 'icem-host-dev.exe'), [byte[]]::new(128))
    Assert-Rejected { Package '0.1.1-test' } 'Not a Windows PE'
    if (@(Get-ChildItem -LiteralPath $Output -Filter '.package-*' -Force).Count) { throw 'Failed packaging left staging directories' }
    if (@(Get-ChildItem -LiteralPath $Output -Filter '*0.1.1*').Count) { throw 'Failed packaging published an archive' }
    Write-Host 'PASS Windows packaging: ZIP roundtrip/completeness/checksums, no overwrite, tamper/missing/extra files, path escape/duplicates, PE architecture, failure cleanup; full AddIn inventory, projected legacy UI/options/paths, stable option components and N+1 version selection.'
} finally { if (Test-Path -LiteralPath $Temp) { Remove-Item -LiteralPath $Temp -Recurse -Force } }
