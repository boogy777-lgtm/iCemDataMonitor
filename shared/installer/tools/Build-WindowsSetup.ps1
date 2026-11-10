#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Msi, [string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$Msi = (Resolve-Path -LiteralPath $Msi).Path
if (-not $IsWindows) { throw 'Windows setup requires Windows x64.' }
if (-not $OutputDirectory) { $OutputDirectory = Split-Path $Msi -Parent }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $Repo)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$Output = Join-Path $OutputDirectory ([IO.Path]::GetFileNameWithoutExtension($Msi) + '-Setup.exe')
if ((Test-Path -LiteralPath $Output) -or (Test-Path -LiteralPath "$Output.sha256")) { throw "Setup already exists: $Output" }
$Hash = (Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Content -Raw -LiteralPath "$Msi.sha256").Split(' ')[0] -ine $Hash) { throw 'MSI checksum mismatch' }
$Rust = Get-Content -Raw (Join-Path $Repo 'rust/installer/windows-installer.json') | ConvertFrom-Json
$Legacy = [xml](Get-Content -Raw (Join-Path $Repo 'installer/Product.wxs'))
$LegacyCode = $Legacy.SelectSingleNode('/*[local-name()="Wix"]/*[local-name()="Package"]').GetAttribute('UpgradeCode')
$Families = @{ '.NET' = [guid]$LegacyCode; 'Rust' = [guid]$Rust.upgrade_code }
$WindowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$Database = $View = $Record = $null
try {
    $Database = $WindowsInstaller.OpenDatabase($Msi, 0)
    $View = $Database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode''')
    $null = $View.Execute()
    $Record = $View.Fetch()
    if (-not $Record) { throw 'MSI has no UpgradeCode' }
    $PayloadCode = [guid]$Record.StringData(1)
    $PayloadFamilies = @($Families.Keys | Where-Object { $Families[$_] -eq $PayloadCode })
    if ($PayloadFamilies.Count -ne 1) { throw 'MSI does not belong to a supported iCem Monitor family' }
} finally {
    if ($View) { $null = $View.Close() }
    foreach ($Com in @($Record, $View, $Database, $WindowsInstaller)) {
        if ($Com) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($Com) }
    }
}
$Work = Join-Path ([IO.Path]::GetTempPath()) ('icem-setup-build-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($Work) | Out-Null
try {
    $Inventory = Join-Path $Work 'families.txt'
    $HashPath = Join-Path $Work 'payload.sha256'
    $FamilyPath = Join-Path $Work 'payload.family'
    [IO.File]::WriteAllText($Inventory, ".NET|$([guid]$LegacyCode)`nRust|$([guid]$Rust.upgrade_code)`n")
    [IO.File]::WriteAllText($HashPath, $Hash)
    [IO.File]::WriteAllText($FamilyPath, $PayloadFamilies[0])
    & dotnet build (Join-Path $Repo 'shared/installer/bootstrapper/Setup.csproj') -c Release "-p:PayloadPath=$Msi" "-p:InventoryPath=$Inventory" "-p:HashPath=$HashPath" "-p:FamilyPath=$FamilyPath" "-p:OutputPath=$Work/bin/" "-p:BaseIntermediateOutputPath=$Work/obj/" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Windows setup compilation failed' }
    $Built = Join-Path $Work 'bin/iCemDataMonitor-Setup.exe'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-WindowsSetup.ps1') -Setup $Built -ExpectedMsi $Msi -ExpectedFamily $PayloadFamilies[0] | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Windows setup checks failed' }
    $SetupHash = (Get-FileHash -LiteralPath $Built -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item -LiteralPath $Built -Destination $Output
    [IO.File]::WriteAllText("$Output.sha256", "$SetupHash  $([IO.Path]::GetFileName($Output))`n")
    Write-Host "Windows setup: $Output"
    return [pscustomobject]@{ setup = $Output; checksum = "$Output.sha256" }
} finally { if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force } }
