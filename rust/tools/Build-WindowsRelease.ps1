#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string] $Version,
    [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem) { throw 'Run with PowerShell 7 on Windows x64 and MSVC Build Tools.' }
$Root = Split-Path $PSScriptRoot -Parent
$RepositoryRoot = Split-Path $Root -Parent
Import-Module (Join-Path $Root 'installer/WindowsPayload.psm1') -Force
Import-Module (Join-Path $Root 'installer/WindowsInstaller.psm1') -Force
Assert-WindowsMsiVersion $Version
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $Root 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$Plan = Read-WindowsPayloadPlan $RepositoryRoot
$SavedFlags = [Environment]::GetEnvironmentVariable('CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS')
Push-Location $Root
try {
    $Commit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Release requires a Git checkout.' }
    $Dirty = & git status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $Dirty) { throw 'Commit/stash source changes before making a release.' }
    if ($env:RUSTFLAGS -or $env:CARGO_ENCODED_RUSTFLAGS -or $SavedFlags) {
        throw 'Release requires RUSTFLAGS, CARGO_ENCODED_RUSTFLAGS and target RUSTFLAGS to be unset; static CRT flags are owned by this script.'
    }
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
    & (Join-Path $PSScriptRoot 'Verify-Scaffold.ps1') -RequireCompleteBaseline
    $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS = '-C target-feature=+crt-static'
    $Build = @('build', '--release', '--locked', '--offline', '--target', $Plan.target, '--target-dir', (Join-Path $Root 'target'))
    foreach ($Package in @($Plan.binaries.package | Sort-Object -Unique)) { $Build += @('--package', $Package) }
    foreach ($Binary in $Plan.binaries) { $Build += @('--bin', $Binary.bin) }
    & cargo @Build
    if ($LASTEXITCODE -ne 0) { throw 'Windows release compilation failed.' }
    $Built = Join-Path $Root "target/$($Plan.target)/release"
    $Result = New-WindowsArchive -RepositoryRoot $RepositoryRoot -BinaryDirectory $Built -OutputDirectory $OutputDirectory -Version $Version -Commit $Commit.Trim() -Compiler $Compiler
    Write-Host "Windows x64 archive: $($Result.archive)"
    Write-Host "SHA256: $($Result.sha256)"
    $Installer = & (Join-Path $PSScriptRoot 'Build-WindowsMsi.ps1') -Archive $Result.archive -OutputDirectory $OutputDirectory
    return [pscustomobject]@{ archive = $Result.archive; msi = $Installer.msi; checksum = $Installer.checksum; version = $Version }
} finally {
    [Environment]::SetEnvironmentVariable('CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS', $SavedFlags)
    Pop-Location
}
