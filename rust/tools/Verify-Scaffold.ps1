[CmdletBinding()]
param([switch] $RequireCompleteBaseline, [switch] $OrdinalIcu26100)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$IdentityFeatures = if ($OrdinalIcu26100) { @('--features', 'icem-emulation/ordinal-icu-26100') } else { @() }
Push-Location $Root
try {
    & (Join-Path $PSScriptRoot 'Verify-ScaffoldInputs.ps1') -RequireCompleteBaseline:$RequireCompleteBaseline
    & cargo check --release --workspace --all-targets --locked --offline @IdentityFeatures
    if ($LASTEXITCODE -ne 0) { throw 'cargo check failed' }
    & cargo build --release --workspace --locked --offline @IdentityFeatures
    if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }
    & cargo test --release --workspace --locked --offline @IdentityFeatures
    if ($LASTEXITCODE -ne 0) { throw 'cargo tests failed' }
    & cargo clippy --release --workspace --all-targets --locked --offline @IdentityFeatures -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw 'clippy failed' }
} finally { Pop-Location }
