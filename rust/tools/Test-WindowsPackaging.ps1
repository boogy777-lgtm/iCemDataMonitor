#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $Root 'installer/WindowsPayload.psm1') -Force
$Temp = Join-Path ([IO.Path]::GetTempPath()) ('icem-package-tests-' + [guid]::NewGuid().ToString('N'))
$Repo = Join-Path $Temp 'repo'
$Bins = Join-Path $Temp 'bins'
$Output = Join-Path $Temp 'out'
function Write-TestFile([string] $Relative, [string] $Text) {
    $Path = Join-Path $Repo $Relative
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
}
function Assert-Rejected([scriptblock] $Operation, [string] $Message) {
    $Failed = $false
    try { & $Operation | Out-Null } catch {
        if (-not $_.Exception.Message.Contains($Message)) { throw }
        $Failed = $true
    }
    if (-not $Failed) { throw "Expected rejection: $Message" }
}
function Package([string] $Version) {
    New-WindowsArchive -RepositoryRoot $Repo -BinaryDirectory $Bins -OutputDirectory $Output -Version $Version -Commit ('1' * 40) -Compiler 'synthetic packaging fixture'
}
try {
    Write-TestFile 'rust/installer/windows-payload.json' (@{
        schema_version = 1; target = 'x86_64-pc-windows-msvc'
        binaries = @(@{ package = 'test'; bin = 'test'; destination = 'test.exe' })
        files = @(@{ source = 'icon.ico'; destination = 'assets/icon.ico' })
        directories = @(@{ source = 'templates'; destination = 'templates'; pattern = '*.json' })
    } | ConvertTo-Json -Depth 8)
    Write-TestFile 'icon.ico' 'synthetic test resource'
    Write-TestFile 'templates/one.json' '{}'
    Write-TestFile 'templates/two.json' '{}'
    # A minimal PE header tests the packaging architecture check, not loader validity.
    [IO.Directory]::CreateDirectory($Bins) | Out-Null
    $Pe = [byte[]]::new(128)
    $Pe[0] = 0x4D; $Pe[1] = 0x5A; $Pe[0x3C] = 64
    $Pe[64] = 0x50; $Pe[65] = 0x45; $Pe[68] = 0x64; $Pe[69] = 0x86
    $Pe[88] = 0x0B; $Pe[89] = 2
    [IO.File]::WriteAllBytes((Join-Path $Bins 'test.exe'), $Pe)
    $Result = Package '0.1.0-test'
    $Unpack = Join-Path $Temp 'relocated space'
    [IO.Compression.ZipFile]::ExtractToDirectory($Result.archive, $Unpack)
    $Verified = Test-WindowsPayload $Unpack
    if ($Verified.files.Count -ne 4) { throw 'Template directory was not packaged completely' }
    if ((Get-Content -Raw -LiteralPath $Result.checksum).Split(' ')[0] -ne (Get-FileHash -LiteralPath $Result.archive).Hash.ToLowerInvariant()) { throw 'Bad ZIP checksum' }
    Assert-Rejected { Package '0.1.0-test' } 'already exists'
    [IO.File]::WriteAllText((Join-Path $Unpack 'assets/icon.ico'), 'tampered')
    Assert-Rejected { Test-WindowsPayload $Unpack } 'hash/size mismatch'
    [IO.File]::WriteAllText((Join-Path $Unpack 'assets/icon.ico'), 'synthetic test resource')
    [IO.File]::WriteAllText((Join-Path $Unpack 'extra.txt'), 'unexpected')
    Assert-Rejected { Test-WindowsPayload $Unpack } 'Unexpected payload file count'
    Remove-Item -LiteralPath (Join-Path $Unpack 'extra.txt')
    Remove-Item -LiteralPath (Join-Path $Unpack 'test.exe')
    Assert-Rejected { Test-WindowsPayload $Unpack } 'Missing payload file'
    $PlanPath = Join-Path $Repo 'rust/installer/windows-payload.json'
    $Original = Get-Content -Raw -LiteralPath $PlanPath
    foreach ($Bad in @('../escape.ico', '/absolute.ico', 'C:\escape.ico', 'assets//empty.ico', 'release-manifest.json')) {
        $Plan = $Original | ConvertFrom-Json
        $Plan.files[0].destination = $Bad
        [IO.File]::WriteAllText($PlanPath, ($Plan | ConvertTo-Json -Depth 8))
        Assert-Rejected { Package '0.1.1-test' } $(if ($Bad -eq 'release-manifest.json') { 'reserved payload path' } else { 'relative payload path' })
    }
    $Plan = $Original | ConvertFrom-Json
    $Plan.files += $Plan.files[0]
    [IO.File]::WriteAllText($PlanPath, ($Plan | ConvertTo-Json -Depth 8))
    Assert-Rejected { Package '0.1.1-test' } 'Duplicate/reserved payload path'
    [IO.File]::WriteAllText($PlanPath, $Original)
    $Pe[68] = 0x4C; $Pe[69] = 1 # reject x86 rather than pretending Windows x64
    [IO.File]::WriteAllBytes((Join-Path $Bins 'test.exe'), $Pe)
    Assert-Rejected { Package '0.1.1-test' } 'AMD64 PE'
    [IO.File]::WriteAllText((Join-Path $Bins 'test.exe'), 'ELF is not a Windows executable')
    Assert-Rejected { Package '0.1.1-test' } 'Not a Windows PE'
    [IO.File]::WriteAllBytes((Join-Path $Bins 'test.exe'), [byte[]]@(0x4D))
    Assert-Rejected { Package '0.1.1-test' } 'Not a Windows PE'
    [IO.File]::WriteAllBytes((Join-Path $Bins 'test.exe'), [byte[]]::new(128))
    Assert-Rejected { Package '0.1.1-test' } 'Not a Windows PE'
    if (@(Get-ChildItem -LiteralPath $Output -Filter '.package-*' -Force).Count) { throw 'Failed packaging left staging directories' }
    if (@(Get-ChildItem -LiteralPath $Output -Filter '*0.1.1*').Count) { throw 'Failed packaging published an archive' }
    Write-Host 'PASS Windows packaging: ZIP roundtrip/completeness/checksums, no overwrite, tamper/missing/extra files, path escape/duplicates, PE architecture and failure cleanup.'
} finally { if (Test-Path -LiteralPath $Temp) { Remove-Item -LiteralPath $Temp -Recurse -Force } }
