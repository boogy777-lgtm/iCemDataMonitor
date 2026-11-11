#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string] $Version,
    [string] $OutputDirectory,
    [string] $AddinDirectory,
    [ValidateSet('icu-19045', 'icu-26100', IgnoreCase = $false)]
    [string] $OrdinalProvider = 'icu-19045'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem) { throw 'Run with PowerShell 7 on Windows x64 and MSVC Build Tools.' }
$Root = Split-Path $PSScriptRoot -Parent
$RepositoryRoot = Split-Path $Root -Parent
Import-Module (Join-Path $Root 'installer/WindowsPayload.psm1') -Force
Import-Module (Join-Path $Root 'installer/WindowsInstaller.psm1') -Force
Import-Module (Join-Path $RepositoryRoot 'shared/icem-addin/tools/LegacyAddin.psm1') -Force
Assert-WindowsMsiVersion $Version
if (-not $OutputDirectory) { $OutputDirectory = 'dist/rust' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $RepositoryRoot)
$Plan = Read-WindowsPayloadPlan $RepositoryRoot
$SavedFlags = [Environment]::GetEnvironmentVariable('CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS')
$AddinWork = $null
Push-Location $Root
try {
    $Commit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Release requires a Git checkout.' }
    $Dirty = & git status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $Dirty) { throw 'Commit/stash source changes before making a release.' }
    if ($env:RUSTFLAGS -or $env:CARGO_ENCODED_RUSTFLAGS -or $SavedFlags) {
        throw 'Release requires RUSTFLAGS, CARGO_ENCODED_RUSTFLAGS and target RUSTFLAGS to be unset; static CRT flags are owned by this script.'
    }
    if (-not $AddinDirectory) {
        $AddinWork = Join-Path ([IO.Path]::GetTempPath()) ('icem-release-addin-' + [guid]::NewGuid().ToString('N'))
        & (Join-Path $RepositoryRoot 'shared/icem-addin/tools/Build-LegacyAddin.ps1') -OutputDirectory $AddinWork
        $AddinDirectory = $AddinWork
    }
    $null = Test-LegacyAddinBundle $AddinDirectory $RepositoryRoot
    & rustup show
    if ($LASTEXITCODE -ne 0) { throw 'Pinned toolchain installation failed.' }
    & rustup target add $Plan.target
    if ($LASTEXITCODE -ne 0) { throw 'MSVC target installation failed.' }
    $Compiler = & rustc --version
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read rustc version.' }
    $Pin = [regex]::Match((Get-Content -Raw (Join-Path $Root 'rust-toolchain.toml')), '(?m)^channel\s*=\s*"(?<v>\d+\.\d+\.\d+)"').Groups['v'].Value
    if (-not $Pin -or $Compiler -notmatch ('^rustc ' + [regex]::Escape($Pin) + ' ')) { throw 'rustc does not match rust-toolchain.toml.' }
    & cargo fetch --locked --target $Plan.target
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency fetch failed.' }
    $Use26100 = switch -CaseSensitive ($OrdinalProvider) {
        'icu-19045' { $false }
        'icu-26100' { $true }
        default { throw 'Unsupported ordinal identity provider' }
    }
    Write-Host "Ordinal identity target: $OrdinalProvider"
    & (Join-Path $PSScriptRoot 'Verify-Scaffold.ps1') -RequireCompleteBaseline -OrdinalIcu26100:$Use26100
    $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS = '-C target-feature=+crt-static'
    $Build = @('build', '--release', '--locked', '--offline', '--target', $Plan.target, '--target-dir', (Join-Path $Root 'target'))
    if ($Use26100) { $Build += @('--features', 'icem-emulator-app/ordinal-icu-26100') }
    foreach ($Package in @($Plan.binaries.package | Sort-Object -Unique)) { $Build += @('--package', $Package) }
    foreach ($Binary in $Plan.binaries) { $Build += @('--bin', $Binary.bin) }
    & cargo @Build
    if ($LASTEXITCODE -ne 0) { throw 'Windows release compilation failed.' }
    $Built = Join-Path $Root "target/$($Plan.target)/release"
    $Result = New-WindowsArchive -RepositoryRoot $RepositoryRoot -BinaryDirectory $Built -AddinDirectory $AddinDirectory -OutputDirectory $OutputDirectory -Version $Version -Commit $Commit.Trim() -Compiler $Compiler -OrdinalProvider $OrdinalProvider
    Write-Host "Windows x64 archive: $($Result.archive)"
    Write-Host "SHA256: $($Result.sha256)"
    $Installer = & (Join-Path $PSScriptRoot 'Build-WindowsMsi.ps1') -Archive $Result.archive -OutputDirectory $OutputDirectory
    $Setup = & (Join-Path $RepositoryRoot 'shared/installer/tools/Build-WindowsSetup.ps1') -Msi $Installer.msi -OutputDirectory $OutputDirectory
    return [pscustomobject]@{ archive = $Result.archive; msi = $Installer.msi; checksum = $Installer.checksum; setup = $Setup.setup; setup_checksum = $Setup.checksum; version = $Version; ordinal_identity_provider = $OrdinalProvider }
} finally {
    [Environment]::SetEnvironmentVariable('CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS', $SavedFlags)
    if ($AddinWork -and (Test-Path -LiteralPath $AddinWork)) { Remove-Item -LiteralPath $AddinWork -Recurse -Force }
    Pop-Location
}
