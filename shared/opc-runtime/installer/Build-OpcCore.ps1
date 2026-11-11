[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRepository,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('VS2022v143', 'VS2026v145')][string]$Toolchain = 'VS2022v143'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$commit = 'efe0d1d1ea86a8a727bf26a501a261765e836766'
$tree = '39ae0c9f060550218f377d8aef9b9c96a20c44da'
$version = '3.1.4.132'
$generator = if ($Toolchain -eq 'VS2022v143') { 'Visual Studio 17 2022' } else { 'Visual Studio 18 2026' }
$toolset = if ($Toolchain -eq 'VS2022v143') { 'v143' } else { 'v145' }
$source = (Resolve-Path -LiteralPath $SourceRepository).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must not already exist.' }
if ($env:SigningClientSecret) { throw 'Unexpected upstream signing environment; use a clean build environment.' }
if ((& dotnet --version) -cne '8.0.423' -or $LASTEXITCODE -ne 0) { throw 'Build requires dotnet SDK 8.0.423 selected by the environment.' }
$wixVersion = (& wix --version)
if ($LASTEXITCODE -ne 0 -or $wixVersion -notmatch '^5\.0\.2(?:\+.*)?$') { throw 'Build requires WiX 5.0.2.' }
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { throw 'CMake is required.' }

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

$actualTree = (& git -C $source rev-parse "$commit^{tree}")
if ($LASTEXITCODE -ne 0 -or $actualTree -cne $tree) { throw 'Pinned source commit/tree unavailable or incorrect.' }

New-Item -ItemType Directory -Path $output | Out-Null
$archive = Join-Path $output 'source-upstream.tar'
$checkout = Join-Path $output 'source'
New-Item -ItemType Directory -Path $checkout | Out-Null
Invoke-Checked git @('-C', $source, 'archive', '--format=tar', "--output=$archive", $commit)
Invoke-Checked tar @('-xf', $archive, '-C', $checkout)
$securityPatch = Join-Path $PSScriptRoot 'Apply-OpcEnumSecurity.ps1'
$patchCopy = Join-Path $output 'Apply-OpcEnumSecurity.ps1'
Copy-Item -LiteralPath $securityPatch -Destination $patchCopy
$enumSource = Join-Path $checkout 'Source/Common/ServerEnumerator/OpcEnum.cpp'
$patchedHash = (& $patchCopy -SourceFile $enumSource | Select-Object -Last 1)
if (-not $patchedHash -or $patchedHash -notmatch '^[a-f0-9]{64}$') { throw 'OpcEnum security patch failed.' }
$categoryPatchCopy = Join-Path $output 'Apply-OpcCategorySecurity.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Apply-OpcCategorySecurity.ps1') -Destination $categoryPatchCopy
$categorySource = Join-Path $checkout 'Source/Common/CategoryManager/CategoryManager.cpp'
$categoryPatchedHash = (& $categoryPatchCopy -SourceFile $categorySource | Select-Object -Last 1)
if (-not $categoryPatchedHash -or $categoryPatchedHash -notmatch '^[a-f0-9]{64}$') { throw 'CategoryManager security patch failed.' }
$enumerationPatchCopy = Join-Path $output 'Apply-OpcEnumEnumeration.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Apply-OpcEnumEnumeration.ps1') -Destination $enumerationPatchCopy
$serverListSource = Join-Path $checkout 'Source/Common/ServerEnumerator/OpcSeLst.cpp'
$enumerationPatchedHash = (& $enumerationPatchCopy -SourceFile $serverListSource | Select-Object -Last 1)
if (-not $enumerationPatchedHash -or $enumerationPatchedHash -notmatch '^[a-f0-9]{64}$') { throw 'OpcEnum category enumeration patch failed.' }
$nativeInputNames = @('New-OpcRuntimeModule.ps1', 'native-runtime-inventory.json', 'opc-component-codes.txt', 'OpcGuidInventory.psm1')
foreach ($name in $nativeInputNames) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $output $name) }

if (((Get-Content (Join-Path $checkout 'build.txt') -Raw).Trim()) -cne '131') { throw 'Unexpected upstream build counter.' }
if ((Get-Content (Join-Path $checkout 'version.txt') -Raw) -notmatch '(?s)MAJOR=3\s+MINOR=1\s+REVISION=4') { throw 'Unexpected upstream version.' }

