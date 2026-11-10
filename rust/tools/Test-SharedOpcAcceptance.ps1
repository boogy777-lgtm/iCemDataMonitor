#requires -Version 7.4
# Pure synthetic-log tests. Does not invoke Windows Installer or read the machine.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SharedOpcAcceptance.psm1') -Force
$preflight = @'
Action start 10:00:00: OpcCheckForeign.
Action ended 10:00:00: OpcCheckForeign. Return value 3.
'@
$rollback = @'
Action start 10:00:00: InstallInitialize.
Action start 10:00:01: InstallExecute.
MSI (s) (00:00) [10:00:01:000]: Executing op: FileCopy(SourceName=fixture.dll)
Action ended 10:00:01: InstallExecute. Return value 1.
Action start 10:00:02: IcemAcceptanceRollback.
Expected acceptance rollback injection
Action ended 10:00:02: IcemAcceptanceRollback. Return value 3.
'@
$script:checks = 0
$snapshot = Read-SharedOpcSnapshot '{"registry":[{"values":{"":{"kind":"String","value":"default"},"named":{"kind":"DWord","value":6}}}],"files":[],"services":[]}'
if ($snapshot.registry.Count -ne 1 -or $snapshot.registry[0].values[''].value -cne 'default' -or
    $snapshot.registry[0].values.named.value -ne 6 -or $snapshot.files.Count -ne 0 -or $snapshot.services.Count -ne 0) {
    throw 'Snapshot decoder lost a default/named registry value or empty collection'
}
$script:checks++
$list = [Collections.ArrayList]::new()
$null = $list.Add('first')
$null = $list.Add('second')
if ((Get-SharedOpcComCount $list) -ne 2 -or (Get-SharedOpcComItem $list 1) -cne 'second') { throw 'COM collection reflection lost Count/Item' }
$script:checks++
$account = 'S-1-5-21-101-202-303-1001'
foreach ($pair in @(@('AccessPermission', '0x3'), @('LaunchPermission', '0xb'))) {
    $name, $mask = $pair
    $baseline = [Security.AccessControl.RawSecurityDescriptor]::new("O:SYG:SYD:(A;;$mask;;;SY)(A;;$mask;;;BA)")
    $bytes = [byte[]]::new($baseline.BinaryLength)
    $baseline.GetBinaryForm($bytes, 0)
    $custom = New-SharedOpcCustomizedDescriptor $bytes $name $account
    $actual = [Security.AccessControl.RawSecurityDescriptor]::new($custom, 0)
    $expected = [Security.AccessControl.RawSecurityDescriptor]::new("O:SYG:SYD:(A;;$mask;;;SY)(A;;$mask;;;BA)(A;;$mask;;;$account)")
    if ($actual.GetSddlForm('All') -cne $expected.GetSddlForm('All')) { throw 'Customized descriptor differs from the explicit local-only account grant.' }
    $script:checks++
    foreach ($invalid in @(
        @{ baseline = $bytes; name = $name; sid = 'S-1-1-0' },
        @{ baseline = $bytes; name = 'UnknownPermission'; sid = $account },
        @{ baseline = $custom; name = $name; sid = $account }
    )) {
        $rejected = $false
        try { New-SharedOpcCustomizedDescriptor $invalid.baseline $invalid.name $invalid.sid | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'Customized descriptor accepted an invalid identity, name or baseline.' }
        $script:checks++
    }
}
function Rejected([scriptblock] $check, [string] $text) {
    $rejected = $false
    try { & $check $text }
    catch {
        if ($_.Exception.Message -notmatch '^Not (a verified foreign-owner refusal|an injected rollback)') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Acceptance oracle allowed a false PASS' }
    $script:checks++
}
Assert-SharedOpcPreflightLog $preflight
Assert-SharedOpcRollbackLog $rollback
$script:checks += 2
$preflightCheck = { param($text) Assert-SharedOpcPreflightLog $text }
$rollbackCheck = { param($text) Assert-SharedOpcRollbackLog $text }
Rejected $preflightCheck 'OpcCheckForeign was scheduled, but never executed.'
Rejected $preflightCheck $preflight.Replace('Return value 3.', 'Return value 1.')
Rejected $preflightCheck ($preflight + "`nAction start 10:00:01: InstallInitialize.")
Rejected $preflightCheck ([string]::Join("`n", $preflight.Split("`n")[1..0]))
Rejected $rollbackCheck $preflight
Rejected $rollbackCheck $rollback.Replace('Executing op: FileCopy(', 'Executing op: NotAFileCopy(')
Rejected $rollbackCheck $rollback.Replace('InstallExecute. Return value 1.', 'InstallExecute. Return value 3.')
Rejected $rollbackCheck $rollback.Replace('Expected acceptance rollback injection', 'Earlier registration failure')
Rejected $rollbackCheck $rollback.Replace('IcemAcceptanceRollback. Return value 3.', 'IcemAcceptanceRollback. Return value 1.')
Rejected $rollbackCheck ($rollback.Replace('Action ended 10:00:01: InstallExecute. Return value 1.', '') + "`nAction ended 10:00:03: InstallExecute. Return value 1.")
Write-Output "PASS: $script:checks snapshot/default-value and acceptance-log checks; early failures cannot masquerade as rollback/preflight."
