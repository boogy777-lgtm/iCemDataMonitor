[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DotNetPayload,
    [Parameter(Mandatory)] [string] $RustPayload,
    [string] $Output = 'artifacts/payload-size.json'
)
$ErrorActionPreference = 'Stop'
function Measure-Tree([string] $RootPath) {
    $Resolved = (Resolve-Path -LiteralPath $RootPath).Path
    $Files = @(Get-ChildItem -LiteralPath $Resolved -Recurse -File | ForEach-Object {
        [PSCustomObject]@{ path = [IO.Path]::GetRelativePath($Resolved, $_.FullName); bytes = $_.Length }
    })
    $Total = ($Files | Measure-Object bytes -Sum).Sum
    if ($Total -le 0) { throw "Empty payload: $Resolved" }
    [PSCustomObject]@{ root = $Resolved; bytes = [long]$Total; files = $Files }
}
$Baseline = Measure-Tree $DotNetPayload
$Candidate = Measure-Tree $RustPayload
$Result = [PSCustomObject]@{
    measuredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    baseline = $Baseline; candidate = $Candidate
    rustToDotNetRatio = $Candidate.bytes / $Baseline.bytes
    smaller = $Candidate.bytes -lt $Baseline.bytes
    note = 'Only supplied payload trees. MSI/prerequisites and functional parity are separate gates.'
}
$Parent = Split-Path $Output -Parent
if ($Parent) { New-Item -ItemType Directory -Force -Path $Parent | Out-Null }
$Result | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $Output
Write-Host "Wrote payload measurement: $Output"
