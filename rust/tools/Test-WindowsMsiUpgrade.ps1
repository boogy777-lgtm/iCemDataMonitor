#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Msi, [switch] $AllowMachineChanges,
    [string] $RepositoryRoot, [string] $BinaryDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or ($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowMachineChanges)) {
    throw 'Run on a disposable Windows CI runner, or explicitly pass -AllowMachineChanges on a disposable Windows VM.'
}
$Root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $Root 'installer/WindowsPayload.psm1') -Force
Import-Module (Join-Path $Root 'installer/WindowsInstaller.psm1') -Force
if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path $Root -Parent }
if (-not $BinaryDirectory) { $BinaryDirectory = Join-Path $Root 'target/x86_64-pc-windows-msvc/release' }
$Policy = Read-WindowsInstallerPolicy
$Msi = (Resolve-Path -LiteralPath $Msi).Path
if ((Get-Content -Raw -LiteralPath "$Msi.sha256").Split(' ')[0] -ine (Get-FileHash -LiteralPath $Msi).Hash) { throw 'MSI checksum mismatch' }
$Work = Join-Path ([IO.Path]::GetTempPath()) ('icem-msi-upgrade-' + [guid]::NewGuid().ToString('N'))
$Install = Join-Path $Work "installed $([char]0x0442) files"
$IcemRoot = Join-Path $Work 'iCem target'
$Modules = Join-Path $IcemRoot 'Bin/Modules'
$Marker = Join-Path $Install 'operator-not-msi-owned.ini'
$MarkerText = 'operator-owned configuration must survive'
[IO.Directory]::CreateDirectory($Work) | Out-Null
$ProductCodes = [Collections.Generic.List[string]]::new()
$FileOwners = [Collections.Generic.List[object]]::new()
function Installed-Products {
    @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' | ForEach-Object {
        $Entry = Get-ItemProperty -LiteralPath $_.PSPath
        if ($Entry.PSObject.Properties['DisplayName'] -and $Entry.DisplayName -eq $Policy.product_name) { $Entry }
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
    if ($DidClose -ne $Graceful) { throw 'Unexpected graceful/forced shutdown path' }
}
function Assert-Installed([string] $Version, [string] $ProductCode) {
    $Products = @(Installed-Products)
    if ($Products.Count -ne 1 -or $Products[0].PSChildName -ne $ProductCode -or $Products[0].DisplayVersion -ne $Version) { throw 'MSI did not leave exactly one expected installed version' }
    # Reconstruct the logical archive from the two actual installation mounts.
    # The DLL must exist ONLY in iCem/Bin/Modules; provenance stays with runtime.
    $View = Join-Path $Work ('verify-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($View) | Out-Null
    try {
        foreach ($File in Get-ChildItem -LiteralPath $Install -Recurse -File) {
            if ($File.FullName -ceq $Marker) { continue }
            $Destination = Join-Path $View ([IO.Path]::GetRelativePath($Install, $File.FullName))
            [IO.Directory]::CreateDirectory((Split-Path $Destination -Parent)) | Out-Null
            Copy-Item -LiteralPath $File.FullName -Destination $Destination
        }
        if (@(Get-ChildItem -LiteralPath $Modules -File -Recurse).Count -ne 1) { throw 'AddIn mount contains missing/extra files' }
        Copy-Item -LiteralPath (Join-Path $Modules 'Burservis.iCem.DataMonitor.dll') -Destination (Join-Path $View 'addin/Burservis.iCem.DataMonitor.dll')
        $Manifest = Test-WindowsPayload $View
        if ($Manifest.artifact_version -ne $Version) { throw 'Installed payload version mismatch' }
    } finally { Remove-Item -LiteralPath $View -Recurse -Force }

}
Push-Location $Root
try {
    $Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (-not [Security.Principal.WindowsPrincipal]::new($Identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'MSI tests require an elevated disposable Windows runner' }
    } finally { $Identity.Dispose() }
    if (@(Installed-Products).Count) { throw 'A Rust development MSI is already installed; refusing to touch it.' }
    $RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $Shortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'iCem Data Monitor.lnk'
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Existing monitor shortcut/autostart found; refusing to touch them' }
    $Version = Read-MsiProperty $Msi 'ProductVersion'
    if ($Version -isnot [string]) { throw "MSI ProductVersion helper returned multiple values: <$Version>" }
    Write-Host "MSI ProductVersion: <$Version>"
    $Next = Get-NextWindowsMsiVersion $Version
    $FirstCode = Read-MsiProperty $Msi 'ProductCode'
    $ProductCodes.Add($FirstCode)
    $Archive = [IO.Path]::ChangeExtension($Msi, '.zip')
    if ((Get-Content -Raw -LiteralPath "$Archive.sha256").Split(' ')[0] -ine (Get-FileHash -LiteralPath $Archive).Hash) { throw 'Probe source archive hash mismatch' }
    $Source = Join-Path $Work 'source'
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Source)
    $SourceManifest = Test-WindowsPayload $Source
    $NextArchive = New-WindowsArchive -RepositoryRoot $RepositoryRoot -BinaryDirectory $BinaryDirectory -AddinDirectory (Join-Path $Source 'addin') -OutputDirectory $Work -Version $Next -Commit $SourceManifest.source_commit -Compiler $SourceManifest.compiler
    $NextMsi = & (Join-Path $PSScriptRoot 'Build-WindowsMsi.ps1') -Archive $NextArchive.archive -OutputDirectory $Work
    $NextCode = Read-MsiProperty $NextMsi.msi 'ProductCode'
    if ($NextCode -eq $FirstCode) { throw 'N+1 must have a new ProductCode' }
    $ProductCodes.Add($NextCode)
    $ExpectedUpgrade = [guid]$Policy.upgrade_code
    foreach ($Path in @($Msi, $NextMsi.msi)) {
        if ([guid](Read-MsiProperty $Path 'UpgradeCode') -ne $ExpectedUpgrade) { throw 'UpgradeCode changed between N and N+1' }
    }
    Invoke-Msi @('/i', $Msi) 'install-default-opt-out' -InitialPaths
    Assert-Installed $Version $FirstCode
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Default install enabled opt-in options' }
    Invoke-Msi @('/x', $FirstCode) 'uninstall-default-opt-out'
    if (@(Installed-Products).Count -or @(Get-ChildItem -LiteralPath $Install -Recurse -File -ErrorAction SilentlyContinue).Count) { throw 'Default uninstall left MSI-owned files/registration' }
    Invoke-Msi @('/i', $Msi, 'CREATEDESKTOPSHORTCUT=1', 'ADDAUTOSTART=1') 'install-N' -InitialPaths
    Assert-Installed $Version $FirstCode
    if ((Get-ItemPropertyValue -LiteralPath $RunKey -Name iCemDataMonitorHost) -cne ('"' + (Join-Path $Install 'icem-host-dev.exe') + '" --autostart')) { throw 'Wrong native autostart command' }
    $Shell = New-Object -ComObject Shell.Application
    $Folder = $null; $Item = $null; $Link = $null
    try {
        if (-not (Test-Path -LiteralPath $Shortcut -PathType Leaf)) { throw "Desktop shortcut missing: $Shortcut" }
        # Inspect the actual Shell link without following/launching its target.
        # Preserve raw evidence if an automation adapter loses Unicode.
        [IO.File]::WriteAllText((Join-Path $Work 'desktop-link-bytes.log'), [Convert]::ToBase64String([IO.File]::ReadAllBytes($Shortcut)))
        $Folder = $Shell.Namespace((Split-Path $Shortcut -Parent))
        $Item = $Folder.ParseName((Split-Path $Shortcut -Leaf))
        $Link = $Item.GetLink
        $Target = $Link.Path
        [IO.File]::WriteAllText((Join-Path $Work 'desktop-link-target.log'), $Target)
        if ($Target -ine (Join-Path $Install 'icem-shell-dev.exe') -or -not (Test-Path -LiteralPath $Target -PathType Leaf)) { throw "Wrong desktop shortcut target: <$Target>" }
    } finally {
        foreach ($Object in @($Link, $Item, $Folder, $Shell)) {
            if ($Object) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($Object) }
        }
    }
    [IO.File]::WriteAllText($Marker, $MarkerText)
    # A plain repeated /i must repair every owned file from the MSI, including
    # corrupted existing files, while recovering paths/options with no CLI help.
    Remove-Item -LiteralPath (Join-Path $Install 'icem-shell-dev.exe')
    [IO.File]::WriteAllText((Join-Path $Install 'templates/variable_registry.json'), 'corrupted')
    Remove-Item -LiteralPath (Join-Path $Modules 'Burservis.iCem.DataMonitor.dll')
    Remove-Item -LiteralPath $Shortcut
    Remove-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost
    Invoke-Msi @('/i', $Msi) 'plain-repeat-repairs-complete-payload'
    Assert-Installed $Version $FirstCode
    if (-not (Test-Path -LiteralPath $Shortcut) -or
        (Get-ItemPropertyValue -LiteralPath $RunKey -Name iCemDataMonitorHost) -cne ('"' + (Join-Path $Install 'icem-host-dev.exe') + '" --autostart')) { throw 'Repeat install did not restore options' }
    Invoke-Msi @('/i', $Msi) 'repeat-again-is-idempotent'
    Assert-Installed $Version $FirstCode
    # Real RM integration: same executable image name, distinct resource owners.
    # Compile using the Windows .NET Framework compiler; fixtures never publish.
    $Locks = Join-Path $Work 'lock-fixtures'
    $ForeignDirectory = Join-Path $Locks 'foreign'
    [IO.Directory]::CreateDirectory($ForeignDirectory) | Out-Null
    $OwnerExe = Join-Path $Locks ('icem-lock-' + [guid]::NewGuid().ToString('N') + '.exe')
    $Framework = Join-Path $env:SystemRoot 'Microsoft.NET/Framework64/v4.0.30319'
    & (Join-Path $Framework 'csc.exe') /nologo /target:winexe /platform:x64 ("/out:$OwnerExe") ("/reference:$Framework/System.Windows.Forms.dll") ("/reference:$Framework/System.Drawing.dll") (Join-Path $Root 'installer/tests/LockedFileOwner.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Lock fixture compilation failed' }
    $ForeignExe = Join-Path $ForeignDirectory ([IO.Path]::GetFileName($OwnerExe))
    Copy-Item -LiteralPath $OwnerExe -Destination $ForeignExe
    $ForeignFile = Join-Path $ForeignDirectory 'unrelated.dll'
    [IO.File]::WriteAllText($ForeignFile, 'foreign resource')
    $Foreign = Start-FileOwner $ForeignExe $ForeignFile 'foreign' 'stubborn'
    $AddinPath = Join-Path $Modules 'Burservis.iCem.DataMonitor.dll'
    $Graceful = Start-FileOwner $OwnerExe $AddinPath 'graceful' 'graceful'
    Invoke-Msi @('/i', $Msi) 'repair-graceful-file-owner'
    Assert-OwnerStopped $Graceful $true
    Assert-Installed $Version $FirstCode
    if ($Foreign.process.HasExited) { throw 'Installer stopped an unrelated same-name process' }
    $Stubborn = Start-FileOwner $OwnerExe $AddinPath 'stubborn' 'stubborn'
    $ShutdownClock = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Msi @('/i', $Msi, 'MSIRMSHUTDOWN=1') 'repair-force-unresponsive-owner'
    if ($ShutdownClock.Elapsed.TotalSeconds -gt 90) { throw 'Force shutdown exceeded finite test deadline' }
    Assert-OwnerStopped $Stubborn $false
    Assert-Installed $Version $FirstCode
    if ($Foreign.process.HasExited) { throw 'Force shutdown escaped the affected file set' }
    Invoke-Msi @('/i', $Msi) 'force-consent-is-not-persistent'
    $ConsentLog = Get-Content -Raw -LiteralPath (Join-Path $Work 'force-consent-is-not-persistent.log')
    if ($ConsentLog -notmatch '(?m)Property\(S\): MSIRMSHUTDOWN = 0\r?$') { throw 'Force consent persisted into another operation' }
    Invoke-Msi @('/i', $NextMsi.msi) 'upgrade-N-plus-one'
    Assert-Installed $Next $NextCode
    if ((Get-Content -Raw -LiteralPath $Marker) -cne $MarkerText) { throw 'Upgrade modified user data' }
    Invoke-Msi @('/i', $Msi, 'CREATEDESKTOPSHORTCUT=1', 'ADDAUTOSTART=1') 'blocked-downgrade' 1603
    Assert-Installed $Next $NextCode
    Invoke-Msi @('/i', $NextMsi.msi, 'CREATEDESKTOPSHORTCUT=0', 'ADDAUTOSTART=0') 'explicit-options-off'
    Assert-Installed $Next $NextCode
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Explicit opt-out did not remove options' }
    Invoke-Msi @('/i', $NextMsi.msi) 'repeat-preserves-options-off'
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Repeat resurrected disabled options' }
    Invoke-Msi @('/x', $NextCode) 'uninstall'
    if (@(Installed-Products).Count -or (Test-Path -LiteralPath (Join-Path $Modules 'Burservis.iCem.DataMonitor.dll')) -or (Test-Path -LiteralPath 'HKLM:\Software\Burservis\iCemDataMonitor\RustDevelopment\Installer')) { throw 'Uninstall left AddIn/registration/saved installer state' }
    if ((Get-Content -Raw -LiteralPath $Marker) -cne $MarkerText -or @(Get-ChildItem -LiteralPath $Install -Recurse -File).Count -ne 1) { throw 'Uninstall changed user data or left MSI-owned files' }
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Uninstall left shortcut/autostart' }
    Write-Host "PASS Windows MSI: install $Version -> upgrade $Next -> blocked downgrade -> uninstall; hashes, one registration and upgrade preserves unowned data; RM graceful/explicit force shutdown targets only actual file owners; plain repeat repairs deleted/corrupt files and restores paths/options; explicit option changes persist; uninstall preserves unowned data and removes all owned files/options."
} finally {
    foreach ($Owner in $FileOwners) {
        try {
            if (-not $Owner.process.HasExited) { $Owner.process.Kill(); $null = $Owner.process.WaitForExit(5000) }
        } finally { $Owner.process.Dispose() }
    }
    foreach ($Code in $ProductCodes) {
        if (@(Installed-Products | Where-Object { $_.PSChildName -eq $Code }).Count) { Invoke-Msi @('/x', $Code) ('cleanup-' + $Code.Trim('{}')) }
    }
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $Logs = Join-Path $Root 'artifacts/msi'
        [IO.Directory]::CreateDirectory($Logs) | Out-Null
        Get-ChildItem -LiteralPath $Work -Filter '*.log' | Copy-Item -Destination $Logs
    }
    if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
    Pop-Location
}
