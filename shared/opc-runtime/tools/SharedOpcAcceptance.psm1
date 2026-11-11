# Pure log oracles for the disposable install fixture; no system operations.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-SharedOpcPrivateCi([string] $EventJson, [string] $Repository) {
    if ([string]::IsNullOrWhiteSpace($EventJson) -or [string]::IsNullOrWhiteSpace($Repository)) {
        throw 'Private Runtime CI requires repository event metadata.'
    }
    try { $event = ConvertFrom-Json -InputObject $EventJson -AsHashtable -ErrorAction Stop }
    catch { throw 'Private Runtime CI requires valid repository event metadata.' }
    if ($event -isnot [Collections.IDictionary] -or -not $event.Contains('repository') -or
        $event.repository -isnot [Collections.IDictionary] -or -not $event.repository.Contains('private') -or
        -not $event.repository.Contains('full_name') -or $event.repository.private -isnot [bool] -or
        -not $event.repository.private -or $event.repository.full_name -isnot [string] -or
        $event.repository.full_name -ine $Repository) {
        throw 'Runtime MSI acceptance requires the matching private GitHub repository.'
    }
}
function Read-SharedOpcSnapshot([string] $Json) {
    # Registry default values have the valid empty name; PSCustomObject cannot
    # represent that JSON key. Preserve all names/types with dictionary decoding.
    return ConvertFrom-Json -InputObject $Json -AsHashtable
}
function New-SharedOpcCustomizedDescriptor([byte[]] $Baseline, [string] $Name, [string] $Sid) {
    # Test-only: start from the exact local-only baseline and add one account SID.
    # This constructs bytes in memory; it does not discover or modify registrations.
    $mask = switch -CaseSensitive ($Name) {
        'AccessPermission' { '0x3' }
        'LaunchPermission' { '0xb' }
        default { throw 'Unknown test descriptor.' }
    }
    $account = [Security.Principal.SecurityIdentifier]::new($Sid)
    if (-not $account.Value.StartsWith('S-1-5-21-', [StringComparison]::Ordinal) -or
        $null -eq $account.AccountDomainSid -or $account.Value -ceq $account.AccountDomainSid.Value) {
        throw 'Test descriptor requires one explicit account SID.'
    }
    $expected = [Security.AccessControl.RawSecurityDescriptor]::new("O:SYG:SYD:(A;;$mask;;;SY)(A;;$mask;;;BA)")
    $current = [Security.AccessControl.RawSecurityDescriptor]::new($Baseline, 0)
    if ($current.GetSddlForm('All') -cne $expected.GetSddlForm('All')) { throw 'Test descriptor baseline changed.' }
    $customized = [Security.AccessControl.RawSecurityDescriptor]::new("O:SYG:SYD:(A;;$mask;;;SY)(A;;$mask;;;BA)(A;;$mask;;;$($account.Value))")
    $bytes = [byte[]]::new($customized.BinaryLength)
    $customized.GetBinaryForm($bytes, 0)
    return ,$bytes
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
function Get-SharedOpcSnapshotPropertySets([string[]] $Transport, [string[]] $Writer) {
    # Keep the array values nested in each object: flattening these into the
    # outer list silently drops hidden writer properties in PowerShell.
    [pscustomobject]@{ property = 'SecureCustomProperties'; names = [string[]]@($Transport) }
    [pscustomobject]@{ property = 'MsiHiddenProperties'; names = [string[]](@($Transport) + @($Writer)) }
}
function Resolve-SharedOpcMsiArguments([string[]] $Arguments) {
    # qb!- exercises basic UI without Cancel or modal dialogs on the runner.
    $ui = @($Arguments | Where-Object { $_ -match '^/q' })
    if ($ui.Count -gt 1 -or @($ui | Where-Object { $_ -cnotin @('/qn', '/qb!-') }).Count) {
        throw 'Unsupported or duplicate disposable MSI UI mode.'
    }
    if ($ui.Count -eq 0) { return @($Arguments) + '/qn' }
    return $Arguments
}
function Get-SharedOpcAdversarialRepairArguments([string] $Candidate, [string[]] $Extra) {
    # /f ignores all command-line properties. /i + REINSTALL is the separate
    # documented reinstall path that can exercise hostile public properties.
    return @('/i', ('"' + $Candidate + '"'), 'REINSTALL=ALL', 'REINSTALLMODE=omus') + @($Extra)
}
function Assert-SharedOpcPreflightLog([string] $Log) {
    $start = [regex]::Match($Log, '(?m)^Action start [^\r\n]*: OpcCheckForeign\.\r?$')
    $failed = [regex]::Match($Log, '(?m)^Action ended [^\r\n]*: OpcCheckForeign\. Return value 3\.\r?$')
    if (-not $start.Success -or -not $failed.Success -or $start.Index -ge $failed.Index -or
        $Log -match '(?m)^Action start [^\r\n]*: InstallInitialize\.') {
        throw 'Not a verified foreign-owner refusal before InstallInitialize.'
    }
}
function Assert-SharedOpcSnapshotRefusalLog([string] $Log) {
    $start = [regex]::Match($Log, '(?m)^Action start [^\r\n]*: IcemTestSnapshotGuard\.\r?$')
    $failed = [regex]::Match($Log, '(?m)^Action ended [^\r\n]*: IcemTestSnapshotGuard\. Return value 3\.\r?$')
    $marker = $Log.IndexOf('ICEM_TEST_SNAPSHOT_REJECTED', [StringComparison]::Ordinal)
    if (-not $start.Success -or -not $failed.Success -or $marker -lt $start.Index -or $marker -ge $failed.Index -or
        $Log -match '(?m)^Action start [^\r\n]*: InstallInitialize\.') {
        throw 'Not a verified snapshot refusal before InstallInitialize.'
    }
}
function Assert-SharedOpcSnapshotAcceptedLog([string] $Log) {
    $start = [regex]::Match($Log, '(?m)^Action start [^\r\n]*: IcemTestSnapshotGuard\.\r?$')
    $ended = [regex]::Match($Log, '(?m)^Action ended [^\r\n]*: IcemTestSnapshotGuard\. Return value 1\.\r?$')
    $initialize = [regex]::Match($Log, '(?m)^Action start [^\r\n]*: InstallInitialize\.\r?$')
    $marker = $Log.IndexOf('ICEM_TEST_SNAPSHOT_VALIDATED', [StringComparison]::Ordinal)
    if (-not $start.Success -or -not $ended.Success -or -not $initialize.Success -or
        $marker -lt $start.Index -or $marker -ge $ended.Index -or $ended.Index -ge $initialize.Index) {
        throw 'Not a verified snapshot acceptance before InstallInitialize.'
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
Export-ModuleMember -Function Assert-SharedOpcPrivateCi, Read-SharedOpcSnapshot, New-SharedOpcCustomizedDescriptor, Get-SharedOpcComCount, Get-SharedOpcComItem, Get-SharedOpcSnapshotPropertySets, Resolve-SharedOpcMsiArguments, Get-SharedOpcAdversarialRepairArguments, Assert-SharedOpcPreflightLog, Assert-SharedOpcSnapshotRefusalLog, Assert-SharedOpcSnapshotAcceptedLog, Assert-SharedOpcRollbackLog
