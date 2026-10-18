#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Build the original net48 AddIn on Windows with the installed Halliburton iCem SDK.' }
$RepositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Import-Module (Join-Path $RepositoryRoot 'rust/installer/LegacyAddin.psm1') -Force
$ProjectPath = Join-Path $RepositoryRoot 'Burservis.iCem.DataMonitor/Burservis.iCem.DataMonitor.csproj'
$Project = [xml](Get-Content -Raw -LiteralPath $ProjectPath)
$References = @($Project.SelectNodes('//*[local-name()="Reference"]') | Where-Object { $Hint = $_.SelectSingleNode('*[local-name()="HintPath"]'); $Hint -and $Hint.InnerText -like '*Halliburton\iCem\Bin\*' })
$Sdk = @()
foreach ($Reference in $References) {
    $Path = [string]$Reference.HintPath
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required vendor SDK reference missing: $Path. AddIn is mandatory; it is never skipped." }
    $Sdk += [pscustomobject]@{ name = [string]$Reference.Include; sha256 = (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant() }
}
if (-not $Sdk.Count) { throw 'No vendor SDK references found in original AddIn project' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'AddIn bundle output already exists' }
$Work = Join-Path ([IO.Path]::GetTempPath()) ('icem-addin-build-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($Work) | Out-Null
Push-Location $RepositoryRoot
try {
    $Commit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'AddIn provenance requires Git' }
    $Dirty = & git status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $Dirty) { throw 'AddIn build requires a clean Git checkout' }
    $Tool = & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'dotnet SDK missing' }
    & dotnet build $ProjectPath -c Release -p:Platform=x64 -p:TreatWarningsAsErrors=true "-p:OutputPath=$Work/bin/" "-p:BaseIntermediateOutputPath=$Work/obj/" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Original AddIn build failed' }
    New-LegacyAddinBundle -RepositoryRoot $RepositoryRoot -Dll (Join-Path $Work 'bin/Burservis.iCem.DataMonitor.dll') -OutputDirectory $OutputDirectory -Commit $Commit.Trim() -SdkReferences $Sdk -BuildTool "dotnet $Tool; original net48 project, Release x64"
    Write-Host "Verified net48 AddIn bundle: $OutputDirectory"
} finally { Pop-Location; if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force } }
