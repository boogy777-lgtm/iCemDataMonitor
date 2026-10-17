[CmdletBinding()]
param([switch] $RequireComplete)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$SourceRoot = Split-Path $Root -Parent
$Manifest = Get-Content -Raw (Join-Path $Root 'migration/source-manifest.json') | ConvertFrom-Json
# Keep the upstream manifest immutable. Approved legacy maintenance has a separate,
# exact-path ledger; it cannot override frozen protocol, icon or SPA checks.
$Ledger = Get-Content -Raw (Join-Path $Root 'migration/legacy-divergences.json') | ConvertFrom-Json
if ($Ledger.schema_version -ne 1 -or $Ledger.source_commit -cne $Manifest.source_commit) {
    throw 'Legacy divergence ledger schema/source commit mismatch'
}
$Baseline = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($Item in $Manifest.files) {
    if (-not $Baseline.TryAdd($Item.path, $Item)) { throw "Duplicate baseline path: $($Item.path)" }
}
$Approved = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($Change in $Ledger.changes) {
    if (-not $Baseline.ContainsKey($Change.path)) { throw "Divergence outside baseline: $($Change.path)" }
    if ($Change.path -match '(?i)(\.proto$|^assets/|^iCemDataMonitor.Host/wwwroot/)') {
        throw "Frozen surface cannot be overridden: $($Change.path)"
    }
    if (-not $Approved.TryAdd($Change.path, $Change)) { throw "Duplicate divergence path: $($Change.path)" }
    if ($Change.baseline_blob_sha1 -cne $Baseline[$Change.path].git_blob_sha1 -or
        $Change.approved_blob_sha1 -cnotmatch '^[0-9a-f]{40}$' -or
        $Change.approved_blob_sha1 -ceq $Change.baseline_blob_sha1) {
        throw "Invalid divergence hashes: $($Change.path)"
    }
    $PullPrefix = 'https://github.com/' + $Manifest.source_repository + '/pull/'
    if ($Change.provenance.commit -cnotmatch '^[0-9a-f]{40}$' -or
        $Change.provenance.pull_request -cnotmatch ('^' + [regex]::Escape($PullPrefix) + '[1-9][0-9]*$') -or
        [string]::IsNullOrWhiteSpace($Change.provenance.reason)) {
        throw "Missing divergence provenance: $($Change.path)"
    }
}
function Get-CanonicalHash([string] $FilePath, [string] $Relative) {
    $Hash = & git -C $SourceRoot hash-object "--path=$Relative" -- $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Cannot hash $Relative" }
    return $Hash.Trim()
}
$Missing = @()
$Verified = 0
foreach ($Item in $Manifest.files) {
    $ExpectedHash = if ($Approved.ContainsKey($Item.path)) { $Approved[$Item.path].approved_blob_sha1 } else { $Item.git_blob_sha1 }
    $Path = Join-Path $SourceRoot $Item.path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $Missing += $Item.path; continue }
    $Hash = & git hash-object --no-filters -- $Path
    if ($LASTEXITCODE -ne 0) { throw "git hash-object failed: $($Item.path)" }
    if ($Hash.Trim() -eq $ExpectedHash) {
        if (-not $Approved.ContainsKey($Item.path) -and (Get-Item -Force -LiteralPath $Path).Length -ne $Item.size) { throw "Size mismatch: $($Item.path)" }
    } else {
        # The preserved upstream .gitattributes uses CRLF for .ps1 checkout.
        # Compare its canonical Git blob too; never normalize binary fixtures.
        $Relative = $Item.path
        $CanonicalHash = & git -C $SourceRoot hash-object "--path=$Relative" -- $Path
        if ($LASTEXITCODE -ne 0 -or $CanonicalHash.Trim() -ne $ExpectedHash) {
            throw "Hash mismatch: $($Item.path)"
        }
    }
    $Verified++
}
$OriginalRelative = 'iCemDataMonitor.Contracts/Protos/telemetry.proto'
$Original = Join-Path $SourceRoot $OriginalRelative
$ProtoRecord = @($Manifest.files | Where-Object path -eq $OriginalRelative)
if ($ProtoRecord.Count -ne 1 -or (Get-CanonicalHash $Original $OriginalRelative) -ne $ProtoRecord[0].git_blob_sha1) {
    throw 'Canonical frozen proto changed'
}
$ProtoFiles = @(Get-ChildItem -LiteralPath $SourceRoot -Filter '*.proto' -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' })
if ($ProtoFiles.Count -ne 1 -or $ProtoFiles[0].FullName -ne $Original) { throw 'Expected exactly one canonical proto' }
foreach ($Asset in @('iCemDataMonitor.ico', 'iCemEmule.ico')) {
    if ((Get-FileHash (Join-Path $Root "assets/$Asset")).Hash -ne (Get-FileHash (Join-Path $SourceRoot "assets/$Asset")).Hash) {
        throw "Original icon changed: $Asset"
    }
}
$Dashboard = Join-Path $Root 'web/dashboard'
Get-ChildItem -File -Recurse $Dashboard | ForEach-Object {
    $Relative = [IO.Path]::GetRelativePath($Dashboard, $_.FullName)
    $Source = Join-Path $SourceRoot ('iCemDataMonitor.Host/wwwroot/' + $Relative)
    # The original tree uses text=auto, while the frozen Rust SPA uses -text.
    # Compare canonical Git bytes; Windows checkout newline conversion is not a source change.
    $RustRelative = 'rust/web/dashboard/' + $Relative.Replace('\', '/')
    $SourceRelative = 'iCemDataMonitor.Host/wwwroot/' + $Relative.Replace('\', '/')
    if ($Relative -eq 'index.html') {
        # D16: the only allowed SPA edits are the D16 bool and D17 valid trailer spacing.
        # Source hash was checked above; compare the complete transformed text.
        $Legacy = 'var light = document.documentElement.getAttribute("data-theme") !== "light";'
        $Fixed = 'var light = document.documentElement.getAttribute("data-theme") === "light";'
        $OriginalHtml = [IO.File]::ReadAllText($Source).Replace("`r`n", "`n")
        if ([regex]::Matches($OriginalHtml, [regex]::Escape($Legacy)).Count -ne 1) {
            throw 'D16 source predicate drift'
        }
        $LegacyTrailer = 'if (trailers.indexOf("grpc-status: 0") === -1)'
        $FixedTrailer = 'if (!/(?:^|\r\n)grpc-status:[ \t]*0(?:\r\n|$)/.test(trailers))'
        if ([regex]::Matches($OriginalHtml, [regex]::Escape($LegacyTrailer)).Count -ne 1) {
            throw 'D17 source trailer predicate drift'
        }
        $ExpectedHtml = $OriginalHtml.Replace($Legacy, $Fixed).Replace($LegacyTrailer, $FixedTrailer)
        if ($ExpectedHtml -cne [IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n")) {
            throw 'SPA index.html changed beyond D16/D17 predicates'
        }
    } elseif ((Get-CanonicalHash $_.FullName $RustRelative) -ne (Get-CanonicalHash $Source $SourceRelative)) {
        throw "Original SPA changed: $Relative"
    }
}
$Schema = Get-Content -Raw (Join-Path $Root 'contracts/http/openapi.json') | ConvertFrom-Json
$Inventory = Get-Content -Raw (Join-Path $Root 'contracts/http/route-inventory.json') | ConvertFrom-Json
$Expected = @($Inventory.routes | Where-Object status -eq 'active' | Select-Object -ExpandProperty path | Sort-Object)
$Actual = @($Schema.paths.PSObject.Properties.Name | Sort-Object)
if (($Expected -join '|') -ne ($Actual -join '|')) { throw 'Active REST inventory drift' }
if ($Missing.Count -gt 0) {
    Write-Warning ("Missing {0} baseline file(s): {1}" -f $Missing.Count, ($Missing -join ', '))
    if ($RequireComplete) { throw 'Full source copy is required before publication' }
}
Write-Host "Baseline: $Verified verified of $($Manifest.files.Count); $($Approved.Count) exact approved legacy divergences; frozen proto/assets, D16/D17 theme predicates and REST inventory OK."
