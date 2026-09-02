<#
.SYNOPSIS
  Local release helper for iCemDataMonitor: verify, build the MSI, optionally
  push the vX.Y.Z tag that triggers the release.yml GitHub Actions pipeline.

.DESCRIPTION
  Repo root is resolved as the PARENT of the folder containing this script
  (scripts\ is the direct parent folder). Flow:
    (a) git sanity: repo present, tracked working tree CLEAN (abort on any
        `git status --porcelain` output unless -Force)
    (b) ProductCode drift check — RETIRED 2026-09-01 PM (1731/10005 autopsy):
        Product.wxs now ships ProductCode="*" (auto per build); every build
        is a new product and MajorUpgrade (AllowSameVersionUpgrades +
        AllowDowngrades) handles any install direction. No manual GUID
        rotation, nothing to drift. The step verifies only that the auto
        marker is in place (a hand-rotated GUID back would REOPEN the 1731
        repair-desync class).
    (c) unless -SkipBuild: run installer\build-msi.ps1 -MsiVersion <Version>
    (d) print SHA256 of the produced MSI
    (e) with -Push: create and push the annotated tag "v<Version>" (pushing
        the tag triggers the release pipeline)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\make-release.ps1 -Version 1.4.4 -Push

.NOTES
  PowerShell 5.1 compatible (no &&, no ??, no ternary). ASCII only.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$Push,
    [switch]$Force,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must match x.y.z (e.g. 1.4.4)."
}

$ScriptDir = $PSScriptRoot
$Repo = Split-Path -Parent $ScriptDir
$WxsPath = Join-Path $Repo 'installer\Product.wxs'

function Invoke-GitCapture([string[]]$GitArgs) {
    # Capture stdout+stderr WITHOUT tripping $ErrorActionPreference='Stop':
    # in PS 5.1 redirected native stderr becomes ErrorRecords, so EAP is
    # relaxed locally; the caller checks the exit code instead.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $Repo @GitArgs 2>&1
    } finally {
        $ErrorActionPreference = $prevEap
    }
    [pscustomobject]@{ Code = $LASTEXITCODE; Output = (@($out) -join "`n") }
}

# ------------------------------------------------ (a) git sanity ------------
$inside = Invoke-GitCapture @('rev-parse', '--is-inside-work-tree')
if ($inside.Code -ne 0) { throw "Not a git repository: $Repo" }

$status = Invoke-GitCapture @('status', '--porcelain')
if ($status.Code -ne 0) { throw "git status failed: $($status.Output)" }
if (-not $Force -and $status.Output.Trim().Length -gt 0) {
    throw "Working tree is not clean - commit or stash first (or pass -Force).`n$($status.Output)"
}
Write-Host '== git repo OK, working tree clean'

# ------------------------------------- (b) ProductCode marker check ---------
# 2026-09-01 PM: ProductCode="*" is the law (auto per build; MajorUpgrade +
# AllowSameVersionUpgrades + AllowDowngrades cover every install direction).
# A hand-rotated GUID in Product.wxs would REOPEN error 1731 (same-version
# rebuild = new PackageCode vs cached client package = repair desync).
$wxsText = Get-Content -LiteralPath $WxsPath -Raw -ErrorAction Stop
if ($wxsText -match 'ProductCode="\*"') {
    Write-Host '== ProductCode="*" (auto per build) - no drift check needed'
} else {
    throw ('Product.wxs must ship ProductCode="*" (2026-09-01 PM law, ' +
        '1731/10005 autopsy). A fixed ProductCode GUID was found - revert to "*".')
}

# --------------------------------------------------- (c) build MSI ----------
$msiPath = Join-Path $Repo ('dist\iCemDataMonitor-{0}-x64.msi' -f $Version)
if (-not $SkipBuild) {
    $buildScript = Join-Path $Repo 'installer\build-msi.ps1'
    Write-Host "== building MSI $Version"
    # 2026-09-02: -NoPause keeps the release flow non-interactive (the build
    # script's end-of-run keypress wait is for double-clicked consoles only).
    & $buildScript -MsiVersion $Version -NoPause
    if ($LASTEXITCODE -ne 0) { throw "build-msi.ps1 failed (exit $LASTEXITCODE)" }
}
if (-not (Test-Path -LiteralPath $msiPath)) {
    throw "MSI not found: $msiPath (build it first, or drop -SkipBuild)."
}

# ------------------------------------------------ (d) print SHA256 ----------
$hash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash
Write-Host ''
Write-Host ("SHA256  {0}" -f $hash)
Write-Host ("        {0}" -f $msiPath)

# --------------------------------------------- (e) optional tag push --------
if ($Push) {
    $tagName = "v$Version"
    $exists = Invoke-GitCapture @('rev-parse', '-q', '--verify', "refs/tags/$tagName")
    if ($exists.Code -eq 0) { throw "Tag $tagName already exists locally or on origin." }

    & git -C $Repo tag -a $tagName -m "iCem Data Monitor $tagName"
    if ($LASTEXITCODE -ne 0) { throw "git tag $tagName failed (exit $LASTEXITCODE)" }

    & git -C $Repo push origin $tagName
    if ($LASTEXITCODE -ne 0) { throw "git push origin $tagName failed (exit $LASTEXITCODE)" }

    Write-Host ''
    Write-Host "== tag $tagName pushed. The release.yml workflow (on: push tags 'v*')"
    Write-Host '   now builds the MSI and publishes the GitHub Release automatically.'
}
