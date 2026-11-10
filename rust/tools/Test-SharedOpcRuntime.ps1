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
Import-Module (Join-Path $PSScriptRoot 'SharedOpcAcceptance.psm1') -Force
$Msi = (Resolve-Path -LiteralPath $Msi).Path
if ((Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash -ine ([IO.File]::ReadAllText("$Msi.sha256").Split(' ')[0])) { throw 'Blocked input MSI checksum mismatch.' }
$Cases = Get-Content -LiteralPath (Join-Path $Repo 'rust/installer/opc-core/acceptance-cases.json') -Raw | ConvertFrom-Json
$Supported = @('lifecycle', 'rollback', 'foreign-registry', 'foreign-component', 'clients', 'shared-owner')
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
    # Standard WiX sequencing may omit InstallExecute. Without it, a Type 19 at
    # 6550 fails before deferred changes run at InstallFinalize: not a rollback test.
    $execute = @(Read-WindowsMsiRecords $database 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = ''InstallExecute''' @('Sequence'))
    if ($execute.Count -gt 1 -or ($execute.Count -eq 1 -and [int]$execute[0].Sequence -ne 6500)) { throw 'Unexpected InstallExecute sequencing.' }
    if ($execute.Count -eq 0) {
        Execute-Sql 'INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES (''InstallExecute'', ''ICEM_ACCEPTANCE_ROLLBACK = 1'', 6500)'
    }
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
function Build-ForeignOwner([string] $proxy, [string] $enumerator, [string] $destination) {
    Push-Location $Work
    try {
        $null = Assert-WindowsBuildSdk
        & wix build (Join-Path $Repo 'rust/installer/opc-core/tests/ForeignOwner.wxs') -arch x86 -wx -d "ProxyPayload=$proxy" -d "EnumPayload=$enumerator" -o $destination | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Cannot build independent MSI component owner fixture.' }
    } finally { Pop-Location }
}
$report = [ordered]@{ schema_version = 1; case = $Case; result = 'FAIL'; product_commit = $env:GITHUB_SHA; input_sha256 = (Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash.ToLowerInvariant(); candidate_not_distributable = $true }
try {
    $before = Snapshot 'before'
    $initial = Read-SharedOpcSnapshot $before
    if ($initial.registry.Count -ne 0 -or $initial.files.Count -ne 0 -or $initial.services.Count -ne 0) { throw 'Disposable runner is not OPC-clean; refusing to adopt or clean existing components.' }
    switch ($Case) {
        'lifecycle' {
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'fresh-install' 0
            $installed = Snapshot 'installed'
            if ((Read-SharedOpcSnapshot $installed).files.Count -ne 18) { throw 'Runtime did not install the complete 18-file native inventory.' }
            # Damage only resources installed by this transaction on this clean
            # disposable runner. A repair exit code without restoration is not PASS.
            [IO.File]::Delete((Join-Path $env:WINDIR 'SysWOW64/opccomn_ps.dll'))
            foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)) {
                $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
                try {
                    $key = $machine.OpenSubKey('SOFTWARE\Classes\Interface\{13486D50-4821-11D2-A494-3CB306C10000}\NumMethods', $true)
                    if ($null -eq $key) { throw 'The installed proxy method-count registration is missing before damage injection.' }
                    try { $key.DeleteValue('', $true) } finally { $key.Dispose() }
                } finally { $machine.Dispose() }
            }
            $null = Snapshot 'damaged'
            # Windows Installer's documented default repair is omus. Force-all
            # replacement (a) changes inherited-descriptor control metadata even
            # for healthy files; it is a separate unaccepted maintenance case.
            # Keep the exact ACL comparator; do not normalize away that difference.
            Invoke-Msi @('/fomus', ('"' + $Candidate + '"')) 'repair' 0
            Equal-Snapshot $installed (Snapshot 'repaired') 'Repair'
            Invoke-Msi @('/x', $ProductCode) 'uninstall' 0
            Equal-Snapshot $before (Snapshot 'uninstalled') 'Uninstall'
        }
        'rollback' {
            Invoke-Msi @('/i', ('"' + $Candidate + '"'), 'ICEM_ACCEPTANCE_ROLLBACK=1') 'injected-rollback' 1603
            Assert-SharedOpcRollbackLog ([IO.File]::ReadAllText((Join-Path $EvidenceDirectory 'injected-rollback.log')))
            Equal-Snapshot $before (Snapshot 'rolled-back') 'Rollback'
        }
        'clients' {
            # Compile is safe, but executing these clients may activate the service;
            # both remain strictly behind the disposable/admin gate at script entry.
            $clientArchitectures = @('x86', 'x64')
            $report['clients'] = @()
            foreach ($arch in $clientArchitectures) {
                $clientRoot = Join-Path $Work "client-$arch"
                Push-Location $Work
                try {
                    $null = Assert-WindowsBuildSdk
                    & dotnet build (Join-Path $Repo 'rust/installer/opc-core/tests/OpcComClient.csproj') -c Release "-p:PlatformTarget=$arch" "-p:BaseIntermediateOutputPath=$clientRoot/obj/" "-p:OutputPath=$clientRoot/bin/" -v:minimal | Out-Host
                    if ($LASTEXITCODE -ne 0) { throw "Independent $arch COM client did not compile" }
                } finally { Pop-Location }
            }
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'client-install' 0
            $null = Snapshot 'client-installed'
            $failedClients = [Collections.Generic.List[string]]::new()
            foreach ($arch in $clientArchitectures) {
                $exe = Join-Path $Work "client-$arch/bin/icem-opc-com-client.exe"
                $out = Join-Path $EvidenceDirectory "com-$arch.stdout.log"
                $err = Join-Path $EvidenceDirectory "com-$arch.stderr.log"
                $client = Start-Process -FilePath $exe -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
                try {
                    if (-not $client.WaitForExit(60000)) { $client.Kill($true); throw "Independent $arch COM client timed out; no retry." }
                    $bits = if ($arch -ceq 'x86') { 32 } else { 64 }
                    $report.clients += [ordered]@{ architecture = $arch; expected_pointer_bits = $bits; exe_sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant(); exit_code = $client.ExitCode }
                    if ($client.ExitCode -ne 0 -or -not ([IO.File]::ReadAllText($out)).StartsWith("PASS COM/RPC: architecture=$bits; authentication=packet-privacy;", [StringComparison]::Ordinal)) { $failedClients.Add($arch) }
                } finally { $client.Dispose() }
            }
            if ($failedClients.Count) { throw "Independent COM/RPC client failed ($($failedClients -join ', ')); see client logs. Both architectures were attempted once, without retries." }
            Invoke-Msi @('/x', $ProductCode) 'client-uninstall' 0
            Equal-Snapshot $before (Snapshot 'client-uninstalled') 'Uninstall after real COM activation'
        }
        'foreign-registry' {
            $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry32)
            try {
                $key = $machine.CreateSubKey('SOFTWARE\Classes\AppID\{13486D44-4821-11D2-A494-3CB306C10000}', $true)
                try { $key.SetValue('IcemAcceptanceForeign', [guid]::NewGuid().ToString('N'), [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
            } finally { $machine.Dispose() }
            $foreign = Snapshot 'foreign-before'
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'foreign-refusal' 1603
            Assert-SharedOpcPreflightLog ([IO.File]::ReadAllText((Join-Path $EvidenceDirectory 'foreign-refusal.log')))
            Equal-Snapshot $foreign (Snapshot 'foreign-after') 'Foreign registration refusal'
        }
        'foreign-component' {
            $foreignPayload = Join-Path $Work 'foreign.txt'
            [IO.File]::WriteAllText($foreignPayload, 'Foreign owner fixture; not an OPC library.')
            $foreignMsi = Join-Path $Work 'foreign-owner.msi'
            Build-ForeignOwner $foreignPayload $foreignPayload $foreignMsi
            Invoke-Msi @('/i', ('"' + $foreignMsi + '"')) 'foreign-owner-install' 0
            $foreign = Snapshot 'foreign-owner-before'
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'foreign-owner-refusal' 1603
            Assert-SharedOpcPreflightLog ([IO.File]::ReadAllText((Join-Path $EvidenceDirectory 'foreign-owner-refusal.log')))
            Equal-Snapshot $foreign (Snapshot 'foreign-owner-after') 'Foreign MSI client refusal'
        }
        'shared-owner' {
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'shared-runtime-install' 0
            $sharedPaths = @((Join-Path $env:WINDIR 'SysWOW64/opcproxy.dll'), (Join-Path $env:WINDIR 'SysWOW64/OpcEnum.exe'))
            $foreignMsi = Join-Path $Work 'shared-owner.msi'
            # Same installed bytes/version, not synthetic executable content. This
            # second product deliberately claims the canonical file components but
            # does not run registration or author our service/COM rows.
            Build-ForeignOwner $sharedPaths[0] $sharedPaths[1] $foreignMsi
            Invoke-Msi @('/i', ('"' + $foreignMsi + '"')) 'shared-owner-install' 0
            $coowned = Read-SharedOpcSnapshot (Snapshot 'coowned')
            $sharedCodes = @('{3C257474-772A-6CE8-282E-7F4413643C2C}', '{4695E177-FFA9-6706-9772-290B37AD1438}')
            $expectedKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $native = Get-Content -LiteralPath (Join-Path $Repo 'rust/installer/opc-core/native-runtime-inventory.json') -Raw | ConvertFrom-Json -AsHashtable
            foreach ($component in @($native.components | Where-Object { $_.guid -cin $sharedCodes })) {
                foreach ($row in $component.registry) { [void]$expectedKeys.Add($row.key) }
            }
            $sharedRoots = @('AppID', 'TypeLib', 'OPC.ServerList', 'OPC.ServerList.1')
            $expected = [ordered]@{
                registry = @($coowned.registry | Where-Object {
                    $row = $_
                    $isService = $row.path -ieq 'SYSTEM\CurrentControlSet\Services\OpcEnum' -or $row.path.StartsWith('SYSTEM\CurrentControlSet\Services\OpcEnum\', [StringComparison]::OrdinalIgnoreCase)
                    $isSharedView = @($sharedRoots | Where-Object { $row.path.StartsWith('SOFTWARE\Classes\' + $_ + '\', [StringComparison]::OrdinalIgnoreCase) -or $row.path.Equals('SOFTWARE\Classes\' + $_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
                    $isService -or ($expectedKeys.Contains($row.path) -and ($row.view -ceq 'Registry32' -or $isSharedView))
                })
                files = @($coowned.files | Where-Object { $_.path -iin $sharedPaths })
                services = @($coowned.services)
            }
            Invoke-Msi @('/x', $ProductCode) 'shared-runtime-uninstall' 0
            Equal-Snapshot ($expected | ConvertTo-Json -Depth 30) (Snapshot 'shared-owner-retained') 'Removal with a surviving MSI client'
            $clientInstaller = New-Object -ComObject WindowsInstaller.Installer
            try {
                foreach ($code in $sharedCodes) {
                    $clients = $clientInstaller.ComponentClients($code)
                    try {
                        if ((Get-SharedOpcComCount $clients) -ne 1 -or (Get-SharedOpcComItem $clients 0) -ieq $ProductCode) { throw 'Shared component lost its independent MSI client or retained our removed product.' }
                    } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($clients) }
                }
            } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($clientInstaller) }
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
