#requires -Version 7.4
# File-only fixture. Never invokes MSI, registration, services or native binaries.
[CmdletBinding()]
param([Parameter(Mandatory)][string] $IncludeDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OpcGuidInventory.psm1') -Force
$expected = @(Read-OpcGuidInventory $IncludeDirectory) | ConvertTo-Json -Depth 5 -Compress
$work = Join-Path ([IO.Path]::GetTempPath()) ('icem-midl-inventory-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
Copy-Item -Path (Join-Path $IncludeDirectory '*_i.c') -Destination $work
$file = Join-Path $work 'opc_ae_i.c'
$original = [IO.File]::ReadAllText($file).Replace("`r`n", "`n")
$script:checks = 0
function Check-Content([string] $label, [string] $content, [bool] $accept) {
    [IO.File]::WriteAllText($file, $content)
    $rejected = $false
    try { $actual = @(Read-OpcGuidInventory $work) | ConvertTo-Json -Depth 5 -Compress }
    catch {
        if ($_.Exception.Message -notmatch '^(Unreviewed MIDL inventory:|Unexpected MIDL metadata heading:)') { throw }
        $rejected = $true
    }
    if ($accept -and ($rejected -or $actual -cne $expected)) { throw "Metadata-only change altered GUID inventory: $label" }
    if (-not $accept -and -not $rejected) { throw "Accepted changed MIDL code: $label" }
    $script:checks++
}
function Check-FileSet([string] $label) {
    $rejected = $false
    try { $null = Read-OpcGuidInventory $work }
    catch {
        if ($_.Exception.Message -cne 'Unexpected MIDL file inventory.') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Accepted changed MIDL file inventory: $label" }
    $script:checks++
}
try {
    Check-Content 'LF' $original $true
    Check-Content 'CRLF' $original.Replace("`n", "`r`n") $true
    Check-Content 'compiler version' ($original.Replace('8.01.0628', '8.99.9999')) $true
    Check-Content 'timestamp' ([regex]::Replace($original, '(?m)^/\* at [^*]*\*/', "/* at Wed Nov 11 00:00:00 2026`n */")) $true
    Check-Content 'source path' ([regex]::Replace($original, '(?m)^/\* Compiler settings for [^\n]+:', '/* Compiler settings for D:/runner/elsewhere/input.idl:')) $true
    Check-Content 'MIDL environment' ($original.Replace('env=Win32 (32b run), target_arch=X86', 'env=Win32 (64b run), target_arch=X86')) $true
    $entry = [regex]::Match($original, 'MIDL_DEFINE_GUID\(IID, IID_OPCEventServerCATID[^\n]+').Value
    if (-not $entry) { throw 'Reviewed mutation context not found' }
    Check-Content 'changed IID' ($original.Replace('0x58E13251', '0x58E13252')) $false
    Check-Content 'renamed IID' ($original.Replace('IID_OPCEventServerCATID', 'IID_Foreign')) $false
    Check-Content 'missing declaration' ($original.Replace($entry, '')) $false
    Check-Content 'duplicate declaration' ($original.Replace($entry, "$entry`n$entry")) $false
    Check-Content 'extra declaration' ($original.Replace($entry, "$entry`n" + $entry.Replace('IID_OPCEventServerCATID', 'IID_Foreign'))) $false
    Check-Content 'changed GUID kind' ($original.Replace('MIDL_DEFINE_GUID(IID, IID_OPCEventServerCATID', 'MIDL_DEFINE_GUID(CLSID, IID_OPCEventServerCATID')) $false
    Check-Content 'changed macro body' ($original.Replace('const type name', 'const type renamed')) $false
    Check-Content 'code inserted between comments' ($original.Replace('/* at ', "int foreign_code = 1;`n/* at ")) $false
    Check-Content 'comment termination injection' ($original.Replace('/* at ', '/* at */ int foreign_code = 1; /*')) $false
    Check-Content 'missing heading' ($original.Replace('/* at ', '/* timestamp ')) $false
    [IO.File]::WriteAllText($file, $original)
    $extra = Join-Path $work 'Foreign_i.c'
    [IO.File]::WriteAllText($extra, $original)
    Check-FileSet 'extra file'
    [IO.File]::Delete($extra)
    [IO.File]::Move($file, "$file.missing")
    Check-FileSet 'missing file'
    [IO.File]::Move("$file.missing", $file)
    Write-Output "PASS: $script:checks MIDL metadata portability and exact-code/file-inventory rejection checks."
} finally {
    # Delete only files copied/created by this fixture under its unique directory.
    foreach ($item in Get-ChildItem -LiteralPath $work -File) { [IO.File]::Delete($item.FullName) }
    [IO.Directory]::Delete($work)
}
