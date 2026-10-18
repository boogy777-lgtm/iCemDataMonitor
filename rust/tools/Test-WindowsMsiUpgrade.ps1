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
function Invoke-Msi([string[]] $Arguments, [string] $Label, [int] $ExpectedExit = 0) {
    $Log = Join-Path $Work "$Label.log"
    $Info = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32/msiexec.exe'))
    $Info.UseShellExecute = $false
    # Windows Installer's PROPERTY="value" grammar must keep the property name
    # outside the quotes; do not quote the whole PROPERTY=value token.
    $Tokens = @($Arguments + @("MONITORROOT=$Install", "ICEMDIR=$IcemRoot", '/qn', '/norestart', '/l*v', $Log))
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
    Invoke-Msi @('/i', $Msi, 'CREATEDESKTOPSHORTCUT=1', 'ADDAUTOSTART=1') 'install-N'
    Assert-Installed $Version $FirstCode
    if ((Get-ItemPropertyValue -LiteralPath $RunKey -Name iCemDataMonitorHost) -cne ('"' + (Join-Path $Install 'icem-host-dev.exe') + '" --autostart')) { throw 'Wrong native autostart command' }
    $Wsh = New-Object -ComObject WScript.Shell
    try {
        $Link = $Wsh.CreateShortcut($Shortcut)
        try { if ($Link.TargetPath -ine (Join-Path $Install 'icem-shell-dev.exe')) { throw 'Wrong desktop shortcut target' } }
        finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($Link) }
    } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($Wsh) }
    [IO.File]::WriteAllText($Marker, $MarkerText)
    Invoke-Msi @('/i', $NextMsi.msi, 'CREATEDESKTOPSHORTCUT=1', 'ADDAUTOSTART=1') 'upgrade-N-plus-one'
    Assert-Installed $Next $NextCode
    if ((Get-Content -Raw -LiteralPath $Marker) -cne $MarkerText) { throw 'Upgrade modified user data' }
    Invoke-Msi @('/i', $Msi, 'CREATEDESKTOPSHORTCUT=1', 'ADDAUTOSTART=1') 'blocked-downgrade' 1603
    Assert-Installed $Next $NextCode
    Invoke-Msi @('/x', $NextCode) 'uninstall'
    if (@(Installed-Products).Count -or (Test-Path -LiteralPath $Install) -or (Test-Path -LiteralPath (Join-Path $Modules 'Burservis.iCem.DataMonitor.dll'))) { throw 'Legacy full uninstall left runtime/AddIn/registration' }
    if ((Test-Path -LiteralPath $Shortcut) -or (Get-ItemProperty -LiteralPath $RunKey -Name iCemDataMonitorHost -ErrorAction SilentlyContinue)) { throw 'Uninstall left shortcut/autostart' }
    Write-Host "PASS Windows MSI: install $Version -> upgrade $Next -> blocked downgrade -> uninstall; hashes, one registration and upgrade preserves unowned data; full uninstall follows legacy runtime cleanup."
} finally {
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
