[CmdletBinding()]
param([switch] $RequireComplete)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$SourceRoot = Split-Path $Root -Parent
$Manifest = Get-Content -Raw (Join-Path $Root 'migration/source-manifest.json') | ConvertFrom-Json
Import-Module (Join-Path $PSScriptRoot 'BaselineMetadata.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'SourceRelocations.psm1') -Force
# Keep the upstream manifest immutable. Approved legacy maintenance has a separate,
# exact-path ledger; it cannot override frozen protocol, icon or SPA checks.
$LedgerText = [IO.File]::ReadAllText((Join-Path $Root 'migration/legacy-divergences.json'),
    [Text.UTF8Encoding]::new($false, $true))
$Json = [System.Text.Json.JsonDocument]::Parse($LedgerText)
try { Assert-UniqueJsonKeys $Json.RootElement 'Duplicate ledger JSON key' } finally { $Json.Dispose() }
$Ledger = ConvertFrom-Json -InputObject $LedgerText -AsHashtable
Assert-ExactFields $Ledger @('schema_version', 'source_commit', 'changes') 'Unknown or missing ledger fields'
if ($Ledger.schema_version -isnot [long] -or $Ledger.schema_version -ne 2 -or $Ledger.source_commit -isnot [string] -or $Ledger.source_commit -cne $Manifest.source_commit -or
    $Ledger.changes -isnot [array]) {
    throw 'Legacy divergence ledger schema/source commit mismatch'
}
$Baseline = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($Item in $Manifest.files) {
    if (-not (Test-CanonicalPath $Item.path)) { throw 'Noncanonical baseline path' }
    if (-not $Baseline.TryAdd($Item.path, $Item)) { throw "Duplicate baseline path: $($Item.path)" }
}
$Approved = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($Change in $Ledger.changes) {
    if ($Change.operation -cnotin @('modify', 'delete')) { throw 'Unknown divergence operation' }
    $Fields = @('path', 'operation', 'baseline_blob_sha1', 'provenance')
    if ($Change.operation -ceq 'modify') { $Fields += 'approved_blob_sha1' }
    Assert-ExactFields $Change $Fields 'Unknown or missing ledger fields'
    if (-not (Test-CanonicalPath $Change.path)) { throw 'Noncanonical divergence path' }
    if (-not $Baseline.ContainsKey($Change.path)) { throw "Divergence outside baseline: $($Change.path)" }
    if ($Change.path -match '(?i)(\.proto$|^assets/|^iCemDataMonitor.Host/wwwroot/)') {
        throw "Frozen surface cannot be overridden: $($Change.path)"
    }
    if (-not $Approved.TryAdd($Change.path, $Change)) { throw "Duplicate divergence path: $($Change.path)" }
    if ($Change.baseline_blob_sha1 -isnot [string] -or $Change.baseline_blob_sha1 -cne $Baseline[$Change.path].git_blob_sha1 -or
        ($Change.operation -ceq 'modify' -and
        ($Change.approved_blob_sha1 -isnot [string] -or $Change.approved_blob_sha1 -cnotmatch '^[0-9a-f]{40}$' -or
         $Change.approved_blob_sha1 -ceq $Change.baseline_blob_sha1))) {
        throw "Invalid divergence hashes: $($Change.path)"
    }
    Assert-ExactFields $Change.provenance @('commit', 'pull_request', 'reason') 'Unknown or missing ledger fields'
    $PullPrefix = 'https://github.com/' + $Manifest.source_repository + '/pull/'
    if ($Change.provenance.commit -isnot [string] -or $Change.provenance.commit -cnotmatch '^[0-9a-f]{40}$' -or
        $Change.provenance.pull_request -isnot [string] -or
        $Change.provenance.pull_request -cnotmatch ('^' + [regex]::Escape($PullPrefix) + '[1-9][0-9]*$') -or
        $Change.provenance.reason -isnot [string] -or [string]::IsNullOrWhiteSpace($Change.provenance.reason)) {
        throw "Missing divergence provenance: $($Change.path)"
    }
}
# Inspect every lexical ancestor: redirected parents cannot prove descendant absence.
function Assert-DeletedPathAbsent([string] $Relative) {
    $Current = $SourceRoot
    $Parts = $Relative.Split('/')
    for ($Index = 0; $Index -lt $Parts.Count; $Index++) {
        $Current = Join-Path $Current $Parts[$Index]
        try { $Entry = Get-Item -Force -LiteralPath $Current -ErrorAction Stop }
        catch [System.Management.Automation.ItemNotFoundException] { return }
        # Permission, I/O and provider errors propagate; only not-found proves absence.
        if ($null -ne $Entry) {
            if (($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $Entry.LinkType) {
                throw "Deleted path has link or redirected parent: $Relative"
            }
            if ($Index -eq $Parts.Count - 1 -or -not $Entry.PSIsContainer) {
                throw "Approved deleted path exists: $Relative"
            }
        }
    }
}
function Get-CanonicalHash([string] $FilePath, [string] $Relative) {
    $Hash = & git -C $SourceRoot hash-object "--path=$Relative" -- $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Cannot hash $Relative" }
    return $Hash.Trim()
}
$DeletedPaths = @($Approved.Values | Where-Object operation -ceq 'delete' | ForEach-Object path)
$Relocations = Read-SourceRelocations (Join-Path $Root 'migration/source-relocations.json') @($Manifest.files | ForEach-Object path) $DeletedPaths
$Owners = @($Manifest.files | Where-Object { $_.path.Contains('/') } | ForEach-Object { $_.path.Split('/')[0] } | Sort-Object -Unique)
foreach ($Owner in $Owners) {
    $OwnerItems = @($Manifest.files | Where-Object {
        $_.path.StartsWith($Owner + '/', [StringComparison]::Ordinal) -and
        -not ($Approved.ContainsKey($_.path) -and $Approved[$_.path].operation -ceq 'delete')
    })
    if ($OwnerItems.Count -gt 0 -and @($OwnerItems | Where-Object { -not $Relocations.ContainsKey($_.path) }).Count -eq 0) {
        Assert-DeletedPathAbsent $Owner
    }
}
$Missing = @()
$Verified = 0
$Deleted = 0
foreach ($Item in $Manifest.files) {
    $Relative = if ($Relocations.ContainsKey($Item.path)) { $Relocations[$Item.path] } else { $Item.path }
    if ($Approved.ContainsKey($Item.path) -and $Approved[$Item.path].operation -ceq 'delete') {
        if ($Relocations.ContainsKey($Item.path)) { throw "Approved deletion cannot be relocated: $($Item.path)" }
        Assert-DeletedPathAbsent $Item.path
        $Deleted++
        continue
    }
    $ExpectedHash = if ($Approved.ContainsKey($Item.path)) { $Approved[$Item.path].approved_blob_sha1 } else { $Item.git_blob_sha1 }
    if ($Relocations.ContainsKey($Item.path)) { Assert-DeletedPathAbsent $Item.path }
    Assert-NoRelocationLinks $SourceRoot $Relative
    $Path = Join-Path $SourceRoot $Relative
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $Missing += $Item.path; continue }
    $Hash = & git hash-object --no-filters -- $Path
    if ($LASTEXITCODE -ne 0) { throw "git hash-object failed: $($Item.path)" }
    if ($Hash.Trim() -eq $ExpectedHash) {
        if (-not $Approved.ContainsKey($Item.path) -and (Get-Item -Force -LiteralPath $Path).Length -ne $Item.size) { throw "Size mismatch: $($Item.path)" }
    } else {
        # The preserved upstream .gitattributes uses CRLF for .ps1 checkout.
        # Compare its canonical Git blob too; never normalize binary fixtures.
        $CanonicalHash = & git -C $SourceRoot hash-object "--path=$($Item.path)" -- $Path
        if ($LASTEXITCODE -ne 0 -or $CanonicalHash.Trim() -ne $ExpectedHash) {
            throw "Hash mismatch: $($Item.path)"
        }
    }
    $Verified++
}
$OriginalRelative = 'iCemDataMonitor.Contracts/Protos/telemetry.proto'
$OriginalActual = if ($Relocations.ContainsKey($OriginalRelative)) { $Relocations[$OriginalRelative] } else { $OriginalRelative }
$Original = Join-Path $SourceRoot $OriginalActual
$ProtoRecord = @($Manifest.files | Where-Object path -eq $OriginalRelative)
if ($ProtoRecord.Count -ne 1 -or (Get-CanonicalHash $Original $OriginalRelative) -ne $ProtoRecord[0].git_blob_sha1) {
    throw 'Canonical frozen proto changed'
}
$ProtoFiles = @(Get-ChildItem -LiteralPath $SourceRoot -Filter '*.proto' -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' })
if ($ProtoFiles.Count -ne 1 -or $ProtoFiles[0].FullName -ne $Original) { throw 'Expected exactly one canonical proto' }
foreach ($Asset in @('iCemDataMonitor.ico', 'iCemEmule.ico')) {
    $AssetSource = "assets/$Asset"
    $AssetActual = if ($Relocations.ContainsKey($AssetSource)) { $Relocations[$AssetSource] } else { $AssetSource }
    if ((Get-FileHash (Join-Path $Root "assets/$Asset")).Hash -ne (Get-FileHash (Join-Path $SourceRoot $AssetActual)).Hash) {
        throw "Original icon changed: $Asset"
    }
}
$Dashboard = Join-Path $Root 'web/dashboard'
Get-ChildItem -File -Recurse $Dashboard | ForEach-Object {
    $Relative = [IO.Path]::GetRelativePath($Dashboard, $_.FullName)
    $SourceRelative = 'iCemDataMonitor.Host/wwwroot/' + $Relative.Replace('\', '/')
    $SourceActual = if ($Relocations.ContainsKey($SourceRelative)) { $Relocations[$SourceRelative] } else { $SourceRelative }
    $Source = Join-Path $SourceRoot $SourceActual
    # The original tree uses text=auto, while the frozen Rust SPA uses -text.
    # Compare canonical Git bytes; Windows checkout newline conversion is not a source change.
    $RustRelative = 'rust/web/dashboard/' + $Relative.Replace('\', '/')
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
Write-Host "Baseline: $Verified verified existing files; $Deleted approved deletions of $($Manifest.files.Count) baseline entries; $($Approved.Count) exact approved legacy divergences; frozen proto/assets, D16/D17 theme predicates and REST inventory OK."
