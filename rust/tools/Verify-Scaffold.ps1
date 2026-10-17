[CmdletBinding()]
param([switch] $RequireCompleteBaseline)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
Push-Location $Root
try {
    & (Join-Path $PSScriptRoot 'Test-BaselineGuard.ps1')
    & (Join-Path $PSScriptRoot 'Test-WindowsPackaging.ps1')
    & (Join-Path $PSScriptRoot 'Verify-Baseline.ps1') -RequireComplete:$RequireCompleteBaseline
    & (Join-Path $PSScriptRoot 'Verify-DesktopMapping.ps1')
    # Every workspace member must follow the pinned compiler/edition policy.
    # CI installs directly from rust-toolchain.toml, so no third version literal exists.
    $ToolchainText = Get-Content -Raw (Join-Path $Root 'rust-toolchain.toml')
    $PinMatch = [regex]::Match($ToolchainText, '(?m)^channel\s*=\s*"(?<version>\d+\.\d+\.\d+)"')
    if (-not $PinMatch.Success) { throw 'Pin an exact stable Rust version in rust-toolchain.toml' }
    $PinnedVersion = $PinMatch.Groups['version'].Value
    $MetadataJson = & cargo metadata --no-deps --format-version 1 --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'cargo metadata failed' }
    $Metadata = $MetadataJson | ConvertFrom-Json
    foreach ($Member in $Metadata.workspace_members) {
        $Package = $Metadata.packages | Where-Object id -eq $Member
        if ($Package.edition -ne '2024' -or $Package.rust_version -ne $PinnedVersion) {
            throw "Workspace toolchain/edition drift: $($Package.name)"
        }
    }
    & cargo fmt --all --check
    if ($LASTEXITCODE -ne 0) { throw 'rustfmt failed' }
    & cargo check --workspace --all-targets --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'cargo check failed' }
    & cargo build --workspace --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }
    & cargo test --workspace --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'cargo tests failed' }
    & cargo clippy --workspace --all-targets --locked --offline -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw 'clippy failed' }
} finally { Pop-Location }
