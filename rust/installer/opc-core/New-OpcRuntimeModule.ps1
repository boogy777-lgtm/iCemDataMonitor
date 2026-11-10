#requires -Version 7.4
# Compile-time authoring only. Never loads a native DLL or changes registration.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CoreBuildDirectory,
    [Parameter(Mandatory)][ValidateSet('x86', 'x64')][string] $Architecture,
    [Parameter(Mandatory)][string] $OutputFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$CoreBuildDirectory = (Resolve-Path -LiteralPath $CoreBuildDirectory).Path
$data = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-runtime-inventory.json') -Raw | ConvertFrom-Json -AsHashtable
if ($data.schema_version -ne 1 -or $data.source_commit -cne 'efe0d1d1ea86a8a727bf26a501a261765e836766') { throw 'Unreviewed native OPC inventory schema/source.' }
$sourceFile = Join-Path $CoreBuildDirectory 'source/WiX/MergeModule.wxs'
if ((Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash -ine 'b28f4dce16d2990a1370983ae840572b61a55dff023122cea1492890694c07a5') { throw 'Unreviewed upstream module authoring.' }
$source = [xml](Get-Content -LiteralPath $sourceFile -Raw)
$original = @($source.SelectNodes('//*[local-name()="Component"]'))
$codes = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'opc-component-codes.txt'))
if ($data.components.Count -ne $original.Count -or $data.components.Count -ne $codes.Count) { throw 'Incomplete native OPC file ownership inventory.' }
$seenCodes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenRegistry = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($component in $data.components) {
    $match = @($original | Where-Object { ('{' + $_.Guid + '}') -ceq $component.guid })
    if ($match.Count -ne 1 -or $component.guid -cnotin $codes -or -not $seenCodes.Add($component.guid) -or
        $match[0].Id -cne $component.id -or $match[0].File.Id -cne $component.file_id -or $match[0].File.Name -cne $component.file -or
        $component.file -cnotmatch '^[A-Za-z0-9_]+\.(dll|exe)$' -or $component.arch -cnotin @('x86', 'x64') -or $component.directory -cnotin @('system', 'common')) {
        throw 'Native OPC component identity differs from pinned upstream authoring.'
    }
    $parent = $match[0].ParentNode
    while ($parent.LocalName -cne 'StandardDirectory') { $parent = $parent.ParentNode }
    $expectedArch = switch -CaseSensitive ($parent.Id) { 'SystemFolder' { 'x86' } 'CommonFilesFolder' { 'x86' } 'CommonFiles64Folder' { 'x64' } default { throw 'Unreviewed native source directory.' } }
    $expectedDirectory = if ($parent.Id -ceq 'SystemFolder') { 'system' } else { 'common' }
    if ($component.arch -cne $expectedArch -or $component.directory -cne $expectedDirectory) { throw 'Native OPC component architecture/location changed.' }
    foreach ($row in $component.registry) {
        if ($row.values.Count -eq 0) { throw 'Native OPC keys must have value-backed ownership.' }
        if (-not $row.key.StartsWith('SOFTWARE\Classes\', [StringComparison]::Ordinal) -or
            -not $seenRegistry.Add($component.arch + ':' + $row.key)) { throw 'Foreign/duplicate native OPC registry ownership.' }
        $valueNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($value in $row.values) {
            if (-not $valueNames.Add($value.name) -or $value.kind -cnotin @('String', 'DWord', 'Binary')) { throw 'Unknown/duplicate native OPC registry value.' }
            if ($value.kind -ceq 'Binary' -and $value.value -cnotmatch '^(?:[0-9A-F]{2})+$') { throw 'Invalid native OPC binary registry data.' }
            if ($value.kind -ceq 'DWord' -and ($value.value -isnot [long] -and $value.value -isnot [int] -or $value.value -lt 0 -or $value.value -gt [uint32]::MaxValue)) { throw 'Invalid native OPC DWORD.' }
            if ($value.kind -ceq 'String') {
                $literal = [string]$value.value
                $fileReference = '[#' + $component.file_id + ']'
                $references = @($fileReference, ('"' + $fileReference + '"'), '[WindowsFolder]SysWOW64')
                if ($literal -match '[A-Za-z]:\\' -or ($literal.Contains('[') -and $literal -cnotin $references)) {
                    throw "Nonportable or foreign native OPC file reference: $($component.file) / $($row.key)"
                }
            }
            if ($value.name -cin @('AccessPermission', 'LaunchPermission')) {
                # Baseline-only candidate, still production-blocked. Operator grants
                # and preserving customized descriptors on repair remain a separate
                # security gate; never claim this baseline as accepted operator policy.
                $mask = if ($value.name -ceq 'AccessPermission') { '0x3' } else { '0xb' }
                $expected = [Security.AccessControl.RawSecurityDescriptor]::new("O:SYG:SYD:(A;;$mask;;;SY)(A;;$mask;;;BA)")
                $actual = [Security.AccessControl.RawSecurityDescriptor]::new([Convert]::FromHexString($value.value), 0)
                if ($value.kind -cne 'Binary' -or $actual.GetSddlForm('All') -cne $expected.GetSddlForm('All')) { throw 'Native OPC DCOM baseline changed.' }
            }
            if ($value.name -ceq 'AuthenticationLevel' -and ($value.kind -cne 'DWord' -or $value.value -ne 6)) { throw 'Native OPC authentication baseline changed.' }
        }
    }
}
$services = @($data.components | Where-Object { $_.Contains('service') })
if ($services.Count -ne 1 -or $services[0].arch -cne 'x86' -or $services[0].file -cne 'OpcEnum.exe' -or
    $services[0].service.name -cne 'OpcEnum' -or $services[0].service.display_name -cne 'OpcEnum' -or
    $services[0].service.account -cne 'LocalSystem' -or ($services[0].service.dependencies -join ',') -cne 'RPCSS') {
    throw 'Unreviewed native OPC service ownership/configuration.'
}
$baselines = @($data.components.registry | Where-Object key -CMatch '^SOFTWARE\\Classes\\AppID\\\{[^}]+\}$')
if ($baselines.Count -ne 2) { throw 'Incomplete native OPC AppID security coverage.' }
foreach ($baseline in $baselines) {
    foreach ($name in @('AccessPermission', 'LaunchPermission', 'AuthenticationLevel')) {
        if (@($baseline.values | Where-Object name -CEQ $name).Count -ne 1) { throw 'Missing native OPC AppID security value.' }
    }
}
$components = @($data.components | Where-Object arch -CEQ $Architecture)
if ($components.Count -ne 9) { throw 'Incomplete native OPC architecture inventory.' }
# Completeness is checked against actual MIDL proxy vtables, not merely a count
# or a copied snapshot. A new/renamed/method-count-changed interface cannot be lost.
Import-Module (Join-Path $PSScriptRoot 'OpcGuidInventory.psm1') -Force
$iids = @{}
foreach ($iid in (Read-OpcGuidInventory (Join-Path $CoreBuildDirectory 'stage-x86/include'))) { $iids[$iid.name] = $iid.guid }
foreach ($component in @($components | Where-Object { $_.file.EndsWith('.dll', [StringComparison]::Ordinal) })) {
    $proxyDirectory = Join-Path $CoreBuildDirectory ("build-$Architecture/" + [IO.Path]::GetFileNameWithoutExtension($component.file))
    $proxyFiles = @(Get-ChildItem -LiteralPath $proxyDirectory -Filter '*_p.c' -File)
    if ($proxyFiles.Count -ne 1) { throw 'Missing/extra MIDL proxy source file.' }
    $proxyText = [IO.File]::ReadAllText($proxyFiles[0].FullName)
    $vtables = [regex]::Matches($proxyText, '(?s)CINTERFACE_PROXY_VTABLE\((\d+)\)\s+_(\w+)ProxyVtbl\s*=\s*\{[^{}]*?&IID_(\w+)')
    $declarations = [regex]::Matches($proxyText, 'CINTERFACE_PROXY_VTABLE\(')
    if ($vtables.Count -eq 0 -or $vtables.Count -ne $declarations.Count) { throw 'Incomplete MIDL proxy vtable inventory.' }
    $expectedInterfaces = [Collections.Generic.List[string]]::new()
    foreach ($vtable in $vtables) {
        $name = $vtable.Groups[3].Value
        $guid = $iids['IID_' + $name]
        if (-not $guid -or $vtable.Groups[2].Value -cne $name) { throw 'Unknown MIDL proxy IID.' }
        $expectedInterfaces.Add("$guid|$name|$($vtable.Groups[1].Value)")
    }
    $actualInterfaces = [Collections.Generic.List[string]]::new()
    foreach ($row in $component.registry) {
        if ($row.key -cmatch '^SOFTWARE\\Classes\\Interface\\(\{[^}]+\})$') {
            $guid = $Matches[1]
            $name = @($row.values | Where-Object name -CEQ '')[0].value
            $methodsRow = @($component.registry | Where-Object key -CEQ ($row.key + '\NumMethods'))
            if ($methodsRow.Count -ne 1) { throw 'Missing native proxy method-count registration.' }
            $methods = @($methodsRow[0].values | Where-Object name -CEQ '')[0].value
            $actualInterfaces.Add("$guid|$name|$methods")
        }
    }
    if ((($actualInterfaces | Sort-Object) -join ';') -cne (($expectedInterfaces | Sort-Object) -join ';')) { throw 'Native registry inventory differs from compiled MIDL interfaces.' }
    $firstProxy = [regex]::Match($proxyText, '(?s)_ProxyVtblList\[\]\s*=\s*\{\s*\(\s*CInterfaceProxyVtbl\s*\*\s*\)\s*&_(\w+)ProxyVtbl')
    if (-not $firstProxy.Success) { throw 'Missing MIDL proxy class identity.' }
    $proxyGuid = $iids['IID_' + $firstProxy.Groups[1].Value]
    foreach ($row in @($component.registry | Where-Object { $_.key.EndsWith('\ProxyStubClsid32', [StringComparison]::Ordinal) })) {
        if ($row.values.Count -ne 1 -or $row.values[0].value -cne $proxyGuid) { throw 'Native proxy factory CLSID differs from MIDL.' }
    }
    foreach ($identity in $expectedInterfaces) {
        $guid = $identity.Split('|')[0]
        $links = @($component.registry | Where-Object key -CEQ "SOFTWARE\Classes\Interface\$guid\ProxyStubClsid32")
        if ($links.Count -ne 1 -or $links[0].values.Count -ne 1 -or $links[0].values[0].value -cne $proxyGuid) { throw 'Missing/mismatched native interface proxy link.' }
    }
    if (@($component.registry | Where-Object key -CEQ "SOFTWARE\Classes\CLSID\$proxyGuid\InProcServer32").Count -ne 1) { throw 'Missing native proxy factory registration.' }
}
$output = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $output) { throw 'Native module authoring output already exists.' }
$document = [xml]::new()
$ns = 'http://wixtoolset.org/schemas/v4/wxs'
function Element([Xml.XmlElement] $parent, [string] $name, [hashtable] $attributes) {
    $node = $document.CreateElement($name, $ns)
    foreach ($key in $attributes.Keys) { $node.SetAttribute($key, [string]$attributes[$key]) }
    $null = $parent.AppendChild($node)
    return ,$node
}
$wix = $document.CreateElement('Wix', $ns)
$null = $document.AppendChild($wix)
$moduleGuid = if ($Architecture -ceq 'x86') { 'E25DF0F2-0F29-47E8-8537-265285ED9757' } else { 'E68AB89F-6DF7-4750-8BC1-F14B2064F313' }
$module = Element $wix 'Module' @{ Id = 'MergeModule'; Guid = $moduleGuid; Language = '1033'; Version = '3.1.4.132' }
$null = Element $module 'SummaryInformation' @{ Manufacturer = 'iCem Data Monitor'; Description = "Source-built OPC runtime ($Architecture), native transactional ownership" }
$directories = @{}
if ($Architecture -ceq 'x86') {
    # Explicit physical WOW64 directory avoids combined-x64-MSI SystemFolder
    # dual-folder path mismatch, without changing any canonical file/component GUID.
    $windows = Element $module 'StandardDirectory' @{ Id = 'WindowsFolder' }
    $directories.system = Element $windows 'Directory' @{ Id = 'IcemOpcWow64'; Name = 'SysWOW64' }
}
$commonId = if ($Architecture -ceq 'x86') { 'CommonFilesFolder' } else { 'CommonFiles64Folder' }
$common = Element $module 'StandardDirectory' @{ Id = $commonId }
$foundation = Element $common 'Directory' @{ Id = 'OPCFoundationFolder'; Name = 'OPC Foundation' }
$directories.common = Element $foundation 'Directory' @{ Id = 'OPCBinFolder'; Name = 'Bin' }
$index = 0
foreach ($entry in $components) {
    $binary = Join-Path $CoreBuildDirectory "stage-$Architecture/bin/$($entry.file)"
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing source-built native binary: $binary" }
    $component = Element $directories[$entry.directory] 'Component' @{ Id = $entry.id; Guid = $entry.guid; Bitness = $(if ($Architecture -ceq 'x86') { 'always32' } else { 'always64' }) }
    $null = Element $component 'File' @{ Id = $entry.file_id; Name = $entry.file; Source = $binary; KeyPath = 'yes' }
    foreach ($row in $entry.registry) {
        $index++
        $attributes = @{ Root = 'HKLM'; Key = $row.key }
        $key = Element $component 'RegistryKey' $attributes
        foreach ($value in $row.values) {
            $index++
            $kind = switch -CaseSensitive ($value.kind) { 'String' { 'string' } 'DWord' { 'integer' } 'Binary' { 'binary' } default { throw 'Unchecked registry kind.' } }
            $attributes = @{ Id = "OpcValue$index"; Type = $kind; Value = $value.value }
            if ($value.name.Length -gt 0) { $attributes.Name = $value.name }
            $null = Element $key 'RegistryValue' $attributes
        }
    }
    if ($entry.Contains('service')) {
        $service = $entry.service
        $install = Element $component 'ServiceInstall' @{ Id = 'OpcEnumService'; Name = $service.name; DisplayName = $service.display_name; Account = $service.account; Type = 'ownProcess'; Start = 'demand'; ErrorControl = 'normal'; Vital = 'yes' }
        foreach ($dependency in $service.dependencies) { $null = Element $install 'ServiceDependency' @{ Id = $dependency } }
        $null = Element $component 'ServiceControl' @{ Id = 'OpcEnumControl'; Name = $service.name; Stop = 'both'; Remove = 'uninstall'; Wait = 'yes' }
    }
}
$settings = [Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$writer = [Xml.XmlWriter]::Create($output, $settings)
try { $document.Save($writer) } finally { $writer.Dispose() }
Write-Output "Generated native $Architecture module authoring; no system registration performed."
