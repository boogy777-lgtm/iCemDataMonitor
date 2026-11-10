Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Same reviewed nine MIDL bodies as the original pins. Only the three generated
# metadata comments and CRLF/LF differ between Windows SDKs/build directories.
# Never discard comments globally: code inserted outside these blocks must fail.
$IidHashes = @{
    'opc_ae_i.c' = 'ecc1b9ea86dd03aff25baf2d9dbc7c2aaf01ba663690c2a33942da950fdb56db'
    'opcbc_i.c' = '04bc1c4cc97af730499b4bfb6991c99216cb75fb2c346112261eb2a7066a08f5'
    'OpcCmd_i.c' = 'dec420f93b5f4bea3b0c22fc667b58d569310125b8bd7a6e50f5e2caff2961ed'
    'opccomn_i.c' = '34d23a7f64901b536d85b097544d2ff3ccb626f1e58931826f742bcac1562982'
    'opcda_i.c' = 'fe2645465d60ae2804f73d4482a87ab169f1cbeb7a267c5e4ad5870c3af3dcf1'
    'OpcDx_i.c' = '63e6ae30bc68cb03b677a47cd31da3ccb9f8b6e99c4cae3e755bfb1d348847db'
    'OpcEnum_i.c' = 'c11ff1e89bb1f03f8e976e9c2d3034fca9beb13b00764f2e2d58982976911c7d'
    'opchda_i.c' = '293d68360c61ff3789935367ba412db27a9426d488a5943bd4dad610f8e18e7c'
    'opcSec_i.c' = '6b14b44e89e6db77979f94942f0b8925562a31f95350b384d426f6d5859e75b7'
}

function Read-OpcGuidInventory([string] $IncludeDirectory) {
    $names = @($IidHashes.Keys | Sort-Object)
    $actual = @(Get-ChildItem -LiteralPath $IncludeDirectory -Filter '*_i.c' -File | Select-Object -ExpandProperty Name | Sort-Object)
    if (($actual -join '|') -cne ($names -join '|')) { throw 'Unexpected MIDL file inventory.' }
    $metadataPatterns = [ordered]@{
        '(?m)^ /\* File created by MIDL compiler version [0-9.]+ \*/$' = ' /* MIDL compiler metadata */'
        '(?m)^/\* at [^*]*\*/' = '/* MIDL timestamp metadata */'
        '(?m)^/\* Compiler settings for [^*]*\*/' = '/* MIDL settings metadata */'
    }
    $pattern = 'MIDL_DEFINE_GUID\(\s*(IID|CLSID)\s*,\s*((?:IID_|LIBID_|CLSID_)\w+)\s*,\s*((?:0x[0-9a-fA-F]+\s*,\s*){10}0x[0-9a-fA-F]+)\s*\)'
    foreach ($name in $names) {
        $text = [IO.File]::ReadAllText((Join-Path $IncludeDirectory $name)).Replace("`r`n", "`n")
        foreach ($metadataPattern in $metadataPatterns.Keys) {
            if ([regex]::Matches($text, $metadataPattern).Count -ne 1) { throw "Unexpected MIDL metadata heading: $name" }
            $text = [regex]::Replace($text, $metadataPattern, $metadataPatterns[$metadataPattern])
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-', '') }
        finally { $sha.Dispose() }
        if ($hash -ine $IidHashes[$name]) { throw "Unreviewed MIDL inventory: $name (canonical SHA256 $hash)" }
        $entries = [regex]::Matches($text, $pattern)
        $declarations = [regex]::Matches($text, 'MIDL_DEFINE_GUID\(\s*(?:IID|CLSID)\s*,')
        if ($entries.Count -eq 0 -or $entries.Count -ne $declarations.Count) { throw "Incomplete MIDL inventory: $name" }
        foreach ($entry in $entries) {
            $parts = @($entry.Groups[3].Value -split ',' | ForEach-Object { [Convert]::ToUInt32($_.Trim().Substring(2), 16) })
            [pscustomobject]@{
                file = $name; kind = $entry.Groups[1].Value; name = $entry.Groups[2].Value
                guid = ('{{{0:X8}-{1:X4}-{2:X4}-{3:X2}{4:X2}-{5:X2}{6:X2}{7:X2}{8:X2}{9:X2}{10:X2}}}' -f $parts)
            }
        }
    }
}
Export-ModuleMember -Function Read-OpcGuidInventory
