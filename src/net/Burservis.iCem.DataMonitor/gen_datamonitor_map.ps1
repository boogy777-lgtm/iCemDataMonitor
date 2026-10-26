# gen_datamonitor_map.ps1 - regenerates DataMonitorAddressMap.cs from the registry (source of truth).
# Usage: powershell -ExecutionPolicy Bypass -File gen_datamonitor_map.ps1
# Source: <repo>\templates\variable_registry.json (resolved repo-relative below)
#
# LAYOUT v5 (user decree 2026-08-17 — bus register map v5):
#   ComMap (flecs family): address = 4 + index x 2   (com_stream 4..199; the v4
#     rebase 16+idx*2 is GONE — the v4 0..15 state-block overlap is REMOVED,
#     the state block is regs 0..3 only).
#   LanMap (lan family): address = 200 + index x 2  (lan_stream_crio 200..399)
#     — ONLY the first 96 entries (idx 0..95) = the 96 cRIO signals of the
#     observed ACE superframe (6 sv: 0x13120203/06/00/09/0C/0F, per
#     Wireshark/PROTOCOL.md); idx >= 96 (Truck Number/Node Address etc.) is
#     OUT of the curated view. 100 float slots >= 96 signals.
#   INVARIANT: ONE name -> ONE address PER MAP. Generated keys are asserted
#   unique within each map; the script FAILS if two different indices produce
#   the same key string in the same map.
#   LAN VENDOR ALIASES (user dispatch 2026-08-18, additive-only n+1): the
#   sidecar lan_vendor_aliases.csv carries the EXACT abbreviated cRIO display
#   names the vendor LAN source exposes (harvested from live POLLUNIV,
#   specs/FlecsEmu/Э2c_lan_zone_classification.csv). Aliases map to the
#   EXISTING addr=200+idx*2 entries; uniqueness asserted together with the
#   registry keys.
# NOTE: ASCII-only source (PowerShell 5.1 default encoding).

$ErrorActionPreference = 'Stop'
$registryPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'templates\variable_registry.json'
$aliasPath = Join-Path $PSScriptRoot 'lan_vendor_aliases.csv'
$outPath = Join-Path $PSScriptRoot 'DataMonitorAddressMap.cs'
$LanMaxIndex = 95   # v5: lan_stream_crio = 96 cRIO signals (idx 0..95), PROTOCOL.md curated view

# ── LAN VENDOR ALIASES (additive-only, rule n+1 — user dispatch 2026-08-18) ──
# Sidecar CSV with the EXACT abbreviated cRIO display names the vendor LAN
# source exposes (harvested from live POLLUNIV telemetry,
# specs/FlecsEmu/Э2c_lan_zone_classification.csv, audit 2026-08-18). Each
# alias maps to the EXISTING addr=200+idx*2 entry — never a new address,
# never a moved entry, never a registry change. Uniqueness is asserted
# TOGETHER with the registry keys below (an alias colliding with any key at
# a different address ⇒ FAIL — ambiguity is a generation-time error).
$lanAliasByIdx = @{}
foreach ($line in Get-Content $aliasPath) {
    $t = $line.Trim()
    if ($t.Length -eq 0 -or $t.StartsWith('idx,')) { continue }
    $parts = $t.Split(',', 2)
    $aidx = [int]$parts[0]
    $aname = $parts[1].Trim()
    if ($aidx -lt 0 -or $aidx -gt $LanMaxIndex) { throw "LAN ALIAS idx $aidx out of curated view (0..$LanMaxIndex): $t" }
    if ($aname.Length -eq 0) { throw "LAN ALIAS empty name: $t" }
    if (-not $lanAliasByIdx.ContainsKey($aidx)) { $lanAliasByIdx[$aidx] = New-Object System.Collections.ArrayList }
    [void]$lanAliasByIdx[$aidx].Add($aname)
}

$j = Get-Content $registryPath -Raw | ConvertFrom-Json
$comRows = New-Object System.Collections.ArrayList
$lanRows = New-Object System.Collections.ArrayList
foreach ($v in $j.variables) {
    if ($v.families.PSObject.Properties.Name -contains 'flecs') {
        $f = $v.families.flecs
        [void]$comRows.Add([pscustomobject]@{ idx = [int]$f.index; name = [string]$v.name; tapName = [string]$f.tapName; wireName = [string]$f.wireName; alias = [string]$f.alias })
    }
    if ($v.families.PSObject.Properties.Name -contains 'lan') {
        $f = $v.families.lan
        if ([int]$f.index -le $LanMaxIndex) {
            $vendorAliases = if ($lanAliasByIdx.ContainsKey([int]$f.index)) { $lanAliasByIdx[[int]$f.index] } else { @() }
            [void]$lanRows.Add([pscustomobject]@{ idx = [int]$f.index; name = [string]$v.name; wireName = [string]$f.wireName; alias = [string]$f.alias; vendorAliases = $vendorAliases })
        }
    }
}
$comEntries = $comRows | Sort-Object idx
$lanEntries = $lanRows | Sort-Object idx

