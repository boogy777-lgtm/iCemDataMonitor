#requires -Version 7.4
# Build/read-only contracts. Never installs, repairs or removes any MSI product.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows Setup contracts require Windows.' }
$Repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$Work = Join-Path ([IO.Path]::GetTempPath()) ('icem-setup-contract-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($Work) | Out-Null
$Rust = Get-Content -Raw -LiteralPath (Join-Path $Repo 'rust/installer/windows-installer.json') | ConvertFrom-Json
$Legacy = [xml](Get-Content -Raw -LiteralPath (Join-Path $Repo 'dotnet/installer/Product.wxs'))
$Families = [ordered]@{
    '.NET' = [guid]$Legacy.SelectSingleNode('/*[local-name()="Wix"]/*[local-name()="Package"]').GetAttribute('UpgradeCode')
    'Rust' = [guid]$Rust.upgrade_code
    'unsupported' = [guid]::NewGuid()
}
function Assert-Rejected([scriptblock] $Operation, [string] $Message) {
    try { & $Operation | Out-Null }
    catch {
        if (-not $_.Exception.Message.Contains($Message)) { throw }
        return
    }
    throw "Expected Setup rejection: $Message"
}
try {
    Push-Location (Join-Path $Repo 'shared/installer')
    try {
        & dotnet tool restore --tool-manifest .config/dotnet-tools.json | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Pinned WiX tool restore failed.' }
        foreach ($Family in $Families.Keys) {
            $Build = Join-Path $Work $Family
            [IO.Directory]::CreateDirectory($Build) | Out-Null
            $Source = Join-Path $Build 'TEST-ONLY.wxs'
            $Msi = Join-Path $Build 'TEST-ONLY.msi'
            $Token = [guid]::NewGuid().ToString('N')
            # Actual family code, random product/component/key, no application bytes.
            # MSI is only compiled and embedded, never passed to Windows Installer.
            [IO.File]::WriteAllText($Source, @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="TEST ONLY iCem Setup fixture $Family" Manufacturer="Installer contract fixture"
           Version="0.0.1" UpgradeCode="$($Families[$Family])" Scope="perUser">
    <StandardDirectory Id="LocalAppDataFolder">
      <Component Id="Fixture" Guid="$([guid]::NewGuid())" Bitness="always64">
        <RegistryValue Root="HKCU" Key="Software\iCemSetupBuildFixture\$Token" Name="Marker" Value="TEST ONLY" Type="string" KeyPath="yes" />
      </Component>
    </StandardDirectory>
    <Feature Id="Main"><ComponentRef Id="Fixture" /></Feature>
  </Package>
</Wix>
"@)
            & dotnet tool run wix -- build $Source -arch x64 -wx -o $Msi | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Build-only MSI fixture compilation failed.' }
            $Checksum = (Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash.ToLowerInvariant() + '  TEST-ONLY.msi'
            [IO.File]::WriteAllText("$Msi.sha256", $Checksum + "`n")
            if ($Family -ceq 'unsupported') {
                Assert-Rejected { & (Join-Path $PSScriptRoot 'Build-WindowsSetup.ps1') -Msi $Msi } 'MSI does not belong to a supported'
                if (@(Get-ChildItem -LiteralPath $Build -Filter '*-Setup.exe*').Count) { throw 'Unsupported family published a Setup.' }
                continue
            }
            $Setup = & (Join-Path $PSScriptRoot 'Build-WindowsSetup.ps1') -Msi $Msi
            $Expected = Join-Path $Build 'TEST-ONLY-Setup.exe'
            if ($Setup.setup -cne $Expected -or
                (Get-Content -Raw -LiteralPath $Setup.checksum).Trim() -cne ((Get-FileHash -LiteralPath $Expected -Algorithm SHA256).Hash.ToLowerInvariant() + '  TEST-ONLY-Setup.exe')) {
                throw 'Setup delivery name or checksum mismatch.'
            }
            Assert-Rejected { & (Join-Path $PSScriptRoot 'Build-WindowsSetup.ps1') -Msi $Msi } 'Setup already exists'
            [IO.File]::WriteAllText("$Msi.sha256", ('0' * 64) + '  TEST-ONLY.msi')
            Assert-Rejected { & (Join-Path $PSScriptRoot 'Build-WindowsSetup.ps1') -Msi $Msi -OutputDirectory (Join-Path $Build 'tampered') } 'MSI checksum mismatch'
        }
    } finally { Pop-Location }
    Write-Host 'PASS: separate .NET/Rust Setup builds, exact embedded MSI/family, both removal policies, no overwrite, tamper and unsupported family rejection. No MSI installation/uninstallation executed; fixtures are not releases.'
} finally {
    if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
}
