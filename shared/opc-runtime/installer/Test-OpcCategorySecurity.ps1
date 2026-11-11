[CmdletBinding()]
param([Parameter(Mandatory)][string]$UpstreamSource)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$base = Join-Path $env:LOCALAPPDATA 'Temp/opencode'
$work = Join-Path $base ('opccategory-security-fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$categoryDir = Join-Path $work 'Source/Common/CategoryManager'
$sharedDir = Join-Path $work 'Source/Shared/OpcUtilityClasses'
New-Item -ItemType Directory -Path $categoryDir, $sharedDir -Force | Out-Null
$copy = Join-Path $categoryDir 'CategoryManager.cpp'
$sharedUpstream = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $UpstreamSource) '../../Shared/OpcUtilityClasses/COpcComModule.cpp'))
$sharedCopy = Join-Path $sharedDir 'COpcComModule.cpp'
Copy-Item -LiteralPath $UpstreamSource -Destination $copy
Copy-Item -LiteralPath $sharedUpstream -Destination $sharedCopy
$before = (Get-FileHash -LiteralPath $UpstreamSource -Algorithm SHA256).Hash
$sharedBefore = (Get-FileHash -LiteralPath $sharedUpstream -Algorithm SHA256).Hash
$result = & (Join-Path $PSScriptRoot 'Apply-OpcCategorySecurity.ps1') -SourceFile $copy
$text = [IO.File]::ReadAllText($copy)
if ($result -notmatch '^[0-9a-f]{64}$' -or $text.Contains('OPC_START_LOCAL_SERVER_EX(hInstance, lpCmdLine)') -or
    !$text.Contains('OpcWinMain(pModule, hInstance, lpCmdLine)') -or $text.Contains('RegisterFromFiles(') -or
    !$text.Contains('RPC_C_AUTHN_LEVEL_PKT_PRIVACY') -or !$text.Contains('REG_CREATED_NEW_KEY')) { throw 'Unexpected CategoryManager patch result.' }
$sharedText = [IO.File]::ReadAllText($sharedCopy)
if ([regex]::Matches($sharedText, 'CoInitializeSecurity\(reinterpret_cast<PSECURITY_DESCRIPTOR>\(&m_cAppID\).*?EOAC_APPID').Count -ne 2 -or
    $sharedText.Contains('hResult != RPC_E_TOO_LATE')) { throw 'CategoryManager runtime security was not made AppID-driven and fail-closed.' }
$rejected = $false
try { & (Join-Path $PSScriptRoot 'Apply-OpcCategorySecurity.ps1') -SourceFile $copy | Out-Null } catch { $rejected = $true }
if (!$rejected -or (Get-FileHash -LiteralPath $UpstreamSource -Algorithm SHA256).Hash -ne $before -or
    (Get-FileHash -LiteralPath $sharedUpstream -Algorithm SHA256).Hash -ne $sharedBefore) { throw 'Source integrity or repeat-application guard failed.' }
$sharedHash = (Get-FileHash -LiteralPath $sharedCopy -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "PASS: pinned source, no external CategoryManager settings, repeat refusal; patched CategoryManager SHA256 $result; COM module SHA256 $sharedHash."