# ── uniqueness assertion: one name -> one address PER MAP ─────────────────────
function Assert-Unique($entries, $mapName) {
    $seen = @{}
    foreach ($e in $entries) {
        $addr = $e.Addr
        foreach ($k in @($e.tapName, $e.name, $e.wireName, $e.alias) + @($e.vendorAliases)) {
            if ([string]::IsNullOrWhiteSpace($k)) { continue }
            if ($seen.ContainsKey($k)) {
                if ($seen[$k] -ne $addr) {
                    throw "MAP COLLISION [$mapName]: key '$k' maps to $($seen[$k]) and $addr (idx $($e.idx))"
                }
            }
            else { $seen[$k] = $addr }
        }
    }
}
Assert-Unique $comEntries 'com_stream'
Assert-Unique $lanEntries 'lan_stream_crio'

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// -----------------------------------------------------------------------------')
[void]$sb.AppendLine('// GENERATED FILE - do not hand-edit. Regenerate:')
[void]$sb.AppendLine('//   powershell -ExecutionPolicy Bypass -File gen_datamonitor_map.ps1')
    [void]$sb.AppendLine('// Source of truth: templates/variable_registry.json (repo-relative)')
[void]$sb.AppendLine('// LAYOUT v5 (bus register map v5, user decree 2026-08-17):')
[void]$sb.AppendLine('//   ComMap = flecs family: address = 4 + index x 2 (com_stream 4..199).')
[void]$sb.AppendLine('//   LanMap = lan family idx 0..95: address = 200 + index x 2 (lan_stream_crio')
[void]$sb.AppendLine('//   200..399; 96 cRIO signals of the observed ACE superframe per')
[void]$sb.AppendLine('//   Wireshark/PROTOCOL.md; idx >= 96 excluded from the curated view).')
[void]$sb.AppendLine('//   VENDOR ALIASES (additive-only, user dispatch 2026-08-18): abbreviated')
[void]$sb.AppendLine('//   cRIO display names from the vendor LAN source (live POLLUNIV harvest,')
[void]$sb.AppendLine('//   specs/FlecsEmu/Э2c_lan_zone_classification.csv) — sidecar')
[void]$sb.AppendLine('//   lan_vendor_aliases.csv; alias => the EXISTING addr=200+idx*2 entry;')
[void]$sb.AppendLine('//   never a new address, never a moved entry.')
[void]$sb.AppendLine('//   ONE name -> ONE address per map; uniqueness asserted at generation.')
[void]$sb.AppendLine('// DEFECT-1 NORMALIZATION: NormMaps are built at RUNTIME from the maps via')
[void]$sb.AppendLine('// NameNormalizer (ASCII alnum-only, lowercase). TryGet* = exact-name fast')
[void]$sb.AppendLine('// path, then normalized fallback. Ambiguous norm keys (same norm key,')
[void]$sb.AppendLine('// different addresses) are excluded - exact path only for them.')
[void]$sb.AppendLine('// -----------------------------------------------------------------------------')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('using System;')
[void]$sb.AppendLine('using System.Collections.Generic;')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('namespace Burservis.iCem.DataMonitor.Bus')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    internal static class DataMonitorAddressMap')
[void]$sb.AppendLine('    {')
[void]$sb.AppendLine('        private static readonly Dictionary<string, int> ComMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)')
[void]$sb.AppendLine('        {')
foreach ($e in $comEntries) {
    $addr = 4 + $e.idx * 2   # LAYOUT v5: com_stream addr = 4 + idx*2
    $keys = @($e.tapName, $e.name, $e.wireName, $e.alias) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    foreach ($k in $keys) {
        $esc = $k.Replace('"', '\"')
        [void]$sb.AppendLine("            { `"$esc`", $addr },   // idx $($e.idx) $($e.name)")
    }
}
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        private static readonly Dictionary<string, int> LanMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)')
[void]$sb.AppendLine('        {')
foreach ($e in $lanEntries) {
    $addr = 200 + $e.idx * 2   # LAYOUT v5: lan_stream_crio addr = 200 + idx*2
    $keys = @($e.wireName, $e.name, $e.alias) + @($e.vendorAliases) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    foreach ($k in $keys) {
        $esc = $k.Replace('"', '\"')
        [void]$sb.AppendLine("            { `"$esc`", $addr },   // idx $($e.idx) $($e.name)")
    }
}
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        // ── DEFECT-1 normalization fallbacks (built at runtime from the maps) ──')
[void]$sb.AppendLine('        private static string[] _ambiguousComNormKeys;')
[void]$sb.AppendLine('        private static string[] _ambiguousLanNormKeys;')
[void]$sb.AppendLine('        private static readonly Dictionary<string, int> ComNormMap = BuildNormMap(ComMap, out _ambiguousComNormKeys);')
[void]$sb.AppendLine('        private static readonly Dictionary<string, int> LanNormMap = BuildNormMap(LanMap, out _ambiguousLanNormKeys);')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        private static Dictionary<string, int> BuildNormMap(Dictionary<string, int> map, out string[] ambiguousKeys)')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            var result = new Dictionary<string, int>(StringComparer.Ordinal);')
[void]$sb.AppendLine('            var conflicts = new List<string>();')
[void]$sb.AppendLine('            foreach (KeyValuePair<string, int> pair in map)')
[void]$sb.AppendLine('            {')
[void]$sb.AppendLine('                string norm = NameNormalizer.Normalize(pair.Key);')
[void]$sb.AppendLine('                if (norm.Length == 0 || conflicts.Contains(norm))')
[void]$sb.AppendLine('                {')
[void]$sb.AppendLine('                    continue;')
[void]$sb.AppendLine('                }')
[void]$sb.AppendLine('                int existing;')
[void]$sb.AppendLine('                if (result.TryGetValue(norm, out existing))')
[void]$sb.AppendLine('                {')
[void]$sb.AppendLine('                    if (existing != pair.Value)')
[void]$sb.AppendLine('                    {')
[void]$sb.AppendLine('                        // ambiguous: one normalized key, two addresses - exclude')
[void]$sb.AppendLine('                        result.Remove(norm);')
[void]$sb.AppendLine('                        conflicts.Add(norm);')
[void]$sb.AppendLine('                    }')
[void]$sb.AppendLine('                }')
[void]$sb.AppendLine('                else')
[void]$sb.AppendLine('                {')
[void]$sb.AppendLine('                    result[norm] = pair.Value;')
[void]$sb.AppendLine('                }')
[void]$sb.AppendLine('            }')
[void]$sb.AppendLine('            ambiguousKeys = conflicts.ToArray();')
[void]$sb.AppendLine('            return result;')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        /// <summary>com_stream lookup (flecs family). Exact-name fast path first;')
[void]$sb.AppendLine('        /// miss = normalized fallback (DEFECT-1).</summary>')
[void]$sb.AppendLine('        public static bool TryGetComAddress(string name, out int address)')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            return TryGetAddressIn(ComMap, ComNormMap, name, out address);')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        /// <summary>lan_stream_crio lookup (lan family idx 0..95). Exact-name fast')
[void]$sb.AppendLine('        /// path first; miss = normalized fallback (DEFECT-1).</summary>')
[void]$sb.AppendLine('        public static bool TryGetLanAddress(string name, out int address)')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            return TryGetAddressIn(LanMap, LanNormMap, name, out address);')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        private static bool TryGetAddressIn(Dictionary<string, int> map, Dictionary<string, int> normMap, string name, out int address)')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            if (name != null && map.TryGetValue(name, out address))')
[void]$sb.AppendLine('            {')
[void]$sb.AppendLine('                return true;')
[void]$sb.AppendLine('            }')
[void]$sb.AppendLine('            string norm = NameNormalizer.Normalize(name);')
[void]$sb.AppendLine('            if (norm.Length > 0 && normMap.TryGetValue(norm, out address))')
[void]$sb.AppendLine('            {')
[void]$sb.AppendLine('                return true;')
[void]$sb.AppendLine('            }')
[void]$sb.AppendLine('            address = 0;')
[void]$sb.AppendLine('            return false;')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        /// <summary>Debug surface: every com (name, address) pair (tests + TapOut trace).</summary>')
[void]$sb.AppendLine('        public static IEnumerable<KeyValuePair<string, int>> DebugComEntries()')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            return ComMap;')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        /// <summary>Debug surface: every lan (name, address) pair (tests + TapOut trace).</summary>')
[void]$sb.AppendLine('        public static IEnumerable<KeyValuePair<string, int>> DebugLanEntries()')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            return LanMap;')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        /// <summary>Diagnostics: normalized key counts + ambiguous exclusions (DEFECT-1).</summary>')
[void]$sb.AppendLine('        public static int DebugComNormCount()')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            return ComNormMap.Count;')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        public static int DebugLanNormCount()')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            return LanNormMap.Count;')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        public static string[] DebugComAmbiguous()')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            return _ambiguousComNormKeys ?? new string[0];')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        public static string[] DebugLanAmbiguous()')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            return _ambiguousLanNormKeys ?? new string[0];')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')

Set-Content -Path $outPath -Value $sb.ToString() -Encoding ASCII
Write-Host ("Generated: " + $outPath + " (com=" + $comEntries.Count + " entries @4+idx*2, lan=" + $lanEntries.Count + " entries @200+idx*2, uniqueness asserted OK)")
