# Pure log oracles for the disposable install fixture; no system operations.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Read-SharedOpcSnapshot([string] $Json) {
    # Registry default values have the valid empty name; PSCustomObject cannot
    # represent that JSON key. Preserve all names/types with dictionary decoding.
    return ConvertFrom-Json -InputObject $Json -AsHashtable
}
function Get-SharedOpcComCount([object] $Collection) {
    # The WindowsInstaller.Installer enumerator is a raw __ComObject: PowerShell
    # ETS exposes no Count, so query the COM property explicitly and read-only.
    if ($null -eq $Collection) { throw 'Missing MSI client collection.' }
    return [int]$Collection.GetType().InvokeMember('Count', [Reflection.BindingFlags]::GetProperty, $null, $Collection, $null)
}
function Get-SharedOpcComItem([object] $Collection, [int] $Index) {
    if ($null -eq $Collection) { throw 'Missing MSI client collection.' }
    return $Collection.GetType().InvokeMember('Item', [Reflection.BindingFlags]::GetProperty, $null, $Collection, [object[]]@($Index))
}
function Assert-SharedOpcPreflightLog([string] $Log) {
    $start = [regex]::Match($Log, '(?m)^Action start [^\r\n]*: OpcCheckForeign\.\r?$')
    $failed = [regex]::Match($Log, '(?m)^Action ended [^\r\n]*: OpcCheckForeign\. Return value 3\.\r?$')
    if (-not $start.Success -or -not $failed.Success -or $start.Index -ge $failed.Index -or
        $Log -match '(?m)^Action start [^\r\n]*: InstallInitialize\.') {
        throw 'Not a verified foreign-owner refusal before InstallInitialize.'
    }
}
function Assert-SharedOpcRollbackLog([string] $Log) {
    $execute = [regex]::Match($Log, '(?m)^Action ended [^\r\n]*: InstallExecute\. Return value 1\.\r?$')
    $injection = [regex]::Match($Log, '(?m)^Action start [^\r\n]*: IcemAcceptanceRollback\.\r?$')
    $failed = [regex]::Match($Log, '(?m)^Action ended [^\r\n]*: IcemAcceptanceRollback\. Return value 3\.\r?$')
    $copy = $Log.IndexOf('Executing op: FileCopy(', [StringComparison]::Ordinal)
    if (-not $execute.Success -or -not $injection.Success -or -not $failed.Success -or
        $copy -lt 0 -or $copy -ge $execute.Index -or $execute.Index -ge $injection.Index -or $injection.Index -ge $failed.Index -or
        -not $Log.Contains('Expected acceptance rollback injection')) {
        throw 'Not an injected rollback after successful application of the install script.'
    }
}
Export-ModuleMember -Function Read-SharedOpcSnapshot, Get-SharedOpcComCount, Get-SharedOpcComItem, Assert-SharedOpcPreflightLog, Assert-SharedOpcRollbackLog
