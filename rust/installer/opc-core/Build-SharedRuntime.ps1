[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CoreBuildDirectory,
    [Parameter(Mandatory)][string]$OutputFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../tools/WindowsMsiDatabase.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'OpcGuidInventory.psm1') -Force
$root = (Resolve-Path -LiteralPath $CoreBuildDirectory).Path
$output = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $output) { throw 'Output file already exists.' }
if ([IO.Path]::GetExtension($output) -ine '.msi') { throw 'Output must be an MSI file.' }
if ((& dotnet --version) -cne '8.0.423' -or $LASTEXITCODE -ne 0) { throw 'SDK 8.0.423 required.' }
$wix = (& wix --version)
if ($LASTEXITCODE -ne 0 -or $wix -notmatch '^5\.0\.2(?:\+.*)?$') { throw 'WiX 5.0.2 required.' }
$metadata = Get-Content -Raw -LiteralPath (Join-Path $root 'build-metadata.json') | ConvertFrom-Json
if ($metadata.commit -cne 'efe0d1d1ea86a8a727bf26a501a261765e836766' -or
    $metadata.tree -cne '39ae0c9f060550218f377d8aef9b9c96a20c44da' -or
    $metadata.version -cne '3.1.4.132' -or $metadata.configuration -cne 'Release') { throw 'Untrusted source metadata.' }
