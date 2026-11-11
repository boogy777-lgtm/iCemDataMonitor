#requires -Version 7.4
# Checks the actual compiled reader's fixed manifest; does not open MSI/registry.
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Executable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$inventory = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../native-runtime-inventory.json') -Raw | ConvertFrom-Json -AsHashtable
$expected = [Collections.Generic.List[string]]::new()
foreach ($component in $inventory.components) {
    foreach ($row in $component.registry) {
        foreach ($value in $row.values) {
            if ($value.name -cin @('AccessPermission', 'LaunchPermission')) {
                if ($value.kind -cne 'Binary' -or $component.arch -cnotin @('x86', 'x64') -or
                    $row.key -cnotmatch '^SOFTWARE\\Classes\\AppID\\\{[0-9A-F-]{36}\}$') {
                    throw 'Unreviewed native AppID security ownership.'
                }
                $expected.Add("$($component.arch)|$($row.key)|$($value.name)")
            }
        }
    }
}
if ($expected.Count -eq 0) { throw 'Empty native AppID security inventory.' }
function Assert-Bindings([string[]] $rows, [string[]] $required) {
    if ($rows.Count -ne $required.Count -or @($required | Sort-Object -Unique).Count -ne $required.Count) {
        throw 'Missing/duplicate native security binding.'
    }
    $actual = [Collections.Generic.List[string]]::new()
    $properties = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $rows) {
        $parts = $row.Split('|')
        if ($parts.Count -ne 6 -or $parts[0] -cnotin @('x86', 'x64') -or
            $parts[3] -cnotmatch '^[A-Z][A-Z0-9_]+$' -or $parts[4] -cnotmatch '^[A-Z][A-Z0-9_]+$' -or
            $parts[5] -cnotmatch '^(?=.*[a-z])[A-Za-z][A-Za-z0-9_]*$') { throw 'Invalid snapshot binding manifest.' }
        foreach ($name in $parts[3..5]) {
            if (-not $properties.Add($name)) { throw 'Duplicate snapshot or private writer property.' }
        }
        $actual.Add(($parts[0..2] -join '|'))
    }
    if (@($actual | Sort-Object -Unique).Count -ne $actual.Count -or
        (($actual | Sort-Object) -join "`n") -cne (($required | Sort-Object) -join "`n")) {
        throw 'Compiled snapshot bindings differ from native security inventory.'
    }
}
$rows = @(& $Executable --manifest)
if ($LASTEXITCODE -ne 0) { throw 'Compiled snapshot manifest failed.' }
Assert-Bindings $rows $expected.ToArray()

# Prove that the completeness check rejects omissions, duplicates, view drift
# and public/private property aliasing instead of only accepting this fixture.
$duplicate = [string[]]$rows.Clone(); $duplicate[1] = $duplicate[0]
$wrongView = [string[]]$rows.Clone(); $parts = $wrongView[0].Split('|'); $parts[0] = if ($parts[0] -ceq 'x86') { 'x64' } else { 'x86' }; $wrongView[0] = $parts -join '|'
$sameProperty = [string[]]$rows.Clone(); $parts = $sameProperty[1].Split('|'); $parts[3] = $rows[0].Split('|')[3]; $sameProperty[1] = $parts -join '|'
$publicWriter = [string[]]$rows.Clone(); $parts = $publicWriter[0].Split('|'); $parts[5] = 'ICEM_PUBLIC_WRITER'; $publicWriter[0] = $parts -join '|'
$negative = @(
    [pscustomobject]@{ name = 'omitted'; rows = @($rows | Select-Object -Skip 1); required = $expected.ToArray() }
    [pscustomobject]@{ name = 'duplicate'; rows = $duplicate; required = $expected.ToArray() }
    [pscustomobject]@{ name = 'wrong writer view'; rows = $wrongView; required = $expected.ToArray() }
    [pscustomobject]@{ name = 'property alias'; rows = $sameProperty; required = $expected.ToArray() }
    [pscustomobject]@{ name = 'public writer'; rows = $publicWriter; required = $expected.ToArray() }
    [pscustomobject]@{ name = 'uncovered inventory'; rows = $rows; required = @($expected.ToArray()) + 'x86|SOFTWARE\Classes\AppID\{00000000-0000-0000-0000-000000000000}|AccessPermission' }
)
foreach ($case in $negative) {
    $rejected = $false
    try { Assert-Bindings $case.rows $case.required } catch { $rejected = $true }
    if (-not $rejected) { throw "Snapshot completeness accepted negative mutation: $($case.name)" }
}
Write-Output "PASS: $($rows.Count) compiled snapshot bindings cover native inventory; $($negative.Count) completeness mutations rejected."
