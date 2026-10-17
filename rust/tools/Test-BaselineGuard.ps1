[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$Guard = Join-Path $PSScriptRoot 'Verify-Baseline.ps1'
$Temp = Join-Path ([IO.Path]::GetTempPath()) ('icem-baseline-tests-' + [guid]::NewGuid().ToString('N'))
$Fixture = Join-Path $Temp 'repo'
function Write-Fixture([string] $Path, [string] $Text) {
    $Target = Join-Path $Fixture $Path
    [IO.Directory]::CreateDirectory((Split-Path $Target -Parent)) | Out-Null
    [IO.File]::WriteAllText($Target, $Text)
}
function Hash-Fixture([string] $Path) {
    $Result = & git hash-object --no-filters -- (Join-Path $Fixture $Path)
    if ($LASTEXITCODE -ne 0) { throw "Cannot hash fixture $Path" }
    return $Result.Trim()
}
function Save-Ledger {
    Write-Fixture 'rust/migration/legacy-divergences.json' ($script:Ledger | ConvertTo-Json -Depth 8)
}
function New-Fixture {
    if (Test-Path $Fixture) { Remove-Item -LiteralPath $Fixture -Recurse -Force }
    [IO.Directory]::CreateDirectory($Fixture) | Out-Null
    & git -C $Fixture init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize test repository' }
    Write-Fixture '.gitattributes' "*.ps1 text eol=crlf`n"
    Write-Fixture 'legacy.cs' "baseline`n"
    Write-Fixture 'legacy.ps1' "# baseline`n"
    Write-Fixture 'iCemDataMonitor.Contracts/Protos/telemetry.proto' "syntax = `"proto3`";`n"
    foreach ($Name in @('iCemDataMonitor.ico', 'iCemEmule.ico')) {
        Write-Fixture "assets/$Name" 'synthetic icon bytes for guard test'
        Write-Fixture "rust/assets/$Name" 'synthetic icon bytes for guard test'
    }
    $Html = 'var light = document.documentElement.getAttribute("data-theme") !== "light";' + "`n" +
        'if (trailers.indexOf("grpc-status: 0") === -1)'
    Write-Fixture 'iCemDataMonitor.Host/wwwroot/index.html' $Html
    Write-Fixture 'rust/web/dashboard/index.html' ($Html.Replace('!== "light"', '=== "light"').Replace(
        'if (trailers.indexOf("grpc-status: 0") === -1)', 'if (!/(?:^|\r\n)grpc-status:[ \t]*0(?:\r\n|$)/.test(trailers))'))
    Write-Fixture 'iCemDataMonitor.Host/wwwroot/main.css' 'body {}'
    Write-Fixture 'rust/web/dashboard/main.css' 'body {}'
    Write-Fixture 'rust/contracts/http/openapi.json' '{"paths":{"/active":{}}}'
    Write-Fixture 'rust/contracts/http/route-inventory.json' '{"routes":[{"path":"/active","status":"active"}]}'
    $Files = @('.gitattributes', 'legacy.cs', 'legacy.ps1', 'iCemDataMonitor.Contracts/Protos/telemetry.proto',
        'assets/iCemDataMonitor.ico', 'assets/iCemEmule.ico', 'iCemDataMonitor.Host/wwwroot/index.html',
        'iCemDataMonitor.Host/wwwroot/main.css') | ForEach-Object {
            @{ path = $_; git_blob_sha1 = Hash-Fixture $_; size = (Get-Item -Force (Join-Path $Fixture $_)).Length }
        }
    $Commit = '1' * 40
    Write-Fixture 'rust/migration/source-manifest.json' (@{
        source_repository = 'test/repo'; source_commit = $Commit; files = @($Files)
    } | ConvertTo-Json -Depth 8)
    Write-Fixture 'legacy.cs' "approved maintenance`n"
    $script:Ledger = @{
        schema_version = 1; source_commit = $Commit
        changes = @(@{
            path = 'legacy.cs'; baseline_blob_sha1 = ($Files | Where-Object path -eq 'legacy.cs').git_blob_sha1
            approved_blob_sha1 = Hash-Fixture 'legacy.cs'
            provenance = @{ pull_request = 'https://github.com/test/repo/pull/2'; commit = '2' * 40; reason = 'Reviewed maintenance' }
        })
    }
    Save-Ledger
    [IO.Directory]::CreateDirectory((Join-Path $Fixture 'rust/tools')) | Out-Null
    Copy-Item -LiteralPath $Guard -Destination (Join-Path $Fixture 'rust/tools/Verify-Baseline.ps1')
}
function Check-Case([string] $Name, [scriptblock] $Mutate, [string] $ExpectedError = '') {
    New-Fixture
    & $Mutate
    $Failure = $null
    try { & (Join-Path $Fixture 'rust/tools/Verify-Baseline.ps1') -RequireComplete }
    catch { $Failure = $_.Exception.Message }
    if ($ExpectedError) {
        if (-not $Failure -or -not $Failure.Contains($ExpectedError)) { throw "$Name expected '$ExpectedError', got '$Failure'" }
    } elseif ($Failure) { throw "$Name unexpectedly failed: $Failure" }
    Write-Host "PASS $Name"
}
try {
    Check-Case 'exact approval and complete baseline' {}
    Check-Case 'canonical PowerShell CRLF' { Write-Fixture 'legacy.ps1' "# baseline`r`n" }
    Check-Case 'next change to approved path' { Write-Fixture 'legacy.cs' 'unreviewed' } 'Hash mismatch: legacy.cs'
    Check-Case 'revert of accepted maintenance' { Write-Fixture 'legacy.cs' "baseline`n" } 'Hash mismatch: legacy.cs'
    Check-Case 'unlisted legacy change' { Write-Fixture 'legacy.ps1' '# unreviewed' } 'Hash mismatch: legacy.ps1'
    Check-Case 'missing baseline file' { Remove-Item (Join-Path $Fixture 'legacy.cs') } 'Full source copy is required'
    Check-Case 'missing approval' { $script:Ledger.changes = @(); Save-Ledger } 'Hash mismatch: legacy.cs'
    Check-Case 'wrong upstream commit' { $script:Ledger.source_commit = '3' * 40; Save-Ledger } 'schema/source commit mismatch'
    Check-Case 'unknown ledger schema' { $script:Ledger.schema_version = 2; Save-Ledger } 'schema/source commit mismatch'
    Check-Case 'wrong original hash' { $script:Ledger.changes[0].baseline_blob_sha1 = '3' * 40; Save-Ledger } 'Invalid divergence hashes'
    Check-Case 'invalid approved hash' { $script:Ledger.changes[0].approved_blob_sha1 = 'bad'; Save-Ledger } 'Invalid divergence hashes'
    Check-Case 'duplicate approval' { $script:Ledger.changes += $script:Ledger.changes[0]; Save-Ledger } 'Duplicate divergence path'
    Check-Case 'outside baseline' { $script:Ledger.changes[0].path = '../outside'; Save-Ledger } 'Divergence outside baseline'
    Check-Case 'missing reason' { $script:Ledger.changes[0].provenance.reason = ''; Save-Ledger } 'Missing divergence provenance'
    Check-Case 'missing commit' { $script:Ledger.changes[0].provenance.commit = ''; Save-Ledger } 'Missing divergence provenance'
    Check-Case 'unrelated PR' { $script:Ledger.changes[0].provenance.pull_request = 'https://github.com/other/repo/pull/2'; Save-Ledger } 'Missing divergence provenance'
    foreach ($Protected in @('iCemDataMonitor.Contracts/Protos/telemetry.proto', 'assets/iCemDataMonitor.ico', 'iCemDataMonitor.Host/wwwroot/main.css')) {
        Check-Case "cannot approve frozen $Protected" { $script:Ledger.changes[0].path = $Protected; Save-Ledger } 'Frozen surface cannot be overridden'
    }
    Check-Case 'canonical proto drift' { Write-Fixture 'iCemDataMonitor.Contracts/Protos/telemetry.proto' 'drift' } 'Hash mismatch:'
    Check-Case 'second proto rejected' { Write-Fixture 'rust/duplicate.proto' 'synthetic duplicate' } 'Expected exactly one canonical proto'
    Check-Case 'copied icon drift' { Write-Fixture 'rust/assets/iCemDataMonitor.ico' 'drift' } 'Original icon changed'
    Check-Case 'SPA outside approved predicates' { Write-Fixture 'rust/web/dashboard/index.html' 'drift' } 'SPA index.html changed beyond'
    Check-Case 'SPA stylesheet drift' { Write-Fixture 'rust/web/dashboard/main.css' 'drift' } 'Original SPA changed'
    Check-Case 'REST inventory drift' { Write-Fixture 'rust/contracts/http/openapi.json' '{"paths":{"/dormant":{}}}' } 'Active REST inventory drift'
    Write-Host 'Baseline guard regression suite passed (25 cases).'
} finally {
    if (Test-Path $Temp) { Remove-Item -LiteralPath $Temp -Recurse -Force }
}