# Upstream increments build.txt and writes version.h in its own source tree.
# The archive is a fresh isolated copy; upstream never touches the supplied checkout.
# Its WiX extension auto-install is deliberately avoided: build only the runtime
# merge modules, without invoking the upstream MSI builder or installing anything.
@'
#define xstr(s) str(s)
#define str(s) #s
#define MAJOR_VERSION 3
#define MINOR_VERSION 1
#define REVISION_VERSION 4
#define BUILD_VERSION 132
#define COPYRIGHT_DATE "2026"
#define FILE_VERSION_TEXT xstr(MAJOR_VERSION) "." xstr(MINOR_VERSION) "." xstr(REVISION_VERSION) "." xstr(BUILD_VERSION)
#define PRODUCT_VERSION_TEXT xstr(MAJOR_VERSION) "." xstr(MINOR_VERSION) "." xstr(REVISION_VERSION) ".0"
'@ | Set-Content -LiteralPath (Join-Path $checkout 'Source/Include/version.h') -Encoding ascii

$modules = @()
foreach ($arch in @('x86', 'x64')) {
    $platform = if ($arch -eq 'x86') { 'Win32' } else { 'x64' }
    $build = Join-Path $output "build-$arch"
    $stage = Join-Path $output "stage-$arch"
    Invoke-Checked cmake @('-S', $checkout, '-B', $build, '-G', $generator, '-A', $platform, '-T', $toolset, '-DOPC_BUILD_TESTS=OFF', "-DCMAKE_INSTALL_PREFIX=$stage")
    Invoke-Checked cmake @('--build', $build, '--config', 'Release')
    Invoke-Checked cmake @('--install', $build, '--config', 'Release')
    $nativeAuthoring = Join-Path $output "native-$arch.wxs"
    & (Join-Path $output 'New-OpcRuntimeModule.ps1') -CoreBuildDirectory $output -Architecture $arch -OutputFile $nativeAuthoring
    $module = Join-Path $output "opc-com-proxystub-mergemodule-$version-$arch.msm"
    Invoke-Checked wix @('build', $nativeAuthoring, '-arch', $arch, '-wx', '-o', $module)
    if (!(Test-Path -LiteralPath $module -PathType Leaf)) { throw "Missing module: $module" }
    $modules += Get-Item -LiteralPath $module
}
$license = Join-Path $output 'LICENSE.md'
Copy-Item -LiteralPath (Join-Path $checkout 'LICENSE.md') -Destination $license
$files = @($modules) + @(Get-Item -LiteralPath $license)
$metadata = [ordered]@{
    source = 'https://github.com/OPCF-Members/OPC-Classic-CoreComponents'
    commit = $commit; tree = $tree; version = $version; configuration = 'Release'
    source_archive_sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    security_patch_sha256 = (Get-FileHash -LiteralPath $patchCopy -Algorithm SHA256).Hash.ToLowerInvariant()
    opcenum_patched_sha256 = $patchedHash
    category_patch_sha256 = (Get-FileHash -LiteralPath $categoryPatchCopy -Algorithm SHA256).Hash.ToLowerInvariant()
    category_patched_sha256 = $categoryPatchedHash
    enumeration_patch_sha256 = (Get-FileHash -LiteralPath $enumerationPatchCopy -Algorithm SHA256).Hash.ToLowerInvariant()
    enumeration_patched_sha256 = $enumerationPatchedHash
    category_com_module_sha256 = (Get-FileHash -LiteralPath (Join-Path $checkout 'Source/Shared/OpcUtilityClasses/COpcComModule.cpp') -Algorithm SHA256).Hash.ToLowerInvariant()
    native_inputs = @($nativeInputNames | ForEach-Object { [ordered]@{ name = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $output $_) -Algorithm SHA256).Hash.ToLowerInvariant() } })
    cmake = ((& cmake --version | Select-Object -First 1) -join '')
    wix = $wixVersion; dotnet_sdk = '8.0.423'; generator = $generator; toolset = $toolset
    cmake_cache_sha256 = @(foreach ($arch in @('x86', 'x64')) { (Get-FileHash -LiteralPath (Join-Path $output "build-$arch/CMakeCache.txt") -Algorithm SHA256).Hash.ToLowerInvariant() })
    files = @($files | ForEach-Object { [ordered]@{ name = $_.Name; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    note = 'Locally modified, unsigned build with explicit OpcEnum/CategoryManager security patches and native MSI ownership; hashes are not vendor signatures.'
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'build-metadata.json') -Encoding utf8
