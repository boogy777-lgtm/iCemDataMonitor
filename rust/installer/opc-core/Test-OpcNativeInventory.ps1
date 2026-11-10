#requires -Version 7.4
# Pure authoring/data mutations; no MSI execution, DLL loading or registration.
[CmdletBinding()]
param([Parameter(Mandatory)][string] $CoreBuildDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$CoreBuildDirectory = (Resolve-Path -LiteralPath $CoreBuildDirectory).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('icem-native-opc-inventory-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
foreach ($name in @('New-OpcRuntimeModule.ps1', 'OpcGuidInventory.psm1', 'opc-component-codes.txt')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $work }
$original = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'native-runtime-inventory.json'))
$scriptFile = Join-Path $work 'New-OpcRuntimeModule.ps1'
$inventoryFile = Join-Path $work 'native-runtime-inventory.json'
function AppId($data) { return @($data.components.registry | Where-Object key -CEQ 'SOFTWARE\Classes\AppID\{13486D44-4821-11D2-A494-3CB306C10000}')[0] }
$cases = @(
    @{ name = 'missing file'; change = { param($d) $d.components = @($d.components | Select-Object -Skip 1) }; error = '^Incomplete native OPC file' },
    @{ name = 'duplicate file'; change = { param($d) $d.components[1] = $d.components[0] }; error = '^Native OPC component identity' },
    @{ name = 'foreign component'; change = { param($d) $d.components[0].guid = '{11111111-1111-1111-1111-111111111111}' }; error = '^Native OPC component identity' },
    @{ name = 'wrong file identity'; change = { param($d) $d.components[0].file_id = 'file_foreign' }; error = '^Native OPC component identity' },
    @{ name = 'wrong architecture'; change = { param($d) $d.components[0].arch = 'x64' }; error = '^Native OPC component architecture' },
    @{ name = 'relocated component'; change = { param($d) $d.components[0].directory = 'common' }; error = '^Native OPC component architecture' },
    @{ name = 'foreign registry'; change = { param($d) $d.components[0].registry[0].key = 'SOFTWARE\Foreign' }; error = '^Foreign/duplicate native OPC registry' },
    @{ name = 'duplicate registry'; change = { param($d) $d.components[0].registry += $d.components[0].registry[0] }; error = '^Foreign/duplicate native OPC registry' },
    @{ name = 'duplicate value'; change = { param($d) $d.components[0].registry[0].values += $d.components[0].registry[0].values[0] }; error = '^Unknown/duplicate native OPC registry value' },
    @{ name = 'unknown value kind'; change = { param($d) $d.components[0].registry[0].values[0].kind = 'ExpandString' }; error = '^Unknown/duplicate native OPC registry value' },
    @{ name = 'absolute file'; change = { param($d) $d.components[0].registry[1].values[0].value = 'C:\Foreign.dll' }; error = '^Nonportable or foreign native OPC file' },
    @{ name = 'foreign file reference'; change = { param($d) $d.components[0].registry[1].values[0].value = '[#foreign]' }; error = '^Nonportable or foreign native OPC file' },
    @{ name = 'missing method count'; change = { param($d) $key = @($d.components[0].registry | Where-Object key -CLike '*\NumMethods')[0].key; $d.components[0].registry = @($d.components[0].registry | Where-Object key -CNE $key) }; error = '^Missing native proxy method-count' },
    @{ name = 'changed method count'; change = { param($d) @($d.components[0].registry | Where-Object key -CLike '*\NumMethods')[0].values[0].value = '777' }; error = '^Native registry inventory differs from compiled MIDL' },
    @{ name = 'renamed interface'; change = { param($d) $d.components[0].registry[2].values[0].value = 'ForeignInterface' }; error = '^Native registry inventory differs from compiled MIDL' },
    @{ name = 'missing proxy link'; change = { param($d) $key = @($d.components[0].registry | Where-Object key -CLike '*\ProxyStubClsid32')[0].key; $d.components[0].registry = @($d.components[0].registry | Where-Object key -CNE $key) }; error = '^Missing/mismatched native interface proxy link' },
    @{ name = 'wrong proxy factory'; change = { param($d) @($d.components[0].registry | Where-Object key -CLike '*\ProxyStubClsid32')[0].values[0].value = '{11111111-1111-1111-1111-111111111111}' }; error = '^Native proxy factory CLSID differs from MIDL' },
    @{ name = 'missing proxy factory'; change = { param($d) $d.components[0].registry = @($d.components[0].registry | Where-Object key -CNotLike '*\InProcServer32') }; error = '^Missing native proxy factory registration' },
    @{ name = 'invalid binary'; change = { param($d) @((AppId $d).values | Where-Object name -CEQ 'AccessPermission')[0].value = 'ABC' }; error = '^Invalid native OPC binary' },
    @{ name = 'widened DCOM'; change = { param($d) $sd = [Security.AccessControl.RawSecurityDescriptor]::new('O:SYG:SYD:(A;;0x3;;;WD)'); $bytes = [byte[]]::new($sd.BinaryLength); $sd.GetBinaryForm($bytes, 0); @((AppId $d).values | Where-Object name -CEQ 'AccessPermission')[0].value = [Convert]::ToHexString($bytes) }; error = '^Native OPC DCOM baseline changed' },
    @{ name = 'missing DCOM'; change = { param($d) $row = AppId $d; $row.values = @($row.values | Where-Object name -CNE 'AccessPermission') }; error = '^Missing native OPC AppID security value' },
    @{ name = 'weak authentication'; change = { param($d) @((AppId $d).values | Where-Object name -CEQ 'AuthenticationLevel')[0].value = 1 }; error = '^Native OPC authentication baseline changed' },
    @{ name = 'foreign service'; change = { param($d) $d.components[6].service.name = 'ForeignService' }; error = '^Unreviewed native OPC service' },
    @{ name = 'changed service account'; change = { param($d) $d.components[6].service.account = 'Guest' }; error = '^Unreviewed native OPC service' },
    @{ name = 'extra service'; change = { param($d) $d.components[0].service = $d.components[6].service }; error = '^Unreviewed native OPC service' }
)
$checks = 0
try {
    [IO.File]::WriteAllText($inventoryFile, $original)
    foreach ($arch in @('x86', 'x64')) {
        $output = Join-Path $work "valid-$arch.wxs"
        $null = & $scriptFile -CoreBuildDirectory $CoreBuildDirectory -Architecture $arch -OutputFile $output
        $xml = [xml]([IO.File]::ReadAllText($output))
        if (@($xml.SelectNodes('//*[local-name()="File"]')).Count -ne 9 -or
            @($xml.SelectNodes('//*[local-name()="CustomAction" or local-name()="RemoveRegistry" or @SelfRegCost or @ForceDeleteOnUninstall]')).Count) { throw 'Native authoring reintroduced untracked/destructive mechanisms' }
        $expectedServices = if ($arch -ceq 'x86') { 1 } else { 0 }
        if (@($xml.SelectNodes('//*[local-name()="ServiceInstall"]')).Count -ne $expectedServices) { throw 'Native service ownership missing' }
        $checks++
    }
    foreach ($case in $cases) {
        $data = ConvertFrom-Json -InputObject $original -AsHashtable
        & $case.change $data
        [IO.File]::WriteAllText($inventoryFile, (ConvertTo-Json -InputObject $data -Depth 30))
        $output = Join-Path $work ('invalid-' + $checks + '.wxs')
        $rejected = $false
        try { $null = & $scriptFile -CoreBuildDirectory $CoreBuildDirectory -Architecture x86 -OutputFile $output }
        catch {
            if ($_.Exception.Message -notmatch $case.error) { throw "Wrong rejection for $($case.name): $($_.Exception.Message)" }
            $rejected = $true
        }
        if (-not $rejected -or (Test-Path -LiteralPath $output)) { throw "Accepted or emitted mutated authoring: $($case.name)" }
        $checks++
    }
    Write-Output "PASS: $checks native ownership authoring/completeness/security checks; no MSI installation."
} finally {
    foreach ($file in Get-ChildItem -LiteralPath $work -File) { [IO.File]::Delete($file.FullName) }
    [IO.Directory]::Delete($work)
}
