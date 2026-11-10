#requires -Version 7.4
# MUTATING TEST: disposable GitHub-hosted Windows runner only. Never use a workstation.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Msi,
    [Parameter(Mandatory)][string] $Case,
    [Parameter(Mandatory)][string] $EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or
    $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') {
    throw 'Refusing installation: this fixture requires a disposable GitHub-hosted Windows runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The disposable runner must supply an elevated test token.'
    }
} finally { $identity.Dispose() }
$Repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Import-Module (Join-Path $PSScriptRoot 'WindowsMsiDatabase.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WindowsBuildSdk.psm1') -Force
$Msi = (Resolve-Path -LiteralPath $Msi).Path
if ((Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash -ine ([IO.File]::ReadAllText("$Msi.sha256").Split(' ')[0])) { throw 'Blocked input MSI checksum mismatch.' }
$Cases = Get-Content -LiteralPath (Join-Path $Repo 'rust/installer/opc-core/acceptance-cases.json') -Raw | ConvertFrom-Json
$Supported = @('lifecycle', 'rollback', 'foreign-registry', 'foreign-component')
if ($Cases.schema_version -ne 1 -or (($Cases.cases | Sort-Object) -join ',') -cne (($Supported | Sort-Object) -join ',') -or
    @($Cases.cases | Sort-Object -Unique).Count -ne $Supported.Count -or $Case -cnotin $Supported) { throw 'Acceptance case coverage mismatch.' }
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
$Work = Join-Path $env:RUNNER_TEMP ('icem-opc-acceptance-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($Work) | Out-Null
Initialize-WindowsBuildSdk $Work
$Candidate = Join-Path $Work 'TEST-ONLY-NOT-FOR-DISTRIBUTION.msi'
Copy-Item -LiteralPath $Msi -Destination $Candidate
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($Candidate, 1)
function Execute-Sql([string] $sql) {
    $view = $database.OpenView($sql)
    try { $null = $view.Execute() } finally { $null = $view.Close(); $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}
try {
    $blocked = @(Read-WindowsMsiRecords $database 'SELECT `Condition` FROM `LaunchCondition` WHERE `Condition` = ''Installed''' @('Condition'))
    if ($blocked.Count -ne 1) { throw 'Expected the unchanged production fresh-install block exactly once.' }
    $UpgradeCode = @(Read-WindowsMsiRecords $database 'SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode''' @('Value'))
    if ($UpgradeCode.Count -ne 1 -or [guid]$UpgradeCode[0].Value -ne [guid]'CFAC98DF-4F13-4C41-948C-CA4AE350920A') { throw 'Not the reviewed shared OPC runtime.' }
    $ProductCode = @(Read-WindowsMsiRecords $database 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''' @('Value'))[0].Value
    $Keys = @(Read-WindowsMsiRecords $database 'SELECT `Key` FROM `RegLocator`' @('Key') | ForEach-Object { $_.Key }) + @(
        'SOFTWARE\Classes\OPC.ServerList', 'SOFTWARE\Classes\OPC.ServerList.1',
        'SOFTWARE\Classes\AppID\OpcEnum.EXE', 'SOFTWARE\Classes\AppID\OpcCategoryManager.exe',
        'SOFTWARE\Classes\OPC.OpcCategoryManager', 'SOFTWARE\Classes\OPC.OpcCategoryManager.1')
    $Keys = @($Keys | Sort-Object -Unique)
    if ($Keys.Count -lt 20 -or @($Keys | Where-Object { $_ -notmatch '^(SOFTWARE\\Classes\\|SYSTEM\\CurrentControlSet\\Services\\)' }).Count) { throw 'Invalid shared registration inventory.' }
    # Test-only copy: preserve every real conflict guard and custom action. Never publish it.
    Execute-Sql 'DELETE FROM `LaunchCondition` WHERE `Condition` = ''Installed'''
    Execute-Sql 'UPDATE `Property` SET `Value` = ''TEST ONLY - iCem Shared OPC Runtime'' WHERE `Property` = ''ProductName'''
    Execute-Sql 'INSERT INTO `CustomAction` (`Action`, `Type`, `Target`) VALUES (''IcemAcceptanceRollback'', 19, ''Expected acceptance rollback injection'')'
    Execute-Sql 'INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES (''IcemAcceptanceRollback'', ''ICEM_ACCEPTANCE_ROLLBACK = 1'', 6550)'
    $database.Commit()
} finally {
    foreach ($com in @($database, $installer)) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) }
}
$Paths = @()
$CoreDlls = @('opccomn_ps.dll', 'opcproxy.dll', 'opc_aeps.dll', 'opcbc_ps.dll', 'OpcCmdPs.dll', 'OpcDxPs.dll', 'opchda_ps.dll', 'opcsec_ps.dll')
foreach ($name in $CoreDlls) {
    $Paths += Join-Path ${env:CommonProgramFiles} "OPC Foundation/Bin/$name"
    $Paths += if ($name -in @('OpcCmdPs.dll', 'OpcDxPs.dll')) { Join-Path ${env:CommonProgramFiles(x86)} "OPC Foundation/Bin/$name" } else { Join-Path $env:WINDIR "SysWOW64/$name" }
}
$Paths += Join-Path $env:WINDIR 'SysWOW64/OpcEnum.exe'
$Paths += Join-Path ${env:CommonProgramFiles} 'OPC Foundation/Bin/OpcCategoryManager.exe'
function Registry-Rows([Microsoft.Win32.RegistryView] $view, [string] $path, [Collections.Generic.List[object]] $rows) {
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
    try { $key = $machine.OpenSubKey($path, $false) } finally { $machine.Dispose() }
    if ($null -eq $key) { return }
    try {
        $values = [ordered]@{}
        foreach ($name in @($key.GetValueNames() | Sort-Object)) {
            $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($value -is [byte[]]) { $value = [Convert]::ToBase64String($value) }
            $values[$name] = [ordered]@{ kind = $key.GetValueKind($name).ToString(); value = $value }
        }
        $security = [Microsoft.Win32.RegistryAclExtensions]::GetAccessControl($key)
        $rows.Add([ordered]@{ view = $view.ToString(); path = $path; values = $values; sddl = $security.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Access,Owner,Group') })
        foreach ($child in @($key.GetSubKeyNames() | Sort-Object)) { Registry-Rows $view "$path\$child" $rows }
    } finally { $key.Dispose() }
}
function Snapshot([string] $name) {
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)) {
        foreach ($path in $Keys) { Registry-Rows $view $path $rows }
    }
    $files = @($Paths | Sort-Object -Unique | ForEach-Object {
        if (Test-Path -LiteralPath $_) { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash; sddl = (Get-Acl -LiteralPath $_).Sddl } }
    })
    $services = @(Get-CimInstance Win32_Service -Filter "Name='OpcEnum'" | Select-Object Name, PathName, StartMode, StartName)
    $result = [ordered]@{ registry = @($rows.ToArray()); files = $files; services = $services }
    $json = ConvertTo-Json -InputObject $result -Depth 30
    [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "$name.json"), $json)
    return $json
}
function Equal-Snapshot([string] $before, [string] $after, [string] $phase) {
    if ($before -cne $after) { throw "$phase changed registry types/values/ACL, files or service configuration; inspect snapshots." }
}
function Invoke-Msi([string[]] $arguments, [string] $phase, [int] $expected) {
    $log = Join-Path $EvidenceDirectory "$phase.log"
    $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32/msiexec.exe') -ArgumentList (@($arguments) + @('/qn', '/norestart', '/l*v', ('"' + $log + '"'))) -PassThru
    try {
        if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw "$phase exceeded the disposable transaction budget; outcome unknown, no retry." }
        if ($process.ExitCode -ne $expected) { throw "$phase returned $($process.ExitCode), expected $expected; see $log" }
    } finally { $process.Dispose() }
}
function Refused-Before-Transaction([string] $phase) {
    $log = [IO.File]::ReadAllText((Join-Path $EvidenceDirectory "$phase.log"))
    if ($log -match '(?m)^Action start [^\r\n]*: InstallInitialize\.' -or -not $log.Contains('OpcCheckForeign')) {
        throw 'A late registration error/rollback is not a valid foreign-owner preflight refusal.'
    }
}
$report = [ordered]@{ schema_version = 1; case = $Case; result = 'FAIL'; product_commit = $env:GITHUB_SHA; input_sha256 = (Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash.ToLowerInvariant(); candidate_not_distributable = $true }
try {
    $before = Snapshot 'before'
    $initial = $before | ConvertFrom-Json
    if ($initial.registry.Count -ne 0 -or $initial.files.Count -ne 0 -or $initial.services.Count -ne 0) { throw 'Disposable runner is not OPC-clean; refusing to adopt or clean existing components.' }
    switch ($Case) {
        'lifecycle' {
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'fresh-install' 0
            $installed = Snapshot 'installed'
            if (($installed | ConvertFrom-Json).files.Count -ne 18) { throw 'Runtime did not install the complete 18-file native inventory.' }
            Invoke-Msi @('/fa', ('"' + $Candidate + '"')) 'repair' 0
            Equal-Snapshot $installed (Snapshot 'repaired') 'Repair'
            Invoke-Msi @('/x', $ProductCode) 'uninstall' 0
            Equal-Snapshot $before (Snapshot 'uninstalled') 'Uninstall'
        }
        'rollback' {
            Invoke-Msi @('/i', ('"' + $Candidate + '"'), 'ICEM_ACCEPTANCE_ROLLBACK=1') 'injected-rollback' 1603
            Equal-Snapshot $before (Snapshot 'rolled-back') 'Rollback'
        }
        'foreign-registry' {
            $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry32)
            try {
                $key = $machine.CreateSubKey('SOFTWARE\Classes\AppID\{13486D44-4821-11D2-A494-3CB306C10000}', $true)
                try { $key.SetValue('IcemAcceptanceForeign', [guid]::NewGuid().ToString('N'), [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
            } finally { $machine.Dispose() }
            $foreign = Snapshot 'foreign-before'
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'foreign-refusal' 1603
            Refused-Before-Transaction 'foreign-refusal'
            Equal-Snapshot $foreign (Snapshot 'foreign-after') 'Foreign registration refusal'
        }
        'foreign-component' {
            $foreignPayload = Join-Path $Work 'foreign.txt'
            [IO.File]::WriteAllText($foreignPayload, 'Foreign owner fixture; not an OPC library.')
            $foreignSource = Join-Path $Work 'Foreign.wxs'
            [IO.File]::WriteAllText($foreignSource, @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Package Name="iCem Acceptance Foreign Component" Manufacturer="iCem test only" Version="1.0.0" UpgradeCode="83DBDF96-794A-4437-8B5F-80BF54274F67" Scope="perMachine" UpgradeStrategy="none"><MediaTemplate EmbedCab="yes"/><StandardDirectory Id="SystemFolder"><Component Id="ForeignProxy" Guid="3C257474-772A-6CE8-282E-7F4413643C2C"><File Id="ForeignProxyFile" Name="opcproxy.dll" SourceFile="$foreignPayload" KeyPath="yes"/></Component></StandardDirectory><Feature Id="Foreign" Level="1"><ComponentRef Id="ForeignProxy"/></Feature></Package></Wix>
"@)
            $foreignMsi = Join-Path $Work 'foreign-owner.msi'
            Push-Location $Work
            try {
                $null = Assert-WindowsBuildSdk
                & wix build $foreignSource -arch x86 -wx -o $foreignMsi | Out-Host
                if ($LASTEXITCODE -ne 0) { throw 'Cannot build independent MSI component owner fixture.' }
            } finally { Pop-Location }
            Invoke-Msi @('/i', ('"' + $foreignMsi + '"')) 'foreign-owner-install' 0
            $foreign = Snapshot 'foreign-owner-before'
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'foreign-owner-refusal' 1603
            Refused-Before-Transaction 'foreign-owner-refusal'
            Equal-Snapshot $foreign (Snapshot 'foreign-owner-after') 'Foreign MSI client refusal'
        }
    }
    $report.result = 'PASS'
} catch {
    $report['error'] = $_.Exception.Message
    throw
} finally {
    $null = Snapshot 'final'
    [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'result.json'), ($report | ConvertTo-Json -Depth 8))
    if ((Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash.ToLowerInvariant() -cne $report.input_sha256) { throw 'The original blocked MSI changed during acceptance.' }
}
