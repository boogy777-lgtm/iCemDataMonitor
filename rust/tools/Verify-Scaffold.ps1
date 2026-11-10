[CmdletBinding()]
param([switch] $RequireCompleteBaseline)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
Push-Location $Root
try {
    & (Join-Path $PSScriptRoot 'Verify-ScaffoldInputs.ps1') -RequireCompleteBaseline:$RequireCompleteBaseline
    & cargo check --release --workspace --all-targets --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'cargo check failed' }
    & cargo build --release --workspace --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }
    & cargo test --release --workspace --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'cargo tests failed' }
    & cargo clippy --release --workspace --all-targets --locked --offline -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw 'clippy failed' }
} finally { Pop-Location }
