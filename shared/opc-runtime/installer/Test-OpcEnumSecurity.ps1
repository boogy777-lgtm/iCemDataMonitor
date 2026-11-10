[CmdletBinding()]
param([Parameter(Mandatory)][string]$UpstreamSource)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$base = Join-Path $env:LOCALAPPDATA 'Temp/opencode'
$work = Join-Path $base ('opcenum-security-fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$copy = Join-Path $work 'OpcEnum.cpp'
Copy-Item -LiteralPath $UpstreamSource -Destination $copy
$before = (Get-FileHash -LiteralPath $UpstreamSource -Algorithm SHA256).Hash
$result = & (Join-Path $PSScriptRoot 'Apply-OpcEnumSecurity.ps1') -SourceFile $copy
if ($result -notmatch '^[0-9a-f]{64}$') { throw 'Missing patched source hash.' }
$text = [IO.File]::ReadAllText($copy)
$start = $text.IndexOf('inline HRESULT CServiceModule::RegisterServer', [StringComparison]::Ordinal)
$end = $text.IndexOf('// UnregisterServer', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) { throw 'Missing RegisterServer scope.' }
$scope = $text.Substring($start, $end - $start)
if ($scope -match 'Everyone|AuthenticationLevel.*\b1\b|\bUninstall\s*\(' -or
    $scope -notmatch 'IcemSetOpcEnumSecurity\(key\)' -or $scope -notmatch 'if \(!Install\(\)\)') { throw 'Unexpected registration security behavior.' }
foreach ($required in @('RPC_C_AUTHN_LEVEL_PKT_PRIVACY', 'O:SYG:SYD:(A;;0x3;;;SY)(A;;0x3;;;BA)', 'O:SYG:SYD:(A;;0xb;;;SY)(A;;0xb;;;BA)')) {
    if (!$text.Contains($required)) { throw "Missing required baseline: $required" }
}
if ($text -notmatch 'if \(FAILED\(hr\)\) \{ LogEvent\("CoInitializeSecurity failed: Error #0x%x", hr\); CoUninitialize\(\); return; \}') { throw 'COM security failure must not publish classes.' }
$rejected = $false
try { & (Join-Path $PSScriptRoot 'Apply-OpcEnumSecurity.ps1') -SourceFile $copy | Out-Null }
catch { $rejected = $true }
if (!$rejected -or (Get-FileHash -LiteralPath $UpstreamSource -Algorithm SHA256).Hash -ne $before) { throw 'Source integrity or repeat-application guard failed.' }
Write-Output "PASS: source pin, local-only SDDL, packet privacy, no existing-service removal, repeat refusal; patched SHA256 $result."
