[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CoreBuildDirectory,
    [Parameter(Mandatory)][string]$OutputFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../tools/WindowsMsiDatabase.psm1') -Force
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
$parent = Split-Path -Parent $output
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Output parent must already exist.' }
$iidHashes = @{
    'opc_ae_i.c'='ec56d7a664df24f57af838459690f9ee7fe1b262920f5a398d452fb043f16bec'
    'opcbc_i.c'='baeeb79cfe8f1e67bcf14038fa27330aff23f7ff304f5cc3c4eeea56f35fd5ab'
    'OpcCmd_i.c'='35839294b37555d71975d9645216e4a7ef0e125ecb1cd686d047f9422b8ab524'
    'opccomn_i.c'='859fd4476a9809f3ae410706b614c76b754c658d9486a24bc34dcc9a1d00c9de'
    'opcda_i.c'='aa9506c2a2cf162eacb4fd6d2f71d3f3c37256fcf4133935fd962fb366448d20'
    'OpcDx_i.c'='61203e6f010746b33e4171f4cd6ff3f33e12192c8dbdd51c8f73d5c967fd5634'
    'OpcEnum_i.c'='f646cbb91344e73182b87eff68e2aa8e2fe2d0eb2a416d0e564cf55f434c5386'
    'opchda_i.c'='6b41682d3b383b4e620a0b3feba6e006937941b93c3eddb326a83ffb64ea08e4'
    'opcSec_i.c'='b5735b093bb9b695c7ec105dff3c25648ba463d12a827ad83c71c0d4741a9ce7'
}
$categoryIdl = Join-Path $root 'source/Source/Common/CategoryManager/CategoryManager.idl'
if ((Get-FileHash -LiteralPath $categoryIdl -Algorithm SHA256).Hash -ine '724b429223ed5f81f2acf011f389cb776b7fa41d01c75e9562636918a397c73d') { throw 'Category Manager IDL differs from pinned source.' }
$categoryCpp = Join-Path $root 'source/Source/Common/CategoryManager/CategoryManager.cpp'
if ((Get-FileHash -LiteralPath $categoryCpp -Algorithm SHA256).Hash -ine $expectedCategorySource) { throw 'Category Manager source differs from reviewed patch.' }
$enumRgs = Join-Path $root 'source/Source/Common/ServerEnumerator/OpcEnum.rgs'
if ((Get-FileHash -LiteralPath $enumRgs -Algorithm SHA256).Hash -ine '2277f802f65c6295720e5f40b72058ac43f1824558a241bf4f4cfc5b397f97b3') { throw 'OpcEnum registration source differs from pinned source.' }
$serverListRgs = Join-Path $root 'source/Source/Common/ServerEnumerator/OpcServerList.rgs'
if ((Get-FileHash -LiteralPath $serverListRgs -Algorithm SHA256).Hash -ine '2d67263159f6c60d67ab74b803eea42cb6b516cf276cfc80dad3ad29cffce823') { throw 'OpcServerList registration source differs from pinned source.' }
$keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$pattern = 'MIDL_DEFINE_GUID\(\s*(?:IID|CLSID)\s*,\s*(IID_|LIBID_|CLSID_)\w+\s*,\s*((?:0x[0-9a-fA-F]+\s*,\s*){10}0x[0-9a-fA-F]+)\s*\)'
foreach ($fileName in ($iidHashes.Keys | Sort-Object)) {
    $iidFile = Join-Path $root "stage-x86/include/$fileName"
    $sourceText = Get-Content -LiteralPath $iidFile -Raw
    $pathPattern = '(?m)^/\* Compiler settings for [^\r\n]+:'
    if ([regex]::Matches($sourceText, $pathPattern).Count -ne 1) { throw "Unexpected MIDL source heading: $fileName" }
    $canonical = [regex]::Replace($sourceText, $pathPattern, '/* Compiler settings for <source>:')
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $canonicalHash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($canonicalHash -ine $iidHashes[$fileName]) { throw "Unreviewed MIDL inventory: $fileName" }
    $entries = [regex]::Matches($sourceText, $pattern)
    $declarations = [regex]::Matches($sourceText, 'MIDL_DEFINE_GUID\(\s*(?:IID|CLSID)\s*,')
    if ($entries.Count -eq 0 -or $entries.Count -ne $declarations.Count) { throw "Incomplete MIDL inventory: $fileName" }
    foreach ($entry in $entries) {
        $parts = @($entry.Groups[2].Value -split ',' | ForEach-Object { [Convert]::ToUInt32($_.Trim().Substring(2), 16) })
        $guid = ('{{{0:X8}-{1:X4}-{2:X4}-{3:X2}{4:X2}-{5:X2}{6:X2}{7:X2}{8:X2}{9:X2}{10:X2}}}' -f $parts)
        switch ($entry.Groups[1].Value) {
            'IID_' { [void]$keys.Add("SOFTWARE\Classes\Interface\$guid"); [void]$keys.Add("SOFTWARE\Classes\CLSID\$guid") }
            'LIBID_' { [void]$keys.Add("SOFTWARE\Classes\TypeLib\$guid") }
            'CLSID_' { [void]$keys.Add("SOFTWARE\Classes\CLSID\$guid"); [void]$keys.Add("SOFTWARE\Classes\AppID\$guid") }
        }
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
    -d "SourceArchive=$archiveFile" -d "SecurityPatch=$patchFile" -d "CategoryPatch=$categoryPatch" -d "BuildMetadata=$(Join-Path $root 'build-metadata.json')" -d "BuildRecipe=$recipe" -o $output
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
