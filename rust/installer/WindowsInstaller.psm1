#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsPayload.psm1')

function Assert-WindowsMsiVersion([string] $Version) {
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'MSI version must be canonical x.y.z without a suffix.' }
    $Parts = $Version.Split('.')
    if ([long]$Parts[0] -gt 255 -or [long]$Parts[1] -gt 255 -or [long]$Parts[2] -gt 65535) { throw 'MSI version exceeds 255.255.65535.' }
}
function Get-NextWindowsMsiVersion([string] $Version) {
    Assert-WindowsMsiVersion $Version
    $Parsed = [version]$Version
    if ($Parsed.Build -eq 65535) { throw 'Patch version exhausted; choose an explicit higher minor/major version.' }
    return "$($Parsed.Major).$($Parsed.Minor).$($Parsed.Build + 1)"
}
function Select-WindowsReleaseVersion([string] $Requested = 'next', [string[]] $Tags = @()) {
    $Versions = @($Tags | Where-Object { $_ -like 'refs/tags/rust-v*' -or $_ -like 'rust-v*' } | ForEach-Object {
        $Value = $_ -replace '^(refs/tags/)?rust-v', ''
        Assert-WindowsMsiVersion $Value
        [version]$Value
    } | Sort-Object -Unique)
    $Last = if ($Versions.Count) { $Versions[-1].ToString() } else { $null }
    if ($Requested -eq 'next') {
        return $(if ($Last) { Get-NextWindowsMsiVersion $Last } else { '0.1.0' })
    }
    Assert-WindowsMsiVersion $Requested
    if ($Last -and [version]$Requested -le [version]$Last) { throw "Version must exceed the last Rust tag $Last; existing releases are never overwritten." }
    return $Requested
}
function Read-WindowsInstallerPolicy {
    $Policy = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'windows-installer.json') | ConvertFrom-Json
    if ($Policy.schema_version -ne 1 -or [guid]$Policy.upgrade_code -eq [guid]::Empty -or
        [guid]$Policy.upgrade_code -eq [guid]'34A0FD7C-EBD5-4E28-BA96-AE6575896F32' -or
        $Policy.product_name -notlike '*Rust Development') { throw 'Invalid/separation-violating Rust MSI policy' }
    return $Policy
}
function Write-WindowsInstallerSource([string] $PayloadDirectory, [string] $Version, [string] $OutputFile,
    [string] $RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)) {
    Assert-WindowsMsiVersion $Version
    $Manifest = Test-WindowsPayload $PayloadDirectory
    if ($Manifest.artifact_version -cne $Version) { throw 'MSI version must match the verified payload version.' }
    $Policy = Read-WindowsInstallerPolicy
    $Xml = [xml](Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot 'installer/Product.wxs'))
    $Ns = [Xml.XmlNamespaceManager]::new($Xml.NameTable)
    $Ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
    $Package = $Xml.SelectSingleNode('/w:Wix/w:Package', $Ns)
    $Package.SetAttribute('Name', $Policy.product_name)
    $Package.SetAttribute('Version', $Version)
    $Package.SetAttribute('UpgradeCode', $Policy.upgrade_code)
    $Package.SelectSingleNode('w:SummaryInformation', $Ns).SetAttribute('Description', 'Rust development candidate with the original net48 AddIn; production cutover is not approved.')
    $Upgrade = $Package.SelectSingleNode('w:MajorUpgrade', $Ns)
    $Upgrade.RemoveAttribute('AllowDowngrades')
    $Upgrade.SetAttribute('Schedule', 'afterInstallInitialize')
    $Upgrade.SetAttribute('DowngradeErrorMessage', 'A newer Rust candidate is already installed.')
    # Detect, never remove a legacy product. Production cutover is a separate gate.
    $Legacy = $Xml.CreateElement('Upgrade', $Ns.LookupNamespace('w'))
    $Legacy.SetAttribute('Id', '34A0FD7C-EBD5-4E28-BA96-AE6575896F32')
    $VersionRow = $Xml.CreateElement('UpgradeVersion', $Ns.LookupNamespace('w'))
    foreach ($Pair in @{ Minimum='0.0.0'; IncludeMinimum='yes'; OnlyDetect='yes'; Property='LEGACY_MONITOR_PRESENT' }.GetEnumerator()) { $VersionRow.SetAttribute($Pair.Key, $Pair.Value) }
    $null = $Legacy.AppendChild($VersionRow); $null = $Package.AppendChild($Legacy)
    $Launch = $Xml.CreateElement('Launch', $Ns.LookupNamespace('w'))
    $Launch.SetAttribute('Condition', 'NOT LEGACY_MONITOR_PRESENT')
    $Launch.SetAttribute('Message', 'Legacy .NET MSI is installed. Automatic production cutover to the Rust candidate is not supported.')
    $null = $Package.AppendChild($Launch)
    # CLI ICEMDIR must remain authoritative on disposable test VMs/custom installs.
    # The separate ICEMDIR_FOUND search still discovers the canonical iCem folder.
    foreach ($Search in @($Xml.SelectNodes('//w:Property[@Id="ICEMDIR"]/w:DirectorySearch', $Ns))) { $null = $Search.ParentNode.RemoveChild($Search) }
    $WipePath = $Xml.SelectSingleNode('//w:SetProperty[@Id="WipeMonitorRoot"]', $Ns)
    $WipePath.SetAttribute('Value', '[MONITORROOT]')
    $Desktop = $Xml.SelectSingleNode('//w:Component[@Id="Cmp.DesktopShortcut"]', $Ns)
    if (-not $Desktop.SelectSingleNode('w:CreateFolder', $Ns)) {
        $null = $Desktop.AppendChild($Xml.CreateElement('CreateFolder', $Ns.LookupNamespace('w')))
    }
    $RoleMap = @{
        'iCemDataMonitor.Host.exe' = 'host'
        'iCemDataMonitor.WinUI.exe' = 'shell'
        'iCemEmulator.exe' = 'emulator'
    }
    foreach ($Role in $RoleMap.GetEnumerator()) {
        $Binary = @($Manifest.binaries | Where-Object role -CEQ $Role.Value)
        if ($Binary.Count -ne 1) { throw "Missing/duplicate runtime role $($Role.Value)" }
        foreach ($Attribute in @($Xml.SelectNodes('//@*'))) {
            if ($Attribute.Value.Contains($Role.Key)) { $Attribute.Value = $Attribute.Value.Replace($Role.Key, $Binary[0].destination) }
        }
    }
    # Distinct ownership from the corresponding legacy option components.
    foreach ($Component in $Xml.SelectNodes('//w:Component[@Guid]', $Ns)) {
        $Hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$($Policy.upgrade_code):$($Component.Id)"))
        $Component.SetAttribute('Guid', [guid]::new([byte[]]$Hash[0..15]).ToString('B'))
    }
    $Xml.Save($OutputFile)
    return @($Manifest.files).Count + 1
}
function Write-WindowsInstallerPayload([string] $PayloadDirectory, [string] $RepositoryRoot, [string] $WorkDirectory) {
    if (-not $IsWindows) { throw 'The unchanged legacy payload harvester requires Windows paths.' }
    $Manifest = Test-WindowsPayload $PayloadDirectory
    $Stage = Join-Path $WorkDirectory 'stage'
    foreach ($Relative in (@($Manifest.files.path) + @('release-manifest.json'))) {
        $Target = if ($Relative -ceq 'addin/Burservis.iCem.DataMonitor.dll') { Join-Path $Stage $Relative }
                  else { Join-Path $Stage "runtime/$Relative" }
        [IO.Directory]::CreateDirectory((Split-Path $Target -Parent)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PayloadDirectory $Relative) -Destination $Target
    }
    $Wxs = Join-Path $WorkDirectory 'payload.wxs'
    & (Join-Path $RepositoryRoot 'installer/gen-payload.ps1') -StageRoot $Stage -OutputPath $Wxs -RootFolders @('runtime') -EmulatorFolder '' | Out-Host
    $Xml = [xml](Get-Content -Raw -LiteralPath $Wxs)
    $Ns = [Xml.XmlNamespaceManager]::new($Xml.NameTable); $Ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
    $Files = @($Xml.SelectNodes('//w:File', $Ns))
    $Sources = @($Files | ForEach-Object { (Resolve-Path -LiteralPath $_.Source).Path } | Sort-Object -Unique)
    $Expected = @(Get-ChildItem -LiteralPath $Stage -Recurse -File)
    if ($Files.Count -ne $Manifest.files.Count + 1 -or $Sources.Count -ne $Expected.Count -or
        $Xml.SelectNodes('//w:ComponentGroup[@Id="Payload.Addin"]/w:Component/w:File', $Ns).Count -ne 1) { throw 'Legacy harvester omitted/duplicated payload files or AddIn mount' }
    foreach ($File in $Expected) { if ($Sources -cnotcontains $File.FullName) { throw "Unharvested file: $($File.FullName)" } }
    return $Wxs
}
function New-WindowsInstallerAssets([string] $RepositoryRoot, [string] $WorkDirectory, $Manifest) {
    $Legacy = Join-Path $RepositoryRoot 'installer'
    $Adapter = Join-Path $WorkDirectory 'legacy-ui'
    $Assets = Join-Path $Adapter 'assets'
    [IO.Directory]::CreateDirectory($Assets) | Out-Null
    foreach ($Name in @('Burservis_logo_cyr_red.png', 'app-icon.ico', 'CheckConflicts.js', 'CheckConflicts.harness.js')) {
        Copy-Item -LiteralPath (Join-Path $Legacy "assets/$Name") -Destination (Join-Path $Assets $Name)
    }
    Copy-Item -LiteralPath (Join-Path $Legacy 'gen-ui-bmps.ps1') -Destination $Adapter
    # Rewrite only image-name adapter data, including the original harness cases.
    $Names = @{ 'icemdatamonitor.host.exe'='host'; 'icemdatamonitor.winui.exe'='shell'; 'icememulator.exe'='emulator' }
    foreach ($File in @('CheckConflicts.js', 'CheckConflicts.harness.js')) {
        $Path = Join-Path $Assets $File
        $Text = Get-Content -Raw -LiteralPath $Path
        foreach ($Name in $Names.GetEnumerator()) {
            $Binary = @($Manifest.binaries | Where-Object role -CEQ $Name.Value)
            if ($Binary.Count -ne 1) { throw 'Incomplete conflict process mapping' }
            $Text = [regex]::Replace($Text, [regex]::Escape($Name.Key), $Binary[0].destination, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
        [IO.File]::WriteAllText($Path, $Text)
    }
    return $Adapter
}
Export-ModuleMember -Function Assert-WindowsMsiVersion, Get-NextWindowsMsiVersion, Select-WindowsReleaseVersion, Read-WindowsInstallerPolicy, Write-WindowsInstallerSource, Write-WindowsInstallerPayload, New-WindowsInstallerAssets
