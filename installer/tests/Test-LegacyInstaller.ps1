#requires -Version 7.4
[CmdletBinding()]
param([switch] $AllowMachineChanges)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or ($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowMachineChanges)) {
    throw 'Use a disposable Windows CI runner or explicitly pass -AllowMachineChanges on a disposable Windows VM.'
}
$Root = Split-Path $PSScriptRoot -Parent
$Repository = Split-Path $Root -Parent
Import-Module (Join-Path $Root 'BuildPaths.psm1') -Force
$Work = Join-Path ([IO.Path]::GetTempPath()) ('icem-legacy-contract-' + [guid]::NewGuid().ToString('N'))
$Install = Join-Path $Work "installed $([char]0x0442) files"
$IcemRoot = Join-Path $Work 'iCem target'
$Modules = Join-Path $IcemRoot 'Bin/Modules'
$Marker = Join-Path $Install 'operator-not-msi-owned.ini'
$MarkerText = 'operator data must survive'
$ProductCodes = [Collections.Generic.List[string]]::new()
$FileOwners = [Collections.Generic.List[object]]::new()
[IO.Directory]::CreateDirectory($Work) | Out-Null
function Test-BuildOutputPaths {
    $Tree = Join-Path $Work 'build output boundaries'
    $Delivery = Join-Path $Tree 'src/bin/delivery'
    foreach ($Case in @(
        @{ path = $Delivery; directory = $Delivery; inside = $true },
        @{ path = (Join-Path $Delivery 'obj'); directory = $Delivery; inside = $true },
        @{ path = $Delivery.ToUpperInvariant(); directory = ($Delivery + '\'); inside = $true },
        @{ path = (Join-Path $Tree 'src/bin/delivery-other'); directory = $Delivery; inside = $false },
        @{ path = (Join-Path $Tree 'src/bin'); directory = $Delivery; inside = $false }
    )) {
        if ((Test-BuildPathWithin $Case.path $Case.directory) -ne $Case.inside) { throw 'Build directory containment boundary failed' }
    }
    $Sentinel = Join-Path $Delivery 'monitor.msi'
    $Nested = Join-Path $Delivery 'obj/required-payload.dat'
    $Unrelated = Join-Path $Tree 'src/obj'
    $Sibling = Join-Path $Tree 'src/bin/delivery-other/obj'
    foreach ($Directory in @((Split-Path $Nested -Parent), $Unrelated, $Sibling)) {
        [IO.Directory]::CreateDirectory($Directory) | Out-Null
    }
    [IO.File]::WriteAllText($Sentinel, 'final MSI')
    [IO.File]::WriteAllText($Nested, 'delivery resource')
    $Candidates = @(Get-ChildItem -LiteralPath $Tree -Recurse -Directory | Where-Object { $_.Name -in @('bin', 'obj') })
    foreach ($Directory in @($Candidates | Where-Object { -not (Test-BuildPathOverlap $_.FullName $Delivery) } | Sort-Object { $_.FullName.Length } -Descending)) {
        Remove-Item -LiteralPath $Directory.FullName -Recurse -Force
    }
    if ((Get-Content -Raw -LiteralPath $Sentinel) -cne 'final MSI' -or
        (Get-Content -Raw -LiteralPath $Nested) -cne 'delivery resource' -or
        (Test-Path -LiteralPath $Unrelated) -or (Test-Path -LiteralPath $Sibling)) { throw 'Cleanup removed delivery artifacts or retained unrelated intermediates' }
    # Invoke the real builder's early guard in a disposable checkout layout.
    $Builder = Join-Path $Tree 'installer'
    $Reserved = Join-Path $Builder 'stage'
    [IO.Directory]::CreateDirectory($Reserved) | Out-Null
    $StageMarker = Join-Path $Reserved 'must-survive.txt'
    [IO.File]::WriteAllText($StageMarker, 'existing stage')
    foreach ($Name in @('build-msi.ps1', 'BuildPaths.psm1')) {
        Copy-Item -LiteralPath (Join-Path $Root $Name) -Destination (Join-Path $Builder $Name)
    }
    foreach ($Output in @($Reserved, (Join-Path $Reserved 'delivery'))) {
        $Rejected = $false
        try { & (Join-Path $Builder 'build-msi.ps1') -OutputDir $Output -NoPause }
        catch {
            if (-not $_.Exception.Message.Contains('OutputDir cannot be inside reserved installer/stage')) { throw }
            $Rejected = $true
        }
        if (-not $Rejected -or (Get-Content -Raw -LiteralPath $StageMarker) -cne 'existing stage') { throw 'Reserved stage guard ran after destructive staging/build work' }
    }
}
function Installed-Products {
    @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' | ForEach-Object {
        $Entry = Get-ItemProperty -LiteralPath $_.PSPath
        if ($Entry.PSObject.Properties['DisplayName'] -and $Entry.DisplayName -in @('iCem Data Monitor','iCem Data Monitor Rust Development')) { $Entry }
    })
}
function Read-MsiProperty([string] $Path, [string] $Name) {
    $Installer = New-Object -ComObject WindowsInstaller.Installer
    $Database = $null; $View = $null; $Row = $null
    try {
        $Database = $Installer.OpenDatabase($Path, 0)
        $View = $Database.OpenView(('SELECT `Value` FROM `Property` WHERE `Property` = ''' + $Name + ''''))
        $null = $View.Execute()
        $Row = $View.Fetch()
        if (-not $Row) { throw "Missing MSI property $Name" }
        $Value = $Row.StringData(1)
        if ($Value -isnot [string]) { throw "MSI property $Name must be one string, got $($Value.GetType().FullName)" }
        return $Value
    } finally {
        if ($View) { $null = $View.Close() }
        foreach ($Object in @($Row, $View, $Database, $Installer)) {
            if ($Object) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($Object) }
        }
    }
}
function Invoke-Msi([string[]] $Arguments, [string] $Label, [int] $ExpectedExit = 0, [switch] $InitialPaths) {
    $Log = Join-Path $Work "$Label.log"
    $Info = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32/msiexec.exe'))
    $Info.UseShellExecute = $false
    # Windows Installer's PROPERTY="value" grammar must keep the property name
    # outside the quotes; do not quote the whole PROPERTY=value token.
    $Paths = if ($InitialPaths) { @("MONITORROOT=$Install", "ICEMDIR=$IcemRoot") } else { @() }
    $Tokens = @($Arguments + $Paths + @('/qn', '/norestart', '/l*v', $Log))
    $Info.Arguments = (@($Tokens | ForEach-Object {
        if ($_.Contains('"')) { throw 'MSI test arguments cannot contain embedded quotes' }
        if ($_ -match '^([A-Z][A-Z0-9_]*)=(.*)$') { $Matches[1] + '="' + $Matches[2] + '"' }
        elseif ($_.StartsWith('/')) { $_ }
        else { '"' + $_ + '"' }
    }) -join ' ')
    [IO.File]::WriteAllText((Join-Path $Work "$Label.launch.log"), $Info.FileName + ' ' + $Info.Arguments)
    Write-Host "MSI ${Label}: $($Info.Arguments)"
    $Process = [Diagnostics.Process]::Start($Info)
    try {
        if (-not $Process.WaitForExit(180000)) {
            Get-Process msiexec -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, MainWindowTitle | Out-String | Set-Content -LiteralPath (Join-Path $Work "$Label.timeout.log")
            $Process.Kill($true); $null = $Process.WaitForExit(5000)
            $Tail = if (Test-Path -LiteralPath $Log) { (Get-Content -LiteralPath $Log -Tail 35) -join "`n" } else { 'No MSI log was created' }
            throw "MSI $Label exceeded 180 seconds; no retry. $Tail"
        }
        if ($Process.ExitCode -ne $ExpectedExit) {
            $Tail = if (Test-Path -LiteralPath $Log) { (Get-Content -LiteralPath $Log -Tail 35) -join "`n" } else { 'No MSI log' }
            throw "MSI $Label exit $($Process.ExitCode), expected ${ExpectedExit}: $Tail"
        }
    } finally { $Process.Dispose() }
}
function Start-FileOwner([string] $Executable, [string] $Resource, [string] $Name, [string] $Mode) {
    $Ready = Join-Path $Work ($Name + '.ready')
    $Info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $Info.UseShellExecute = $false
    foreach ($Argument in @($Resource, $Ready, $Mode)) { $Info.ArgumentList.Add($Argument) }
    $Process = [Diagnostics.Process]::Start($Info)
    $Owner = [pscustomobject]@{ process=$Process; ready=$Ready }
    $FileOwners.Add($Owner)
    $Clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Ready)) {
        if ($Process.HasExited -or $Clock.Elapsed.TotalSeconds -gt 10) { throw "Lock fixture $Name did not become ready" }
        Start-Sleep -Milliseconds 50
    }
    return $Owner
}
function Assert-OwnerStopped($Owner, [bool] $Graceful) {
    if (-not $Owner.process.WaitForExit(5000)) { throw 'MSI returned success with a live file owner' }
    $DidClose = Test-Path -LiteralPath ($Owner.ready + '.closed')
    if ($DidClose -ne $Graceful) {
        $Events = Get-Content -Raw -LiteralPath ($Owner.ready + '.events.log') -ErrorAction SilentlyContinue
        throw "Unexpected shutdown path: expected graceful=$Graceful, marker=$DidClose, exit=$($Owner.process.ExitCode), events=<$Events>"
    }
}
function New-FixtureMsi([string] $Version, [string] $Label, [switch] $FailDuringTransaction) {
    $Build = Join-Path $Work $Label
    $Stage = Join-Path $Build 'stage'
    foreach ($Directory in @('host/templates','shell','emulator','addin')) { [IO.Directory]::CreateDirectory((Join-Path $Stage $Directory)) | Out-Null }
    foreach ($Relative in @('host/iCemDataMonitor.Host.exe','shell/iCemDataMonitor.WinUI.exe','emulator/iCemEmulator.exe')) {
        Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32/where.exe') -Destination (Join-Path $Stage $Relative)
    }
    # Deliberately synthetic bytes. This test proves MSI mechanics, never SDK/runtime compatibility.
    [IO.File]::WriteAllText((Join-Path $Stage 'addin/Burservis.iCem.DataMonitor.dll'), 'SYNTHETIC installer fixture')
    [IO.File]::WriteAllText((Join-Path $Stage 'host/templates/fixture.json'), $Version)
    $Payload = Join-Path $Build 'payload.wxs'
    & (Join-Path $Root 'gen-payload.ps1') -StageRoot $Stage -OutputPath $Payload -RootFolders @('shell','host','emulator') -EmulatorFolder '' | Out-Host
    $Source = Join-Path $Build 'Product.wxs'
    $Xml = [xml](Get-Content -Raw -LiteralPath (Join-Path $Root 'Product.wxs'))
    $Ns = [Xml.XmlNamespaceManager]::new($Xml.NameTable); $Ns.AddNamespace('w','http://wixtoolset.org/schemas/v4/wxs')
    $Package = $Xml.SelectSingleNode('/w:Wix/w:Package',$Ns)
    if ($FailDuringTransaction) {
        # Execute deferred file/registry changes before the test-only error, inside rollback boundary.
        $Action = $Xml.CreateElement('CustomAction',$Ns.LookupNamespace('w'))
        $Action.SetAttribute('Id','FixtureFailure'); $Action.SetAttribute('Error','Expected fixture rollback')
        $null = $Package.AppendChild($Action)
        $Sequence = $Xml.CreateElement('InstallExecuteSequence',$Ns.LookupNamespace('w'))
        $Execute = $Xml.CreateElement('InstallExecute',$Ns.LookupNamespace('w'))
        $Execute.SetAttribute('Before','InstallFinalize'); $null = $Sequence.AppendChild($Execute)
        $Custom = $Xml.CreateElement('Custom',$Ns.LookupNamespace('w'))
        $Custom.SetAttribute('Action','FixtureFailure'); $Custom.SetAttribute('After','InstallExecute'); $Custom.SetAttribute('Condition','1')
        $null = $Sequence.AppendChild($Custom); $null = $Package.AppendChild($Sequence)
    }
    $Xml.Save($Source)
    $Msi = Join-Path $Build 'fixture.msi'
    & wix build -arch x64 -wx -ext WixToolset.UI.wixext/5.0.2 -ext WixToolset.Util.wixext/5.0.2 -define "MsiVersion=$Version" -define "AssetsDir=$(Join-Path $Root 'assets')" -intermediateFolder (Join-Path $Build 'obj') -o $Msi $Source $Payload | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Legacy fixture MSI compilation failed' }
    $Code = Read-MsiProperty $Msi 'ProductCode'
    $ProductCodes.Add($Code)
    return [pscustomobject]@{ msi=$Msi; code=$Code; version=$Version; stage=$Stage }
}
function New-ForeignFamilyFixture {
    $Build = Join-Path $Work 'foreign-family'; [IO.Directory]::CreateDirectory($Build) | Out-Null
    $Source = Join-Path $Build 'foreign.wxs'
    # Only the UpgradeCode models the other family. No Rust application/components are installed.
    [IO.File]::WriteAllText($Source, @'
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="iCem Data Monitor Rust Development" Manufacturer="Installer contract fixture" Version="0.0.1" UpgradeCode="7DDC9268-182F-432C-AD9A-E61BBC967F1A" Scope="perMachine">
    <StandardDirectory Id="ProgramFiles64Folder">
      <Component Id="ForeignFamily" Guid="{AD3518E7-90FE-48EE-A690-0267AE61529C}">
        <RegistryValue Root="HKLM" Key="Software\Burservis\InstallerContractFixture" Name="ForeignFamily" Value="1" Type="integer" KeyPath="yes" />
      </Component>
    </StandardDirectory>
    <Feature Id="Main"><ComponentRef Id="ForeignFamily" /></Feature>
  </Package>
</Wix>
'@)
    $Msi = Join-Path $Build 'foreign.msi'
    & wix build -arch x64 -wx -o $Msi $Source | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Foreign family fixture compilation failed' }
    $Code = Read-MsiProperty $Msi 'ProductCode'; $ProductCodes.Add($Code)
    return [pscustomobject]@{ msi=$Msi; code=$Code }
}
function Assert-Installed($Fixture) {
    $Products = @(Installed-Products)
    if ($Products.Count -ne 1 -or $Products[0].PSChildName -ne $Fixture.code -or $Products[0].DisplayVersion -ne $Fixture.version) { throw 'Expected exactly one legacy registration' }
    $Expected = @{}
    foreach ($File in Get-ChildItem -LiteralPath $Fixture.stage -Recurse -File) {
        $Relative = [IO.Path]::GetRelativePath($Fixture.stage,$File.FullName)
        $Parts = $Relative -split '[\\/]',2
        $Target = Join-Path $(if ($Parts[0] -eq 'addin') { $Modules } else { $Install }) $Parts[1]
        if (-not (Test-Path -LiteralPath $Target -PathType Leaf) -or (Get-FileHash -LiteralPath $Target).Hash -cne (Get-FileHash -LiteralPath $File.FullName).Hash) { throw "Installed file hash differs: $Relative" }
        $Expected[$Target] = $true
    }
    foreach ($File in @(Get-ChildItem -LiteralPath $Install -Recurse -File) + @(Get-ChildItem -LiteralPath $Modules -Recurse -File)) {
        if ($File.FullName -cne $Marker -and -not $Expected.ContainsKey($File.FullName)) { throw "Unexpected owned payload file: $($File.FullName)" }
    }
    if ((Test-Path -LiteralPath $Marker) -and (Get-Content -Raw -LiteralPath $Marker) -cne $MarkerText) { throw 'Operator data was modified' }
}
function Invoke-InstallerUi($Fixture,[string] $Label,[string] $Mode = 'repair',[string] $Expected = '1',[switch] $ApplyOptOut, [switch] $ExplicitZero) {
    $Info = [Diagnostics.ProcessStartInfo]::new($UiProbe); $Info.UseShellExecute = $false
    foreach ($Argument in @($Fixture.msi,(Join-Path $Work "$Label.log"),$Expected,([int][bool]$ExplicitZero).ToString(),([int][bool]$ApplyOptOut).ToString(),$Mode,$IcemRoot,$Install)) { $Info.ArgumentList.Add($Argument) }
    $Process = [Diagnostics.Process]::Start($Info)
    try {
        if (-not $Process.WaitForExit(240000)) { $Process.Kill($true); $null = $Process.WaitForExit(5000); throw 'Legacy UI probe timed out' }
        Get-Content -LiteralPath (Join-Path $Work "$Label.log.ui.log") | Write-Host
        if ($Process.ExitCode -ne 0) { throw 'Legacy UI probe failed' }
    } finally { $Process.Dispose() }
}
try {
    Test-BuildOutputPaths
    $Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { if (-not [Security.Principal.WindowsPrincipal]::new($Identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run elevated on a disposable VM' } } finally { $Identity.Dispose() }
    if (@(Installed-Products).Count) { throw 'Existing legacy/Rust MSI found; refusing to touch it' }
    $RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $Shortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'iCem Data Monitor.lnk'
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Existing monitor options found; refusing to touch them' }
    & wix extension add -g WixToolset.UI.wixext/5.0.2 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'WiX UI extension restore failed' }
    & wix extension add -g WixToolset.Util.wixext/5.0.2 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'WiX Util extension restore failed' }
    # Brand assets are generated by the same builder used for production.
    & (Join-Path $Root 'gen-ui-bmps.ps1') | Out-Host
    $N = New-FixtureMsi '0.0.1' 'N'
    $Same = New-FixtureMsi '0.0.1' 'same-version-rebuild'
    $Next = New-FixtureMsi '0.0.2' 'N-plus-one'
    $Failed = New-FixtureMsi '0.0.3' 'rollback' -FailDuringTransaction
    if ($N.code -eq $Same.code) { throw 'Rebuild must receive a new ProductCode' }
    $Framework = Join-Path $env:SystemRoot 'Microsoft.NET/Framework64/v4.0.30319'
    $UiProbe = Join-Path $Work 'LegacyUiProbe.exe'
    & (Join-Path $Framework 'csc.exe') /nologo /target:exe "/out:$UiProbe" "/reference:$Framework/WPF/UIAutomationClient.dll" "/reference:$Framework/WPF/UIAutomationTypes.dll" "/reference:$Framework/WPF/WindowsBase.dll" (Join-Path $PSScriptRoot 'LegacyUiProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Legacy UI fixture compilation failed' }
    Invoke-InstallerUi $N 'fresh-cancel' 'fresh-cancel' '0'
    if (@(Installed-Products).Count -or (Test-Path -LiteralPath $Modules) -or (Test-Path -LiteralPath $Install)) { throw 'Fresh cancellation changed installation' }
    Invoke-InstallerUi $N 'fresh-custom-path-install' 'fresh-install' '0'
    Assert-Installed $N
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Defaults enabled opt-ins' }
    # Explicit path properties must win over a stale saved directory.
    Set-ItemProperty -LiteralPath 'HKLM:\Software\Burservis\iCemDataMonitor\Installer' -Name IcemDirectory -Value (Join-Path $Work 'stale saved path')
    Invoke-Msi @('/i',$N.msi) 'explicit-path-wins' -InitialPaths
    Assert-Installed $N
    Invoke-Msi @('/i',$N.msi,'CREATEDESKTOPSHORTCUT=1','ADDAUTOSTART=1') 'enable-options'
    Assert-Installed $N
    if (-not (Test-Path -LiteralPath $Shortcut) -or (Get-ItemPropertyValue -LiteralPath $RunKey -Name iCemDataMonitorHost) -cne ('"' + (Join-Path $Install 'iCemDataMonitor.Host.exe') + '" --autostart')) { throw 'Native options missing or wrong autostart path' }
    [IO.File]::WriteAllText($Marker,$MarkerText)
    Remove-Item -LiteralPath (Join-Path $Install 'iCemDataMonitor.WinUI.exe')
    [IO.File]::WriteAllText((Join-Path $Install 'templates/fixture.json'),'corrupt')
    Remove-Item -LiteralPath (Join-Path $Modules 'Burservis.iCem.DataMonitor.dll')
    Invoke-Msi @('/i',$N.msi) 'plain-repeat-repairs'
    Assert-Installed $N
    Invoke-Msi @('/fa',$N.code) 'explicit-repair'
    Assert-Installed $N
    Invoke-InstallerUi $N 'explicit-zero-checkboxes' 'repair' '0' -ExplicitZero
    Invoke-InstallerUi $N 'maintenance-cancel'
    Assert-Installed $N
    Invoke-Msi @('/i',$Same.msi) 'same-version-rebuild'
    Assert-Installed $Same
    Invoke-Msi @('/i',$Next.msi) 'upgrade'
    Assert-Installed $Next
    if (-not (Test-Path -LiteralPath $Shortcut)) { throw 'Upgrade lost saved options' }
    $StationPath = Join-Path $env:ProgramData 'Halliburton/iCem/DataMonitoring'
    $BeforeAcl = (Get-Acl -LiteralPath $StationPath).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
    $BeforeState = Get-ItemProperty -LiteralPath 'HKLM:\Software\Burservis\iCemDataMonitor\Installer'
    Invoke-Msi @('/i',$Failed.msi,'CREATEDESKTOPSHORTCUT=0','ADDAUTOSTART=0') 'rollback-after-old-removal' 1603
    $RollbackLog = Get-Content -Raw -LiteralPath (Join-Path $Work 'rollback-after-old-removal.log')
    foreach ($Action in @('RemoveExistingProducts','InstallFiles','InstallExecute','FixtureFailure')) {
        if ($RollbackLog -notmatch ('Action start .*: ' + $Action + '\.')) { throw "Rollback did not reach $Action" }
    }
    # /qn logs native script operations, not the full-UI "Rollback:" progress text.
    # Restrict evidence to AFTER this fixture's failed action; normal execution
    # also emits RollbackInfo and must never satisfy the rollback proof.
    $FailureEnd = [regex]::Match($RollbackLog, '(?m)^Action ended [^\r\n]*: FixtureFailure\. Return value 3\.\r?$')
    if (-not $FailureEnd.Success) { throw 'Injected failure did not return failure status' }
    $RollbackScript = $RollbackLog.Substring($FailureEnd.Index + $FailureEnd.Length)
    foreach ($Operation in @('Executing op: Header\([^\r\n]*ScriptType=2,',
        'Executing op: RollbackInfo\([^\r\n]*RollbackAction=Rollback,',
        'Executing op: RegRemoveValue\(', 'Executing op: FileCopy\(SourceName=[^\r\n]*\.rbf,',
        'Executing op: RegAddValue\(Name=RuntimeDirectory,')) {
        if ($RollbackScript -notmatch $Operation) { throw "Missing executed rollback operation: $Operation" }
    }
    Assert-Installed $Next
    $AfterAcl = (Get-Acl -LiteralPath $StationPath).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
    if ($AfterAcl -cne $BeforeAcl) { throw 'Rollback changed station directory access permissions' }
    $AfterState = Get-ItemProperty -LiteralPath 'HKLM:\Software\Burservis\iCemDataMonitor\Installer'
    foreach ($Name in @('RuntimeDirectory','IcemDirectory','DesktopChoice','AutostartChoice')) {
        if ($BeforeState.$Name -cne $AfterState.$Name) { throw "Rollback changed saved $Name" }
    }
    if (-not (Test-Path -LiteralPath $Shortcut) -or
        (Get-ItemPropertyValue -LiteralPath $RunKey -Name iCemDataMonitorHost) -cne ('"' + (Join-Path $Install 'iCemDataMonitor.Host.exe') + '" --autostart')) { throw 'Rollback lost shortcut/autostart' }
    Invoke-Msi @('/i',$N.msi) 'permitted-downgrade'
    Assert-Installed $N
    # Real file-owner tests share only the disposable C# lock fixture with Rust.
    $Locks = Join-Path $Work 'locks'; [IO.Directory]::CreateDirectory($Locks) | Out-Null
    $OwnerExe = Join-Path $Locks ('icem-lock-' + [guid]::NewGuid().ToString('N') + '.exe')
    & (Join-Path $Framework 'csc.exe') /nologo /target:winexe /platform:x64 "/out:$OwnerExe" "/reference:$Framework/System.Windows.Forms.dll" "/reference:$Framework/System.Drawing.dll" (Join-Path $Repository 'rust/installer/tests/LockedFileOwner.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'File-owner fixture compilation failed' }
    $ForeignFile = Join-Path $Locks 'foreign.dll'; [IO.File]::WriteAllText($ForeignFile,'unrelated')
    $Foreign = Start-FileOwner $OwnerExe $ForeignFile 'foreign' 'stubborn'
    $Addin = Join-Path $Modules 'Burservis.iCem.DataMonitor.dll'
    $Graceful = Start-FileOwner $OwnerExe $Addin 'graceful' 'graceful'
    Invoke-InstallerUi $N 'cancel-with-live-owner' 'cancel-live'
    if ($Graceful.process.HasExited -or $Foreign.process.HasExited) { throw 'Cancel stopped an external file owner' }
    Invoke-Msi @('/i',$N.msi) 'graceful-owner'
    Assert-OwnerStopped $Graceful $true
    Assert-Installed $N
    $Stubborn = Start-FileOwner $OwnerExe $Addin 'stubborn' 'stubborn'
    Invoke-InstallerUi $N 'ui-force-owner' 'force-repair'
    Assert-OwnerStopped $Stubborn $false
    Assert-Installed $N
    if ($Foreign.process.HasExited) { throw 'UI force stopped unrelated owner' }
    $Stubborn = Start-FileOwner $OwnerExe $Addin 'cli-stubborn' 'stubborn'
    Invoke-Msi @('/i',$N.msi,'MSIRMSHUTDOWN=1') 'force-owner'
    Assert-OwnerStopped $Stubborn $false
    if ($Foreign.process.HasExited) { throw 'Force stopped unrelated same-name process' }
    Invoke-Msi @('/i',$N.msi) 'force-not-persistent'
    if ((Get-Content -Raw -LiteralPath (Join-Path $Work 'force-not-persistent.log')) -notmatch '(?m)Property\(S\): MSIRMSHUTDOWN = 0\r?$') { throw 'Force consent persisted' }
    Invoke-Msi @('/i',$N.msi,'MSIRMSHUTDOWN=2') 'reject-invalid-shutdown-choice' 1603
    Assert-Installed $N
    Invoke-Msi @('/i',$N.msi,'CREATEDESKTOPSHORTCUT=0','ADDAUTOSTART=0') 'cli-explicit-opt-out'
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'CLI opt-out did not remove options' }
    Invoke-Msi @('/i',$N.msi,'CREATEDESKTOPSHORTCUT=1','ADDAUTOSTART=1') 're-enable-for-ui'
    Invoke-InstallerUi $N 'ui-opt-out' 'repair' '1' -ApplyOptOut
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Repair did not remove opted-out options' }
    Invoke-Msi @('/i',$N.msi) 'repeat-keeps-opt-out'
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Repeat resurrected opted-out options' }
    Invoke-InstallerUi $N 'ui-remove' 'remove' '0'
    if (@(Installed-Products).Count -or (Test-Path -LiteralPath $Addin) -or (Test-Path -LiteralPath 'HKLM:\Software\Burservis\iCemDataMonitor\Installer')) { throw 'Uninstall left owned registration/AddIn/state' }
    if ((Get-Content -Raw -LiteralPath $Marker) -cne $MarkerText -or @(Get-ChildItem -LiteralPath $Install -Recurse -File).Count -ne 1) { throw 'Uninstall changed unowned data or left payload' }
    if ($Foreign.process.HasExited) { throw 'Uninstall stopped unrelated owner' }
    Invoke-Msi @('/i',$N.msi) 'reinstall-after-ui-remove' -InitialPaths
    Assert-Installed $N
    $UninstallOwner = Start-FileOwner $OwnerExe $Addin 'uninstall-owner' 'stubborn'
    Invoke-Msi @('/x',$N.code,'MSIRMSHUTDOWN=1') 'force-uninstall-owner'
    Assert-OwnerStopped $UninstallOwner $false
    if ($Foreign.process.HasExited -or (Get-Content -Raw -LiteralPath $Marker) -cne $MarkerText) { throw 'Forced uninstall escaped file ownership' }
    # The incoming family is blocked; existing legacy maintenance must stay available.
    Invoke-Msi @('/i',$N.msi) 'separation-base' -InitialPaths
    $Other = New-ForeignFamilyFixture
    Invoke-Msi @('/i',$Other.msi) 'foreign-family-install'
    Invoke-Msi @('/i',$N.msi) 'existing-maintenance-with-other-family'
    Invoke-Msi @('/x',$N.code) 'existing-remove-with-other-family'
    Invoke-Msi @('/i',$N.msi) 'block-incoming-cross-family' 1603 -InitialPaths
    if (@(Installed-Products).Count -ne 1 -or @(Installed-Products)[0].PSChildName -ne $Other.code) { throw 'Cross-family gate changed the installed family' }
    Invoke-Msi @('/x',$Other.code) 'foreign-family-remove'
    Write-Host 'PASS legacy MSI: fresh cancel/install, complete repeat/repair, same-version rebuild, transactional upgrade rollback, allowed downgrade, options/paths, graceful/explicit force isolation, maintenance UI Back/Cancel/Remove, unowned data preservation.'
} finally {
    foreach ($Owner in $FileOwners) { try { if (-not $Owner.process.HasExited) { $Owner.process.Kill(); $null = $Owner.process.WaitForExit(5000) } } finally { $Owner.process.Dispose() } }
    foreach ($Code in $ProductCodes) {
        if (@(Installed-Products | Where-Object { $_.PSChildName -eq $Code }).Count) { Invoke-Msi @('/x',$Code) ('cleanup-' + $Code.Trim('{}')) }
    }
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $Logs = Join-Path $Root 'artifacts'; [IO.Directory]::CreateDirectory($Logs) | Out-Null
        Get-ChildItem -LiteralPath $Work -Filter '*.log' | Copy-Item -Destination $Logs
    }
    if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
}
