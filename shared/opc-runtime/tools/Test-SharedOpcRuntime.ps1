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
$Repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
Import-Module (Join-Path $Repo 'shared/installer/tools/WindowsMsiDatabase.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WindowsBuildSdk.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'SharedOpcAcceptance.psm1') -Force
if ([string]::IsNullOrWhiteSpace($env:GITHUB_EVENT_PATH) -or -not (Test-Path -LiteralPath $env:GITHUB_EVENT_PATH -PathType Leaf)) {
    throw 'Private Runtime CI event metadata is missing.'
}
Assert-SharedOpcPrivateCi ([IO.File]::ReadAllText($env:GITHUB_EVENT_PATH)) $env:GITHUB_REPOSITORY
$Msi = (Resolve-Path -LiteralPath $Msi).Path
if ((Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash -ine ([IO.File]::ReadAllText("$Msi.sha256").Split(' ')[0])) { throw 'Blocked input MSI checksum mismatch.' }
$Fixtures = @{}
foreach ($name in @('BoundaryProbe.dll', 'TestOnlyRepairSnapshot.dll', 'OpcRepairSnapshotTests.exe')) {
    $file = Join-Path (Split-Path $Msi -Parent) $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or -not (Test-Path -LiteralPath "$file.sha256" -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine ([IO.File]::ReadAllText("$file.sha256").Split(' ')[0])) {
        throw "Missing or mismatched TEST ONLY fixture: $name"
    }
    $Fixtures[$name] = $file
}
& (Join-Path $PSScriptRoot '../installer/tests/Test-RepairSnapshotInventory.ps1') -Executable $Fixtures['OpcRepairSnapshotTests.exe']
$Bindings = @(& $Fixtures['OpcRepairSnapshotTests.exe'] --manifest)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read TEST ONLY compiled repair bindings.' }
$Cases = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../installer/acceptance-cases.json') -Raw | ConvertFrom-Json
$Supported = @('lifecycle', 'rollback', 'foreign-registry', 'foreign-component', 'clients', 'shared-owner', 'descriptor-repair', 'descriptor-adversarial')
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
function Add-TestBinary([string] $name, [string] $path) {
    $record = $installer.CreateRecord(1)
    $view = $database.OpenView("INSERT INTO ``Binary`` (``Name``, ``Data``) VALUES ('$name', ?)")
    try {
        $null = $record.SetStream(1, $path)
        $null = $view.Execute($record)
    } finally {
        $null = $view.Close()
        foreach ($com in @($view, $record)) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) }
    }
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
    # Diagnostic DLL exists only in this disposable copy. It cannot validate or
    # preserve descriptors; the telemetry must not be mistaken for acceptance.
    $initialize = @(Read-WindowsMsiRecords $database 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = ''InstallInitialize''' @('Sequence'))
    if ($initialize.Count -ne 1 -or [int]$initialize[0].Sequence -le 1) { throw 'Cannot locate pre-transaction probe slot.' }
    $probeSequence = [int]$initialize[0].Sequence - 1
    $snapshotSequence = $probeSequence - 1
    $tamperSequence = $probeSequence - 2
    foreach ($sequence in @($tamperSequence, $snapshotSequence, $probeSequence)) {
        if ($sequence -le 1 -or @(Read-WindowsMsiRecords $database "SELECT ``Action`` FROM ``InstallExecuteSequence`` WHERE ``Sequence`` = $sequence" @('Action')).Count) { throw 'TEST ONLY pre-transaction sequence slot is occupied.' }
    }
    $appSearch = @(Read-WindowsMsiRecords $database 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = ''AppSearch''' @('Sequence'))
    if ($appSearch.Count -ne 1 -or [int]$appSearch[0].Sequence -ge $tamperSequence) { throw 'AppSearch must precede the TEST ONLY snapshot guard.' }
    Add-TestBinary 'IcemBoundaryProbe' $Fixtures['BoundaryProbe.dll']
    Execute-Sql 'INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES (''IcemBoundaryProbe'', 1, ''IcemBoundaryProbe'', ''InspectBoundary'')'
    Execute-Sql "INSERT INTO ``InstallExecuteSequence`` (``Action``, ``Condition``, ``Sequence``) VALUES ('IcemBoundaryProbe', '', $probeSequence)"
    Execute-Sql 'INSERT INTO `Property` (`Property`, `Value`) VALUES (''ICEM_BOUNDARY_PROBE'', ''raw" OTHER=1;#x00'')'
    # Link the real reader only in this disposable copy. The DLL DOES NOT prove
    # package/session trust and must never be published as a production guard.
    $registry = @(Read-WindowsMsiRecords $database 'SELECT `Registry`, `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`' @('Id', 'Root', 'Key', 'Name', 'Value', 'Component'))
    $components = @(Read-WindowsMsiRecords $database 'SELECT `Component`, `Attributes` FROM `Component`' @('Id', 'Attributes'))
    $transportNames = [Collections.Generic.List[string]]::new()
    $writerNames = [Collections.Generic.List[string]]::new()
    foreach ($binding in $Bindings) {
        $arch, $key, $name, $public32, $public64, $private = $binding.Split('|')
        $rows = @($registry | Where-Object { $_.Key -ceq $key -and $_.Name -ceq $name })
        if ($rows.Count -ne 1 -or $rows[0].Root -cne '2' -or $rows[0].Id -cnotmatch '^[A-Za-z0-9_.]+$' -or
            $rows[0].Value -cnotmatch '^#x(?:[0-9A-Fa-f]{2})+$') { throw 'Unexpected compiled native descriptor Registry row.' }
        $owner = @($components | Where-Object Id -CEQ $rows[0].Component)
        if ($owner.Count -ne 1 -or (([int]$owner[0].Attributes -band 256) -ne $(if ($arch -ceq 'x64') { 256 } else { 0 }))) {
            throw 'Descriptor Registry row has the wrong native component view.'
        }
        if (@(Read-WindowsMsiRecords $database "SELECT ``Property`` FROM ``Property`` WHERE ``Property`` = '$private'" @('Property')).Count) { throw 'TEST ONLY writer property already exists.' }
        $baselineHex = $rows[0].Value.Substring(2)
        Execute-Sql "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('$private', '$baselineHex')"
        Execute-Sql "UPDATE ``Registry`` SET ``Value`` = '#x[$private]' WHERE ``Registry`` = '$($rows[0].Id)'"
        $updated = @(Read-WindowsMsiRecords $database "SELECT ``Value`` FROM ``Registry`` WHERE ``Registry`` = '$($rows[0].Id)'" @('Value'))
        if ($updated.Count -ne 1 -or $updated[0].Value -cne "#x[$private]") { throw 'TEST ONLY binary writer representation differs from the intended Registry row.' }
        $writerNames.Add($private)
        foreach ($view in @(@($public32, 2), @($public64, 18))) {
            $property, $type = $view
            $locator = $property + '_LOC'
            Execute-Sql "INSERT INTO ``RegLocator`` (``Signature_``, ``Root``, ``Key``, ``Name``, ``Type``) VALUES ('$locator', 2, '$key', '$name', $type)"
            Execute-Sql "INSERT INTO ``AppSearch`` (``Property``, ``Signature_``) VALUES ('$property', '$locator')"
            $transportNames.Add($property)
        }
    }
    # This switch exists only in the disposable candidate and enables the
    # deliberate post-AppSearch property corruption used by negative tests.
    $transportNames.Add('ICEM_TEST_TAMPER')
    foreach ($entry in (Get-SharedOpcSnapshotPropertySets -Transport $transportNames.ToArray() -Writer $writerNames.ToArray())) {
        $property, $names = $entry.property, $entry.names
        $current = @(Read-WindowsMsiRecords $database "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$property'" @('Value'))
        if ($current.Count -gt 1) { throw 'Duplicate MSI policy property.' }
        $combined = if ($current.Count) { @($current[0].Value.Split(';') | Where-Object { $_ }) + @($names) } else { @($names) }
        if (@($combined | Sort-Object -Unique).Count -ne $combined.Count) { throw 'Duplicate TEST ONLY transfer/hidden property.' }
        $value = $combined -join ';'
        if ($current.Count) { Execute-Sql "UPDATE ``Property`` SET ``Value`` = '$value' WHERE ``Property`` = '$property'" }
        else { Execute-Sql "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('$property', '$value')" }
    }
    Add-TestBinary 'IcemTestSnapshot' $Fixtures['TestOnlyRepairSnapshot.dll']
    Execute-Sql 'INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES (''IcemTestSnapshotGuard'', 1, ''IcemTestSnapshot'', ''TestOnlyRepairSnapshot'')'
    Execute-Sql 'INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES (''IcemTestSnapshotTamper'', 1, ''IcemTestSnapshot'', ''TestOnlyTamperSnapshot'')'
    Execute-Sql "INSERT INTO ``InstallExecuteSequence`` (``Action``, ``Condition``, ``Sequence``) VALUES ('IcemTestSnapshotGuard', 'Installed AND NOT (REMOVE = `"ALL`")', $snapshotSequence)"
    Execute-Sql "INSERT INTO ``InstallExecuteSequence`` (``Action``, ``Condition``, ``Sequence``) VALUES ('IcemTestSnapshotTamper', 'Installed AND ICEM_TEST_TAMPER', $tamperSequence)"
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
    $withUi = @(Resolve-SharedOpcMsiArguments $arguments)
    $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32/msiexec.exe') -ArgumentList ($withUi + @('/norestart', '/l*v', ('"' + $log + '"'))) -PassThru
    try {
        if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw "$phase exceeded the disposable transaction budget; outcome unknown, no retry." }
        if ($process.ExitCode -ne $expected) { throw "$phase returned $($process.ExitCode), expected $expected; see $log" }
        $targetsRuntime = @($arguments | Where-Object { $_ -ceq ('"' + $Candidate + '"') -or $_ -ieq $ProductCode }).Count -gt 0
        if ($expected -eq 0 -and $targetsRuntime) {
            $text = [IO.File]::ReadAllText($log)
            foreach ($marker in @('ICEM_BOUNDARY_CONTEXT', 'ICEM_BOUNDARY_PROCESS', 'ICEM_BOUNDARY_THREAD', 'ICEM_BOUNDARY_ACCESS', 'ICEM_BOUNDARY_RESTRICTED', 'ICEM_BOUNDARY_IMAGE', 'ICEM_BOUNDARY_PROPERTY')) {
                if (-not $text.Contains($marker, [StringComparison]::Ordinal)) { throw "$phase lacks execute-side boundary evidence: $marker" }
            }
            if (-not [regex]::IsMatch($text, 'ICEM_BOUNDARY_PROPERTY characters=\d+;literalMatch=1')) { throw "$phase did not preserve the raw quoted MSI property through the DLL API." }
            if (-not [regex]::IsMatch($text, 'ICEM_BOUNDARY_RESTRICTED mediumSameSid=1;adminEnabled=0;openStatus=(?:\d+,){5}\d+(?![\d,])')) { throw "$phase lacks complete restricted-token process access evidence." }
            if ([regex]::Matches($text, 'ICEM_BOUNDARY_REG index=').Count -lt 8) { throw "$phase lacks both registry views for both fixed AppIDs." }
            if (@($arguments | Where-Object { $_ -cmatch '^/f[a-z]+$' }).Count) { Assert-SharedOpcSnapshotAcceptedLog $text }
            if ('/qb!-' -cin $arguments -and -not [regex]::IsMatch($text, '(?m)^Property\([CS]\): UILevel = 3\r?$')) { throw "$phase did not log basic UI level." }
        }
    } finally { $process.Dispose() }
}
function Build-ForeignOwner([string] $proxy, [string] $enumerator, [string] $destination) {
    Push-Location $Work
    try {
        $null = Assert-WindowsBuildSdk
        & wix build (Join-Path $PSScriptRoot '../installer/tests/ForeignOwner.wxs') -arch x86 -wx -d "ProxyPayload=$proxy" -d "EnumPayload=$enumerator" -o $destination | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Cannot build independent MSI component owner fixture.' }
    } finally { Pop-Location }
}
function Damage-RepairResources {
    # Only resources installed by this transaction on this clean disposable runner.
    $dll = Join-Path $env:WINDIR 'SysWOW64/opccomn_ps.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Owned DLL missing before damage injection.' }
    [IO.File]::Delete($dll)
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)) {
        $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        try {
            $key = $machine.OpenSubKey('SOFTWARE\Classes\Interface\{13486D50-4821-11D2-A494-3CB306C10000}\NumMethods', $true)
            if ($null -eq $key) { throw 'The installed proxy method-count registration is missing before damage injection.' }
            try { $key.DeleteValue('', $true) } finally { $key.Dispose() }
        } finally { $machine.Dispose() }
    }
}
$report = [ordered]@{ schema_version = 1; case = $Case; result = 'FAIL'; product_commit = $env:GITHUB_SHA; input_sha256 = (Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash.ToLowerInvariant(); candidate_not_distributable = $true; package_session_trust_verified = $false }
try {
    $before = Snapshot 'before'
    $initial = Read-SharedOpcSnapshot $before
    if ($initial.registry.Count -ne 0 -or $initial.files.Count -ne 0 -or $initial.services.Count -ne 0) { throw 'Disposable runner is not OPC-clean; refusing to adopt or clean existing components.' }
    switch ($Case) {
        'lifecycle' {
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'fresh-install' 0
            $installed = Snapshot 'installed'
            if ((Read-SharedOpcSnapshot $installed).files.Count -ne 18) { throw 'Runtime did not install the complete 18-file native inventory.' }
            # A repair exit code without restoring resources is not PASS.
            Damage-RepairResources
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
                    & dotnet build (Join-Path $PSScriptRoot '../installer/tests/OpcComClient.csproj') -c Release "-p:PlatformTarget=$arch" "-p:BaseIntermediateOutputPath=$clientRoot/obj/" "-p:OutputPath=$clientRoot/bin/" -v:minimal | Out-Host
                    if ($LASTEXITCODE -ne 0) { throw "Independent $arch COM client did not compile" }
                } finally { Pop-Location }
            }
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'client-install' 0
            $clientInstalled = Snapshot 'client-installed'
            Damage-RepairResources
            Invoke-Msi @('/fomus', ('"' + $Candidate + '"')) 'client-repair' 0
            Equal-Snapshot $clientInstalled (Snapshot 'client-repaired') 'Repair before independent clients'
            Damage-RepairResources
            Invoke-Msi @('/famus', ('"' + $Candidate + '"')) 'client-force-repair' 0
            if ((Read-SharedOpcSnapshot (Snapshot 'client-force-repaired')).files.Count -ne 18) { throw 'Force repair did not restore the complete native file inventory.' }
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
            $native = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../installer/native-runtime-inventory.json') -Raw | ConvertFrom-Json -AsHashtable
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
        'descriptor-repair' {
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'descriptor-install' 0
            if ((Read-SharedOpcSnapshot (Snapshot 'descriptor-installed')).files.Count -ne 18) { throw 'Runtime did not install the complete 18-file native inventory.' }
            $native = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../installer/native-runtime-inventory.json') -Raw | ConvertFrom-Json -AsHashtable
            $securityRows = @(
                foreach ($component in $native.components) {
                    foreach ($row in $component.registry) {
                        if (@($row.values | Where-Object { $_.name -cin @('AccessPermission', 'LaunchPermission') }).Count -gt 0) {
                            [pscustomobject]@{ arch = $component.arch; row = $row }
                        }
                    }
                }
            )
            $securityPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            if ($securityRows.Count -ne 2) { throw 'Expected exactly two owned AppID security rows.' }
            foreach ($security in $securityRows) {
                $row = $security.row
                if ($row.key -cnotmatch '^SOFTWARE\\Classes\\AppID\\\{[0-9A-F-]{36}\}$' -or
                    $row.key -notin $Keys -or -not $securityPaths.Add($row.key) -or $security.arch -cnotin @('x86', 'x64')) {
                    throw 'Unreviewed or duplicate owned AppID security row.'
                }
                $view = if ($security.arch -ceq 'x86') { [Microsoft.Win32.RegistryView]::Registry32 } else { [Microsoft.Win32.RegistryView]::Registry64 }
                $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
                try {
                    $key = $machine.OpenSubKey($row.key, $true)
                    if ($null -eq $key) { throw 'Owned AppID missing after fresh install.' }
                    try {
                        foreach ($name in @('AccessPermission', 'LaunchPermission')) {
                            if (@($row.values | Where-Object name -CEQ $name).Count -ne 1 -or $key.GetValueKind($name) -ne [Microsoft.Win32.RegistryValueKind]::Binary) {
                                throw 'Owned AppID descriptor missing or ambiguous.'
                            }
                            $baseline = [byte[]]$key.GetValue($name)
                            $bytes = New-SharedOpcCustomizedDescriptor $baseline $name 'S-1-5-21-101-202-303-1001'
                            if ([Convert]::ToBase64String($baseline) -ceq [Convert]::ToBase64String($bytes)) { throw 'Test customization did not change the descriptor.' }
                            $key.SetValue($name, $bytes, [Microsoft.Win32.RegistryValueKind]::Binary)
                            if ([Convert]::ToBase64String([byte[]]$key.GetValue($name)) -cne [Convert]::ToBase64String($bytes)) { throw 'Test customization did not persist exactly.' }
                        }
                    } finally { $key.Dispose() }
                } finally { $machine.Dispose() }
            }
            $customized = Snapshot 'descriptor-customized'
            Damage-RepairResources
            $null = Snapshot 'descriptor-damaged'
            Invoke-Msi @('/fomus', ('"' + $Candidate + '"')) 'descriptor-repair' 0
            Equal-Snapshot $customized (Snapshot 'descriptor-repaired') 'Repair with customized AppID permissions'
            Damage-RepairResources
            Invoke-Msi @('/fomus', ('"' + $Candidate + '"'), '/qb!-') 'descriptor-basic-ui-repair' 0
            Equal-Snapshot $customized (Snapshot 'descriptor-basic-ui-repaired') 'Basic UI repair with customized AppID permissions'
            Damage-RepairResources
            $null = Snapshot 'descriptor-force-damaged'
            Invoke-Msi @('/famus', ('"' + $Candidate + '"')) 'descriptor-force-repair' 0
            Equal-Snapshot $customized (Snapshot 'descriptor-force-repaired') 'Force-all repair with customized AppID permissions'
            Invoke-Msi @('/x', $ProductCode) 'descriptor-uninstall' 0
            Equal-Snapshot $before (Snapshot 'descriptor-uninstalled') 'Uninstall after customized repair'
        }
        'descriptor-adversarial' {
            Invoke-Msi @('/i', ('"' + $Candidate + '"')) 'adversarial-install' 0
            $parts = $Bindings[0].Split('|')
            $view = if ($parts[0] -ceq 'x86') { [Microsoft.Win32.RegistryView]::Registry32 } else { [Microsoft.Win32.RegistryView]::Registry64 }
            $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
            try {
                $key = $machine.OpenSubKey($parts[1], $true)
                if ($null -eq $key) { throw 'Owned AppID missing before negative tests.' }
                try {
                    if ($key.GetValueKind($parts[2]) -ne [Microsoft.Win32.RegistryValueKind]::Binary) { throw 'Owned descriptor is not binary.' }
                    $original = [byte[]]$key.GetValue($parts[2], $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    if ($original.Length -eq 0) { throw 'Owned descriptor is empty before negative tests.' }
                    function Assert-Refusal([string] $phase, [string[]] $extra) {
                        $state = Snapshot "$phase-before"
                        $arguments = @(Get-SharedOpcAdversarialRepairArguments -Candidate $Candidate -Extra $extra)
                        Invoke-Msi $arguments $phase 1603
                        $log = [IO.File]::ReadAllText((Join-Path $EvidenceDirectory "$phase.log"))
                        Assert-SharedOpcSnapshotRefusalLog $log
                        if (@($extra | Where-Object { $_ -clike 'ICEM_TEST_TAMPER=*' }).Count -and
                            -not $log.Contains('ICEM_TEST_SNAPSHOT_TAMPERED', [StringComparison]::Ordinal)) { throw 'Property tampering action did not execute.' }
                        if ('/qb!-' -cin $extra -and -not [regex]::IsMatch($log, '(?m)^Property\([CS]\): UILevel = 3\r?$')) { throw 'Negative case did not run with basic UI level.' }
                        Equal-Snapshot $state (Snapshot "$phase-after") "Pre-transaction refusal $phase"
                    }
                    foreach ($mode in @('missing', 'tamper', 'odd', 'injection')) {
                        Assert-Refusal "property-$mode" @("ICEM_TEST_TAMPER=$mode")
                    }
                    Assert-Refusal 'property-tamper-basic-ui' @('ICEM_TEST_TAMPER=tamper', '/qb!-')
                    foreach ($mode in @('missing', 'empty', 'string', 'dword', 'malformed')) {
                        try {
                            switch ($mode) {
                                'missing' { $key.DeleteValue($parts[2], $true) }
                                'empty' { $key.SetValue($parts[2], [byte[]]::new(0), [Microsoft.Win32.RegistryValueKind]::Binary) }
                                'string' { $key.SetValue($parts[2], ('#x' + [Convert]::ToHexString($original)), [Microsoft.Win32.RegistryValueKind]::String) }
                                'dword' { $key.SetValue($parts[2], 1, [Microsoft.Win32.RegistryValueKind]::DWord) }
                                'malformed' { $key.SetValue($parts[2], [Convert]::FromHexString('01000480000000000000000000000000140000000200000000000000'), [Microsoft.Win32.RegistryValueKind]::Binary) }
                            }
                            Assert-Refusal "registry-$mode" @()
                        } finally { $key.SetValue($parts[2], $original, [Microsoft.Win32.RegistryValueKind]::Binary) }
                    }
                    # HKCU is a decoy only. Refuse if anything existed there;
                    # remove precisely this key created by this disposable test.
                    $user = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
                    try {
                        $existing = $user.OpenSubKey($parts[1], $false)
                        if ($null -ne $existing) { $existing.Dispose(); throw 'HKCU decoy already exists; refusing to modify it.' }
                        $decoy = $user.CreateSubKey($parts[1], $true)
                        if ($null -eq $decoy) { throw 'Cannot create the disposable HKCU decoy.' }
                        try {
                            $decoy.SetValue($parts[2], $original, [Microsoft.Win32.RegistryValueKind]::Binary)
                            try {
                                $key.DeleteValue($parts[2], $true)
                                $property = if ($parts[0] -ceq 'x86') { $parts[3] } else { $parts[4] }
                                Assert-Refusal 'hkcu-decoy' @($property + '=#x' + [Convert]::ToHexString($original))
                            } finally { $key.SetValue($parts[2], $original, [Microsoft.Win32.RegistryValueKind]::Binary) }
                        } finally {
                            $decoy.Dispose()
                            $user.DeleteSubKey($parts[1], $true)
                        }
                    } finally { $user.Dispose() }
                } finally { $key.Dispose() }
            } finally { $machine.Dispose() }
            Invoke-Msi @('/x', $ProductCode) 'adversarial-uninstall' 0
            Equal-Snapshot $before (Snapshot 'adversarial-uninstalled') 'Uninstall after negative descriptor cases'
        }
        default { throw 'Acceptance case has no implementation.' }
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
