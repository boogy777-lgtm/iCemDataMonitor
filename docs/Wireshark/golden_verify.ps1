# golden_verify.ps1 -- decode ACE 124-byte payloads from out\{sv}_{equip}.hex
# using FlecsEmu\templates\lantable_registry.json signal definitions.
# Offset law: payload_index = ByteOffset - 4. Encoding per ace_wire.signal_encoding.
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File golden_verify.ps1
param(
    [string]$HexDir   = $PSScriptRoot + "\out",
    [string]$Registry = "C:\Program Files (x86)\Beartooth HMI\FlecsEmu\templates\lantable_registry.json"
)
$ErrorActionPreference = "Stop"

$reg = Get-Content $Registry -Raw | ConvertFrom-Json

$svOrder = @("13120203","13120206","13120200","13120209","1312020C","1312020F")

function HexToBytes([string]$h) {
    $h = $h.Trim()
    $b = New-Object byte[] ($h.Length / 2)
    for ($i = 0; $i -lt $b.Length; $i++) { $b[$i] = [Convert]::ToByte($h.Substring($i*2,2),16) }
    return ,$b
}

function DecodeSig([byte[]]$payload, $sig) {
    $off = [int]$sig.byteOffset - 4
    $sz  = [int]$sig.size
    if ($off -lt 0 -or ($off + $sz) -gt $payload.Length) { return "[overflow O=$($sig.byteOffset)]" }
    $b = $payload[$off..($off+$sz-1)]
    $vt = ([string]$sig.valueType).ToUpperInvariant()
    switch ($vt) {
        "FF" {
            if ($sz -ne 4) { return "FF!sz$sz" }
            $le = New-Object byte[] 4
            $le[0] = $b[3]; $le[1] = $b[2]; $le[2] = $b[1]; $le[3] = $b[0]
            return ([BitConverter]::ToSingle($le, 0)).ToString("R")
        }
        "UL" { $v = 0L; foreach($x in $b){ $v = ($v -shl 8) -bor $x }; return ($v * [double]$sig.dasFactor).ToString("R") }
        "UI" { $v = 0L; foreach($x in $b){ $v = ($v -shl 8) -bor $x }; return ($v * [double]$sig.dasFactor).ToString("R") }
        "HX" { $v = 0L; foreach($x in $b){ $v = ($v -shl 8) -bor $x }; return ("0x{0:X8}" -f $v) }
        "SI" { $v = 0L; foreach($x in $b){ $v = ($v -shl 8) -bor $x }; if (($b[0] -band 0x80) -ne 0) { $v -= [long][Math]::Pow(2, 8*$sz) }; return ($v * [double]$sig.dasFactor).ToString("R") }
        "SL" { $v = 0L; foreach($x in $b){ $v = ($v -shl 8) -bor $x }; if (($b[0] -band 0x80) -ne 0) { $v -= [long][Math]::Pow(2, 8*$sz) }; return ($v * [double]$sig.dasFactor).ToString("R") }
        "UC" { return [System.Text.Encoding]::ASCII.GetString($b) }
        default { return "?type=$($sig.valueType)" }
    }
}

$global:fail = 0
foreach ($svKey in $svOrder) {
    $hexFile = Join-Path $HexDir "${svKey}_00A86F46.hex"
    if (-not (Test-Path $hexFile)) { Write-Output "MISSING $hexFile"; $global:fail++; continue }
    $payload = HexToBytes (Get-Content $hexFile -Raw)
    if ($payload.Length -ne 124) { Write-Output ("FAIL: {0} payload {1} bytes, expected 124" -f $svKey, $payload.Length); $global:fail++; continue }
    $svDef = $reg.serviceVectors | Where-Object { [uint32]$_.svNumber -eq [uint32]("0x" + $svKey) } | Select-Object -First 1
    if ($null -eq $svDef) { Write-Output "FAIL: sv 0x$svKey not found in registry"; $global:fail++; continue }
    Write-Output ("===== SV 0x{0} ({1}) -- first payload {2} bytes =====" -f $svKey, $svDef.name, $payload.Length)
    foreach ($sig in $svDef.signals) {
        $pidx = [int]$sig.byteOffset - 4
        if ($pidx -lt 0 -or ($pidx + [int]$sig.size) -gt 124) {
            Write-Output ("  {0} O={1} pidx={2} sz={3} -> NOT TRANSMITTABLE ((O-4)+Size>124)" -f $sig.name, $sig.byteOffset, $pidx, $sig.size)
            continue
        }
        $hexbytes = ($payload[$pidx..($pidx + [int]$sig.size - 1)] | ForEach-Object { $_.ToString("X2") }) -join " "
        $val = DecodeSig $payload $sig
        Write-Output ("  {0,-28} O={1,-4} pidx={2,-4} {3,-6} {4,-36} {5}  uom={6}" -f $sig.name, $sig.byteOffset, $pidx, $sig.valueType, $hexbytes, $val, $sig.uom)
    }
}
if ($global:fail -eq 0) { Write-Output ""; Write-Output "ALL FILES OK (124 B each)" } else { Write-Output "FAILURES: $global:fail" }