$patchFile = Join-Path $root 'Apply-OpcEnumSecurity.ps1'
$categoryPatch = Join-Path $root 'Apply-OpcCategorySecurity.ps1'
$archiveFile = Join-Path $root 'source-upstream.tar'
$expectedPatch = '4bdfd807a64bab694120c3e047086b36decaca6de8702c56b5515482c2711f8e'
$expectedPatchedSource = '2ba163374d6d708e9d2a2b7b8b52dcda74ecc959a40b92fe69b9de1442eb26af'
$expectedCategoryPatch = 'cca5106e63285577d757f5202d3d652e19e12c9d6d0d4ddda7a3867ed6fc046c'
$expectedCategorySource = '8713e8f45b0e3946a7a0079b657aa48e6a5f3d2aab1687e89f6ee3000ef0430a'
$expectedComModule = 'b968a2cf6d5259538a4ddbf2120219ec3842b99da57b3789155c6e75363bebe3'
if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Apply-OpcEnumSecurity.ps1') -Algorithm SHA256).Hash -ine $expectedPatch -or
    $metadata.security_patch_sha256 -ine $expectedPatch -or $metadata.opcenum_patched_sha256 -ine $expectedPatchedSource -or
    (Get-FileHash -LiteralPath $patchFile -Algorithm SHA256).Hash -ine $expectedPatch -or
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Apply-OpcCategorySecurity.ps1') -Algorithm SHA256).Hash -ine $expectedCategoryPatch -or
    $metadata.category_patch_sha256 -ine $expectedCategoryPatch -or $metadata.category_patched_sha256 -ine $expectedCategorySource -or
    $metadata.category_com_module_sha256 -ine $expectedComModule -or
    (Get-FileHash -LiteralPath (Join-Path $root 'source/Source/Shared/OpcUtilityClasses/COpcComModule.cpp') -Algorithm SHA256).Hash -ine $expectedComModule -or
    (Get-FileHash -LiteralPath $categoryPatch -Algorithm SHA256).Hash -ine $expectedCategoryPatch -or
    (Get-FileHash -LiteralPath $archiveFile -Algorithm SHA256).Hash -ine $metadata.source_archive_sha256 -or
    (Get-FileHash -LiteralPath (Join-Path $root 'source/Source/Common/ServerEnumerator/OpcEnum.cpp') -Algorithm SHA256).Hash -ine $metadata.opcenum_patched_sha256) {
    throw 'Missing or mismatched reviewed OpcEnum security provenance.'
}
$names = @('opc-com-proxystub-mergemodule-3.1.4.132-x86.msm', 'opc-com-proxystub-mergemodule-3.1.4.132-x64.msm', 'LICENSE.md')
if (@($metadata.files).Count -ne $names.Count) { throw 'Unexpected build inventory.' }
foreach ($name in $names) {
    $matches = @($metadata.files | Where-Object { $_.name -ceq $name })
    if ($matches.Count -ne 1) { throw "Missing/duplicate input: $name" }
    $file = Get-Item -LiteralPath (Join-Path $root $name)
    if ($file.Length -ne $matches[0].size -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $matches[0].sha256) { throw "Input hash mismatch: $name" }
}
if ((Get-FileHash -LiteralPath (Join-Path $root 'LICENSE.md') -Algorithm SHA256).Hash -ine '4f9f1935014049ea941a6d93fbd0248d1aa905c44ed2520ea2815d48c3a51291') { throw 'License does not match pinned upstream bytes.' }
$nativeNames = @('New-OpcRuntimeModule.ps1', 'native-runtime-inventory.json', 'opc-component-codes.txt', 'OpcGuidInventory.psm1')
if (@($metadata.native_inputs).Count -ne $nativeNames.Count) { throw 'Incomplete native ownership provenance.' }
foreach ($name in $nativeNames) {
    $record = @($metadata.native_inputs | Where-Object name -CEQ $name)
    if ($record.Count -ne 1 -or $record[0].sha256 -ine (Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash -or
        $record[0].sha256 -ine (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $name) -Algorithm SHA256).Hash) { throw "Native ownership provenance mismatch: $name" }
}
$nativeData = Get-Content -LiteralPath (Join-Path $root 'native-runtime-inventory.json') -Raw | ConvertFrom-Json -AsHashtable
# The fixed embedded ownership inventory must cover every component from both
# validated merge modules, not just the known OpcEnum/opcproxy examples.
$componentCodes = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'opc-component-codes.txt'))
$moduleCodes = @()
$componentInstaller = New-Object -ComObject WindowsInstaller.Installer
try {
    foreach ($moduleIndex in 0..1) {
        $moduleName = $names[$moduleIndex]
        $architecture = if ($moduleIndex -eq 0) { 'x86' } else { 'x64' }
        $moduleDatabase = $componentInstaller.OpenDatabase((Join-Path $root $moduleName), 0)
        try {
            $moduleCodes += @(Read-WindowsMsiRecords $moduleDatabase 'SELECT `ComponentId` FROM `Component`' @('ComponentId') | ForEach-Object { $_.ComponentId })
            $tables = @(Read-WindowsMsiRecords $moduleDatabase 'SELECT `Name` FROM `_Tables`' @('Name') | ForEach-Object { $_.Name })
            foreach ($forbidden in @('SelfReg', 'CustomAction', 'RemoveRegistry')) {
                if ($forbidden -cin $tables -and @(Read-WindowsMsiRecords $moduleDatabase "SELECT * FROM ``$forbidden``" @('First')).Count) { throw "Nontransactional/unbounded module table: $forbidden" }
            }
            $registry = @(Read-WindowsMsiRecords $moduleDatabase 'SELECT `Root`, `Key`, `Name` FROM `Registry`' @('Root', 'Key', 'Name'))
            $expectedCount = 0
            foreach ($component in @($nativeData.components | Where-Object arch -CEQ $architecture)) {
                foreach ($row in $component.registry) { $expectedCount += $row.values.Count }
            }
            if ($registry.Count -ne $expectedCount -or @($registry | Where-Object { $_.Root -cne '2' -or $_.Name -cin @('-', '*') -or -not $_.Key.StartsWith('SOFTWARE\Classes\', [StringComparison]::Ordinal) }).Count) {
                throw 'Native module registry coverage/scope mismatch.'
            }
            $serviceCount = if ($architecture -ceq 'x86') { 1 } else { 0 }
            foreach ($table in @('ServiceInstall', 'ServiceControl')) {
                $actualCount = if ($table -cin $tables) { @(Read-WindowsMsiRecords $moduleDatabase "SELECT ``Name`` FROM ``$table``" @('Name')).Count } else { 0 }
                if ($actualCount -ne $serviceCount) { throw 'Native service ownership coverage mismatch.' }
            }
        } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($moduleDatabase) }
    }
} finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($componentInstaller) }
if ($componentCodes.Count -ne 18 -or @($componentCodes | Sort-Object -Unique).Count -ne 18 -or
    (($componentCodes | Sort-Object) -join '|') -cne (($moduleCodes | Sort-Object) -join '|')) {
    throw 'MSI component ownership inventory differs from the canonical native merge modules.'
}
$parent = Split-Path -Parent $output
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Output parent must already exist.' }
$categoryIdl = Join-Path $root 'source/Source/Common/CategoryManager/CategoryManager.idl'
if ((Get-FileHash -LiteralPath $categoryIdl -Algorithm SHA256).Hash -ine '724b429223ed5f81f2acf011f389cb776b7fa41d01c75e9562636918a397c73d') { throw 'Category Manager IDL differs from pinned source.' }
$categoryCpp = Join-Path $root 'source/Source/Common/CategoryManager/CategoryManager.cpp'
if ((Get-FileHash -LiteralPath $categoryCpp -Algorithm SHA256).Hash -ine $expectedCategorySource) { throw 'Category Manager source differs from reviewed patch.' }
$enumRgs = Join-Path $root 'source/Source/Common/ServerEnumerator/OpcEnum.rgs'
if ((Get-FileHash -LiteralPath $enumRgs -Algorithm SHA256).Hash -ine '2277f802f65c6295720e5f40b72058ac43f1824558a241bf4f4cfc5b397f97b3') { throw 'OpcEnum registration source differs from pinned source.' }
$serverListRgs = Join-Path $root 'source/Source/Common/ServerEnumerator/OpcServerList.rgs'
if ((Get-FileHash -LiteralPath $serverListRgs -Algorithm SHA256).Hash -ine '2d67263159f6c60d67ab74b803eea42cb6b516cf276cfc80dad3ad29cffce823') { throw 'OpcServerList registration source differs from pinned source.' }
$keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in (Read-OpcGuidInventory (Join-Path $root 'stage-x86/include'))) {
    $guid = $entry.guid
    switch -Regex -CaseSensitive ($entry.name) {
        '^IID_' { [void]$keys.Add("SOFTWARE\Classes\Interface\$guid"); [void]$keys.Add("SOFTWARE\Classes\CLSID\$guid") }
        '^LIBID_' { [void]$keys.Add("SOFTWARE\Classes\TypeLib\$guid") }
        '^CLSID_' { [void]$keys.Add("SOFTWARE\Classes\CLSID\$guid"); [void]$keys.Add("SOFTWARE\Classes\AppID\$guid") }
    }
}
# Pinned CategoryManager.idl defines these two UUIDs; its MIDL file is not part of x86 stage.
[void]$keys.Add('SOFTWARE\Classes\TypeLib\{E4DCB3DA-343C-4037-A126-9A02A284D6CE}')
[void]$keys.Add('SOFTWARE\Classes\CLSID\{E4DCB3DB-343C-4037-A126-9A02A284D6CE}')
[void]$keys.Add('SOFTWARE\Classes\AppID\{E4DCB3DB-343C-4037-A126-9A02A284D6CE}')
[void]$keys.Add('SOFTWARE\Classes\AppID\{CC7D4C09-B26B-45B3-B482-2DECA09B832A}')
[void]$keys.Add('SOFTWARE\Classes\AppID\{13486D44-4821-11D2-A494-3CB306C10000}')
[void]$keys.Add('SYSTEM\CurrentControlSet\Services\OpcEnum')
if ($keys.Count -lt 20) { throw 'Incomplete OPC registration inventory.' }
$include = "$output.guard.wxi"
if (Test-Path -LiteralPath $include) { throw 'Guard inventory output already exists.' }
$xml = [Collections.Generic.List[string]]::new()
$xml.Add('<Include xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$index = 0
foreach ($key in ($keys | Sort-Object)) {
    foreach ($bitness in @('always32', 'always64')) {
        $index++
        $id = 'ICEMGUARD{0:D4}' -f $index
        $nameAttribute = if ($key -eq 'SYSTEM\CurrentControlSet\Services\OpcEnum') { ' Name="ImagePath"' } else { '' }
        $xml.Add("<Property Id=`"$id`"><RegistrySearch Id=`"${id}Search`" Root=`"HKLM`" Key=`"$key`"$nameAttribute Type=`"raw`" Bitness=`"$bitness`" /></Property>")
        $xml.Add("<Launch Condition=`"Installed OR NOT $id`" Message=`"An existing OPC registration was detected; preserve it and use compatibility verification.`" />")
    }
}
$xml.Add('</Include>')
[IO.File]::WriteAllLines($include, $xml, [Text.UTF8Encoding]::new($false))
$inventoryFile = "$output.inventory.txt"
if (Test-Path -LiteralPath $inventoryFile) { throw 'Guard input already exists.' }
$guardKeys = [Collections.Generic.List[string]]::new()
foreach ($key in ($keys | Sort-Object)) { $guardKeys.Add($key) }
# These named keys are authored by the pinned RGS sources. They are not placed
# in RegLocator because the bootstrapper accepts only GUID keys and services.
$guardKeys.Add('SOFTWARE\Classes\OPC.ServerList')
$guardKeys.Add('SOFTWARE\Classes\OPC.ServerList.1')
$guardKeys.Add('SOFTWARE\Classes\AppID\OpcEnum.EXE')
$guardKeys.Add('SOFTWARE\Classes\AppID\OpcCategoryManager.exe')
$guardKeys.Add('SOFTWARE\Classes\OPC.OpcCategoryManager')
$guardKeys.Add('SOFTWARE\Classes\OPC.OpcCategoryManager.1')
foreach ($component in $nativeData.components) {
    foreach ($row in $component.registry) {
        if (-not @($guardKeys | Where-Object { $row.key.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or $row.key.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) }).Count) {
            throw "Native registration is outside the conflict inventory: $($row.key)"
        }
    }
}
[IO.File]::WriteAllLines($inventoryFile, $guardKeys, [Text.UTF8Encoding]::new($false))
$guardBuild = "$output.guard-build"
if (Test-Path -LiteralPath $guardBuild) { throw 'Guard build directory already exists.' }
$guardBin = Join-Path $guardBuild 'bin'
$guardObj = Join-Path $guardBuild 'obj'
& dotnet build (Join-Path $PSScriptRoot 'RegistryGuard.csproj') -c Release `
    "-p:GuardInventoryFile=$inventoryFile" "-p:BaseIntermediateOutputPath=$guardObj\" "-p:OutputPath=$guardBin\" -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Registry guard build failed: $LASTEXITCODE" }
$guardExe = Join-Path $guardBin 'icem-opc-core-registry-guard.exe'
if (-not (Test-Path -LiteralPath $guardExe -PathType Leaf)) { throw 'Registry guard missing.' }
$recipe = Join-Path $PSScriptRoot 'Build-OpcCore.ps1'
& wix build (Join-Path $PSScriptRoot 'SharedRuntime.wxs') -arch x64 `
    -d "MsmX86=$(Join-Path $root $names[0])" -d "MsmX64=$(Join-Path $root $names[1])" `
    -d "OpcLicense=$(Join-Path $root $names[2])" -d "GuardInventory=$include" -d "GuardExe=$guardExe" `
    -d "SourceArchive=$archiveFile" -d "SecurityPatch=$patchFile" -d "CategoryPatch=$categoryPatch" -d "BuildMetadata=$(Join-Path $root 'build-metadata.json')" -d "BuildRecipe=$recipe" `
    -d "NativeRecipe=$(Join-Path $root $nativeNames[0])" -d "NativeInventory=$(Join-Path $root $nativeNames[1])" -d "NativeCodes=$(Join-Path $root $nativeNames[2])" -d "NativeGuidReader=$(Join-Path $root $nativeNames[3])" -o $output
if ($LASTEXITCODE -ne 0) { throw "WiX failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw 'WiX did not produce the MSI.' }
# WiX 5 defaults to an implicit major upgrade even without a MajorUpgrade element.
# Inspect the built database; fail closed if any Upgrade row was introduced.
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($output, 0)
try {
    $tables = @(Read-WindowsMsiRecords $database 'SELECT `Name` FROM `_Tables`' @('Name'))
    if (@($tables | Where-Object Name -CEQ 'Upgrade').Count -and
        @(Read-WindowsMsiRecords $database 'SELECT `UpgradeCode` FROM `Upgrade`' @('UpgradeCode')).Count) { throw 'Unexpected MSI Upgrade rows.' }
} finally {
    foreach ($com in @($database, $installer)) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) }
}
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($output))" | Set-Content -LiteralPath "$output.sha256" -Encoding ascii
Write-Output "Built $output SHA256 $hash; no installation performed."
