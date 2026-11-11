<#
.SYNOPSIS
  Deterministic WiX v5 payload harvester for the iCemDataMonitor MSI.

.DESCRIPTION
  Walks the staging folders and emits installer\payload.wxs with:

    * ComponentGroup Payload.Root  -> files under MONITORROOT (flat layout:
      host\, shell\ and emulator\ staging folders are merged at the monitor
      root, subfolders like templates\ are preserved as nested directories
      under MONITORROOT). The separate CLIDIR mount is RETIRED 2026-08-31
      (owner decree «пункт 3»): the cli merged into the host exe as argv
      mode (CliMode\CliForwarder) — no cli\ folder is staged anymore.
    * ComponentGroup Payload.Emulator -> files under EMULATORDIR. RETIRED
      2026-08-31 (owner decree «1+2»): the emulator merges FLAT into
      MONITORROOT via -RootFolders (its WinUI runtime was 94% byte-identical
      to the shell's). Pass -EmulatorFolder '' to skip this mount — the
      group stays emitted but empty.
    * ComponentGroup Payload.Addin -> files under ICEMMODULES (the iCem
      AddIn dll: [ICEMDIR]\Bin\Modules).
    * ComponentGroup Payload.All   -> aggregates all groups (this is the
      only group referenced from Product.wxs).

  Deterministic identifiers: Id = 'Dir.'/'Cmp.'/'Fil.' + sanitized
  '<group>.<relative-path>'. Identifiers longer than 60 chars are truncated
  and salted with a short SHA1 of the original path (stable across runs).
  File Source paths are emitted ABSOLUTE (WiX resolves relative sources
  against the wix build CWD, not the wxs folder).

  Priority law: two staged files with the same relative destination path
  but different content resolve keep-first (mount order host > cli > shell),
  each difference logged as a WARNING; identical content dedupes silently.

.PARAMETER StageRoot
  Absolute path to the staging root (contains host\, shell\, emulator\, addin\).

.PARAMETER OutputPath
  Absolute path of the payload.wxs to generate.

.PARAMETER RootFolders
  Staging folders that are merged into MONITORROOT. Default: host, shell
  (2026-08-31: the emulator rides RootFolders too — the flat merge; the cli
  stage no longer exists, its CLIDIR mount is retired).

.PARAMETER EmulatorFolder
  Staging folder mounted into EMULATORDIR. Default: emulator. Pass '' to
  skip the mount entirely (2026-08-31 flat-merge: the emulator rides
  -RootFolders instead and lands at the monitor root).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$StageRoot,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string[]]$RootFolders = @('host', 'shell'),
    [string]$EmulatorFolder = 'emulator',
    # 2026-09-02 (TFM-asset law, see build-msi.ps1 step 10): file names whose
    # cross-stage hash mismatch is EXPECTED — the host chain (plain net10.0)
    # and the WinUI chains (net10.0-windows10.0.19041) resolve DIFFERENT
    # lib\ assets of the same pinned package version by NuGet design. The
    # -RootFolders order makes the windows-TFM asset win; the mismatch is
    # logged as info, not a loud warning. Any OTHER mismatch still warns.
    [string[]]$ExpectedAssetMismatch = @()
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# CLI -File passing flattens arrays: accept 'host,cli,shell' and split.
$RootFolders = @($RootFolders | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$ExpectedAssetMismatch = @($ExpectedAssetMismatch | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$RootMount = 'MONITORROOT'
$EmulatorMount = 'EMULATORDIR'  # retired 2026-08-31 (flat merge) — emitted only when -EmulatorFolder is passed
$ModulesMount = 'ICEMMODULES'

function Sanitize-Id([string]$raw) {
    # MSI identifiers: letters/digits/_/., must not start with a digit.
    # All emitted ids start with a letter prefix, so only character mapping
    # and length (<= 72) matter. Non-token chars become '_'.
    $s = $raw -replace '[^A-Za-z0-9_.]', '_'
    if ($s.Length -gt 60) {
        $sha = New-Object System.Security.Cryptography.SHA1Managed
        $hashBytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($raw))
        $hex = -join ($hashBytes[0..3] | ForEach-Object { $_.ToString('x2') })
        $s = $s.Substring(0, 52) + '_' + $hex
    }
    return $s
}

function Escape-Xml([string]$v) {
    $v = $v -replace '&', '&amp;'
    $v = $v -replace '<', '&lt;'
    $v = $v -replace '>', '&gt;'
    $v = $v -replace '"', '&quot;'
    $v = $v -replace "'", '&apos;'
    return $v
}

function Get-Relative([string]$base, [string]$full) {
    return $full.Substring($base.Length).TrimStart('\')
}

function New-DeterministicGuid([string]$key) {
    # Stable name-based GUID (RFC 4122 v5 layout, SHA1 of the component key).
    # Required for components that are NOT rooted in a standard directory
    # (the AddIn under the property-rooted ICEMMODULES): WiX cannot
    # auto-generate ("Guid="*"") GUIDs there, and the GUID must stay stable
    # across builds for the extractor's upgrade semantics.
    $sha = New-Object System.Security.Cryptography.SHA1Managed
    $b = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($key.ToLowerInvariant()))
    $b[7] = ($b[7] -band 0x0F) -bor 0x50
    $b[8] = ($b[8] -band 0x3F) -bor 0x80
    return '{0}{1}{2}{3}-{4}{5}-{6}{7}-{8}{9}-{10}{11}{12}{13}{14}{15}' -f ($b[0..15] | ForEach-Object { $_.ToString('x2') })
}

# ---------------------------------------------------------------- collect ---
# key: '<mount>:<relpath>' -> source file path (lowercase for case-insens. fs)
$mounts = @{}          # mount id  -> group label
$filesByMount = @{}    # mount id  -> list of @{ Rel; Src; Group }
$dirsByMount = @{}     # mount id  -> set of directory relpaths
$script:deduped = 0
$script:keepFirst = 0

function Add-MountFiles([string]$mountId, [string]$groupLabel, [string]$folder) {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return }
    if (-not $filesByMount.ContainsKey($mountId)) {
        $filesByMount[$mountId] = New-Object System.Collections.ArrayList
        $dirsByMount[$mountId] = New-Object System.Collections.ArrayList
    }
    $base = (Resolve-Path -LiteralPath $folder).Path
    Get-ChildItem -LiteralPath $folder -Recurse -File | ForEach-Object {
        $rel = Get-Relative $base $_.FullName
        $key = "$mountId`:$rel".ToLowerInvariant()
        if ($mounts.ContainsKey($key)) {
            $existing = $mounts[$key]
            if ($existing -ine $_.FullName) {
                $h1 = (Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash
                $h2 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                if ($h1 -ne $h2) {
                    # PRIORITY LAW (flat MONITORROOT layout): the first staged
                    # copy wins (mount order = host > cli > shell). Shared
                    # assemblies are the SAME source built with different
                    # msbuild flags (anycpu vs Platform=x64) -> different
                    # bytes, same IL. Keep-first is intentional; identical
                    # names with genuinely different payloads would be a
                    # packaging bug, so the warning is LOUD.
                    # 2026-09-02: names on -ExpectedAssetMismatch (windows-TFM
                    # vs plain-TFM NuGet assets of one pinned package) are a
                    # documented by-design case -> info line, not a warning.
                    if ($ExpectedAssetMismatch -contains $_.Name) {
                        Write-Host "   keep-first (expected TFM-asset mismatch): $rel -> '$existing'"
                    } else {
                        Write-Warning "KEEP-FIRST: '$rel' differs across stages (sha256 $($h1.Substring(0,8)).. vs $($h2.Substring(0,8))..) - using '$existing'"
                    }
                    $script:keepFirst++
                } else {
                    Write-Host "   dedupe: $rel (identical sha256, first copy wins)"
                    $script:deduped++
                }
            }
            return
        }
        $mounts[$key] = $_.FullName
        [void]$filesByMount[$mountId].Add(@{ Rel = $rel; Src = $_.FullName; Group = $groupLabel })
        $dirRel = Split-Path -Parent $rel
        while ($dirRel -and $dirRel -ne '.') {
            if (-not ($dirsByMount[$mountId] -contains $dirRel)) {
                [void]$dirsByMount[$mountId].Add($dirRel)
            }
            $dirRel = Split-Path -Parent $dirRel
        }
    }
}

foreach ($g in $RootFolders) {
    Add-MountFiles $RootMount $g (Join-Path $StageRoot $g)
}
if ($EmulatorFolder) {
    Add-MountFiles $EmulatorMount 'emulator' (Join-Path $StageRoot $EmulatorFolder)
}
Add-MountFiles $ModulesMount 'addin' (Join-Path $StageRoot 'addin')

# -------------------------------------------------------------- directory ---
function Emit-DirectoryTree([System.Collections.ArrayList]$dirs, [string]$mountId) {
    if (-not $dirs -or $dirs.Count -eq 0) { return @() }
    $sorted = @($dirs | Sort-Object -Unique)
    $lines = New-Object System.Collections.ArrayList
    # emit nested; every path has its ancestors present by construction
    function Emit-One([string]$p, [int]$depth) {
        $indent = '      ' + ('  ' * $depth)
        $name = Split-Path -Leaf $p
        # mount-qualified: 'templates' under MONITORROOT and under CLIDIR are
        # DIFFERENT directories (WIX0091 would fire on a bare 'Dir.templates').
        $id = 'Dir.' + (Sanitize-Id "$mountId.$p")
        [void]$lines.Add("$indent<Directory Id=`"$id`" Name=`"$(Escape-Xml $name)`">")
        $childPrefix = $p + '\'
        foreach ($child in $sorted) {
            if ($child.StartsWith($childPrefix) -and
                ($child.Substring($childPrefix.Length) -notmatch '\\')) {
                Emit-One $child ($depth + 1)
            }
        }
        [void]$lines.Add("$indent</Directory>")
    }
    foreach ($top in $sorted) {
        if ($top -notmatch '\\') {
            Emit-One $top 0
        }
    }
    return $lines
}

# ------------------------------------------------------------------ emit ----
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<!-- GENERATED by gen-payload.ps1 - do not edit by hand. -->')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')

foreach ($mountId in @($RootMount, $EmulatorMount, $ModulesMount)) {
    if (-not $dirsByMount.ContainsKey($mountId)) { continue }
    [void]$sb.AppendLine("    <DirectoryRef Id=`"$mountId`">")
    $dirLines = Emit-DirectoryTree $dirsByMount[$mountId] $mountId
    foreach ($l in $dirLines) { [void]$sb.AppendLine($l) }
    [void]$sb.AppendLine('    </DirectoryRef>')
}

[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('  <Fragment>')

function Emit-Components([string]$mountId) {
    if (-not $filesByMount.ContainsKey($mountId)) { return }
    foreach ($f in $filesByMount[$mountId]) {
        $dirRel = Split-Path -Parent $f.Rel
        if ($dirRel) {
            $dirId = 'Dir.' + (Sanitize-Id "$mountId.$dirRel")
        } else {
            $dirId = $mountId
        }
        $leaf = Split-Path -Leaf $f.Rel
        $stem = if ($dirRel) { "$($f.Group)\$dirRel\$leaf" } else { "$($f.Group)\$leaf" }
        $compId = 'Cmp.' + (Sanitize-Id $stem)
        $fileId = 'Fil.' + (Sanitize-Id $stem)
        $compGuid = New-DeterministicGuid "component:$stem"
        $src = Escape-Xml $f.Src
        [void]$sb.AppendLine("      <Component Id=`"$compId`" Directory=`"$dirId`" Guid=`"$compGuid`">")
        [void]$sb.AppendLine("        <File Id=`"$fileId`" KeyPath=`"yes`" Source=`"$src`" />")
        [void]$sb.AppendLine('      </Component>')
    }
}

[void]$sb.AppendLine('    <ComponentGroup Id="Payload.Root">')
Emit-Components $RootMount
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('    <ComponentGroup Id="Payload.Emulator">')
Emit-Components $EmulatorMount
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('    <ComponentGroup Id="Payload.Addin">')
Emit-Components $ModulesMount
[void]$sb.AppendLine('    </ComponentGroup>')

[void]$sb.AppendLine('    <ComponentGroup Id="Payload.All">')
[void]$sb.AppendLine('      <ComponentGroupRef Id="Payload.Root" />')
[void]$sb.AppendLine('      <ComponentGroupRef Id="Payload.Emulator" />')
[void]$sb.AppendLine('      <ComponentGroupRef Id="Payload.Addin" />')
[void]$sb.AppendLine('    </ComponentGroup>')

[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

$outDir = Split-Path -Parent $OutputPath
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
$sb.ToString() | Out-File -FilePath $OutputPath -Encoding utf8
$rootCount = if ($filesByMount.ContainsKey($RootMount)) { $filesByMount[$RootMount].Count } else { 0 }
$emulatorCount = if ($filesByMount.ContainsKey($EmulatorMount)) { $filesByMount[$EmulatorMount].Count } else { 0 }
$addinCount = if ($filesByMount.ContainsKey($ModulesMount)) { $filesByMount[$ModulesMount].Count } else { 0 }
Write-Host ("payload.wxs written: {0} files (root={1}, emulator={2}, addin={3}, deduped={4}, keep-first={5})" -f `
    ($rootCount + $emulatorCount + $addinCount), $rootCount, $emulatorCount, $addinCount, $script:deduped, $script:keepFirst)




