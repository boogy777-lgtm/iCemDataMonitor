<#
.SYNOPSIS
  Full production build of the iCemDataMonitor MSI.

.DESCRIPTION
  Pipeline: publish Host (SELF-CONTAINED single-file since the 2026-08-31
  owner decree — no .NET runtime needed on the target, no Burn bootstrapper
  anywhere in the pipeline; the cli rides INSIDE the host exe as argv mode
  since the «пункт 3» decree) + Shell/Emulator -> stage, build+stage the
  iCem AddIn, prune junk (languages -> en/ru), ensure WiX extensions,
  regenerate the branded wizard bitmaps, harvest payload.wxs, wix build
  the MSI — the single deliverable (no zip since the 2026-08-31 decree).
  Since 2026-08-28 no ini is shipped — the host creates/migrates
  config.ini itself (see step 5 comment).
  Since 2026-09-02 (owner decree «подчищать за собой»): after the MSI is
  CONFIRMED on disk, the build erases its own traces — installer\stage and
  every bin\ / obj\ folder under the repo — so the repo stays
  sources-only. Only the MSI in dist\legacy\ survives (see step 12).
  Since 2026-09-02 (owner decree «скрипт должен быть самодостаточным»):
  MsiVersion is OPTIONAL — without it the version comes from the VERSION
  file at the repo root (single source of truth; a bump = edit that file).
  The script never prompts interactively.
  Since 2026-09-02 (owner request «создание лога + закрыть после нажатия
  клавиши»): the whole run is transcribed into dist\legacy\<msi>.build.log (see
  step 0.7) and the window waits for a keypress at the end — warnings
  stay readable after the console closes; -NoPause opts out.

.PARAMETER MsiVersion
  Product version, e.g. 1.4.6 (must be x.y.z for MSI). OPTIONAL since
  2026-09-02 — when omitted, the version is read from the VERSION file at
  the repo root. The parameter stays as an explicit override for the
  release flow (make-release.ps1 passes the release version through).

.PARAMETER Configuration
  dotnet configuration used for all projects. Default: Release.

.PARAMETER OutputDir
  Folder for the FINAL artifact (MSI). Default:
  <repo>\dist\legacy — a dedicated delivery folder at the repo root, separate
  from sources and from the intermediate installer\stage. Relative
  values are resolved against the repo root. The reserved installer\stage
  directory cannot be used for delivery. Delivery folders are excluded from cleanup.

.PARAMETER KeepBuildArtifacts
  Skip the post-build cleanup (step 12): the stage folder and all
  bin\ / obj\ folders stay on disk for debugging. Default: cleanup runs —
  the repo is left sources-only, the MSI in dist\legacy\ is the only binary.

.PARAMETER AllowMissingAddinForCi
  GitHub Actions smoke builds only: permit missing vendor SDK/AddIn. The
  resulting incomplete MSI must never be released. Production builds fail closed.

.PARAMETER NoPause
  Skip the end-of-run keypress wait (success AND failure path). The
  default pause exists so a double-clicked console stays open long enough
  to read warnings; automated callers (make-release.ps1, the VS Code task)
  pass -NoPause.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1
  (no arguments — version comes from the VERSION file at the repo root)
  powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1 -MsiVersion 1.4.6

.NOTES
  PowerShell 5.1 compatible (no &&, no ??, no ternary). Run from the repo
  root or anywhere — paths are resolved from $PSScriptRoot. No absolute
  paths are hardcoded: the repo root folder may be renamed/moved freely.
#>
[CmdletBinding()]
param(
    [string]$MsiVersion = '',
    [string]$Configuration = 'Release',
    [string]$OutputDir = '',
    [switch]$KeepBuildArtifacts,
    [switch]$NoPause,
    # Explicit CI smoke escape hatch; never permitted for a production build.
    [switch]$AllowMissingAddinForCi
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$InstallerDir = $PSScriptRoot
$Repo = Split-Path -Parent $InstallerDir
$Stage = Join-Path $InstallerDir 'stage'
Import-Module (Join-Path $InstallerDir 'BuildPaths.psm1') -Force
# The script-level trap is hoisted: preflight exceptions occur before transcription.
# Keep its log state defined so an early invalid path can propagate unchanged.
$BuildLog = $null
if ($AllowMissingAddinForCi -and $env:GITHUB_ACTIONS -ne 'true') {
    throw '-AllowMissingAddinForCi is restricted to GitHub Actions smoke builds.'
}
if ($OutputDir) {
    $OutDir = $OutputDir
    if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $Repo $OutDir }
} else {
    # 2026-08-30 (owner decree «финальная сборка — в отдельной папке»):
    # the MSI/EXE land in <repo>\dist\legacy, NOT in installer\out — delivery
    # artifacts live apart from sources and from the transient stage.
    $OutDir = Join-Path $Repo 'dist/legacy'
}
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (Test-BuildPathWithin $OutDir $Stage) {
    throw 'OutputDir cannot be inside reserved installer/stage staging directory.'
}
# -------------------------------------------- 0.6. version resolution -------
# 2026-09-02 (owner decree «скрипт должен быть самодостаточным»): no
# interactive prompt, ever. MsiVersion is optional — without it the version
# comes from the VERSION file at the repo root (single source of truth; a
# bump = edit that file). The parameter remains an explicit override for
# the release flow (make-release.ps1 passes the release version through).
if (-not $MsiVersion) {
    $VersionFile = Join-Path $Repo 'VERSION'
    if (-not (Test-Path -LiteralPath $VersionFile)) {
        throw "MsiVersion not given and VERSION file missing: $VersionFile — create it with x.y.z content (e.g. '1.4.6')"
    }
    $MsiVersion = (Get-Content -LiteralPath $VersionFile -Raw).Trim()
    Write-Host "== MSI version from VERSION file: $MsiVersion"
}
if ($MsiVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "MsiVersion '$MsiVersion' must match x.y.z (e.g. 1.4.6)"
}
$MsiName = "iCemDataMonitor-$MsiVersion-x64.msi"
$MsiPath = Join-Path $OutDir $MsiName

# -------------------------------------- 0.7. build log + failure trap -------
# 2026-09-02 (owner request «создание лога + закрыть после нажатия клавиши —
# я видел как появились warning во время сборки»): the WHOLE run is
# transcribed into dist\legacy\<msi>.build.log (next to the MSI) so MSBuild/WiX
# warnings stay reviewable after the window closes; a rebuild of the same
# version overwrites its own log. The trap below mirrors the success
# close-out (step 14) on ANY failure: summary + log flush + keypress wait,
# so a failed double-clicked build does not vanish either. PS 5.1 note:
# trap is dynamically scoped — it catches throws from the helper functions
# (Invoke-Checked etc.) too.
$BuildLog = Join-Path $OutDir ($MsiName -replace '\.msi$', '.build.log')
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Start-Transcript -Path $BuildLog -Force | Out-Null

trap {
    # Preflight errors have no log/transcript; let the caller catch the original error.
    if (-not $BuildLog) { break }
    Write-Host ""
    Write-Host ("BUILD FAILED: {0}" -f $_.Exception.Message) -ForegroundColor Red
    Write-Host ("Build log: $BuildLog")
    try { Stop-Transcript -ErrorAction SilentlyContinue } catch { }
    if (-not $NoPause) {
        Write-Host 'Press any key to close...'
        try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { }
    }
    exit 1
}

function Invoke-Checked([scriptblock]$cmd, [string]$what) {
    Write-Host "== $what"
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "FAILED: $what (exit $LASTEXITCODE)" }
}

# MSB4166 ("child node exited prematurely") is a TRANSIENT MSBuild-node crash
# seen twice on this stand under load (low disk/memory) — the SAME command
# passes on retry. Wrap long-running publish steps with a bounded retry so a
# node crash never kills the whole pipeline mid-way (fail-fast stays for
# deterministic errors: only MSB4166-class is retried, everything else throws).
function Invoke-CheckedWithRetry([scriptblock]$cmd, [string]$what, [int]$maxAttempts = 3) {
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        Write-Host "== $what (attempt $attempt/$maxAttempts)"
        $outFile = Join-Path $env:TEMP ("msi-publish-{0}-{1}.log" -f $PID, [Guid]::NewGuid().ToString('N'))
        try {
            & $cmd 2>&1 | Tee-Object -FilePath $outFile | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) { Write-Host "   $_" } else { Write-Host "   $_" }
            }
            $code = $LASTEXITCODE
        } catch {
            $code = 1
        }
        if ($code -eq 0) { Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue; return }
        $logText = if (Test-Path -LiteralPath $outFile) { (Get-Content -LiteralPath $outFile -Raw) } else { '' }
        $transient = ($logText -match 'MSB4166' -or $logText -match 'exited prematurely')
        if (-not $transient -or $attempt -eq $maxAttempts) {
            Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
            throw "FAILED: $what (exit $code)"
        }
        Write-Host "   transient MSBuild-node crash (MSB4166) — retrying in 3 s"
        Start-Sleep -Seconds 3
    }
}

# ------------------------------------------------------------- 1. staging ---
Write-Host "== wipe staging: $Stage"
if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
# 2026-08-31 (owner decree «пункт 3»): the cli stage folder is RETIRED — the
# cli merged INTO the host exe (argv mode: iCemDataMonitor.Host.exe
# snapshot|get|header, CliMode\CliForwarder). One exe = one runtime copy,
# −51.9 MB of duplicate self-contained runtime.
foreach ($d in @('host', 'shell', 'emulator', 'addin')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Stage $d) | Out-Null
}

# --------------------------------------------------- 1.5. clean rebuild ------
# 2026-08-27 fix: incremental publishes can carry STALE outputs into the stage
# (MSBuild sees the obj cache as up-to-date while sources changed) - a mixed
# payload crashed the installed shell (old versioned dlls + new xbf/pri).
# Force a clean rebuild of the published projects so the stage is
# always a coherent snapshot of the CURRENT sources.
foreach ($p in @(@('iCemDataMonitor.Host', $false),
                 @('iCemDataMonitor.Shell.WinUI', $true),
                 @('iCemEmulator', $true))) {
    $proj = $p[0]; $x64 = $p[1]
    Write-Host "== clean $proj (Configuration=$Configuration)"
    if ($x64) {
        dotnet clean (Join-Path $Repo $proj) -c $Configuration -p:Platform=x64 -v minimal
    } else {
        dotnet clean (Join-Path $Repo $proj) -c $Configuration -v minimal
    }
    if ($LASTEXITCODE -ne 0) { throw "clean failed: $proj (exit $LASTEXITCODE)" }
}

# ----------------------------------------------------- 2. publish host ------
# 2026-08-31 (owner decree «делаем self-contained!»): SUPERSEDED 2026-09-02.
# 2026-09-02 (owner decree «убераем self contained — дешевле передавать
# core\runtime, важнее меньший размер итоговых файлов, 3 файла вместо 1 god
# installer»): the host publishes FRAMEWORK-DEPENDENT again — no runtime is
# bundled, no single-file, NO TRIMMING (PublishTrimmed is self-contained-only;
# it dies together with TrimMode/JsonSerializerIsReflectionEnabledByDefault/
# SuppressTrimAnalysisWarnings and the whole 2026-09-01/02 trim-warning
# hygiene). The target machine gets the runtimes from the two installers the
# owner ships NEXT TO the MSI (installer\aspnetcore-runtime-*-win-x64.exe
# covers Host's FrameworkReference AspNetCore.App + NETCore.App;
# installer\windowsdesktop-runtime-*-win-x64.exe covers the WinUI apps' 
# NETCore.App). Field evidence that forced this: a clean PC demanded
# «.NET Runtime + ASP.NET Core Runtime» — and shell/emulator were
# framework-dependent all along, so the runtime handoff exists regardless;
# bundling it only into the host was the worst of both worlds.
# -r win-x64 stays: framework-dependent still needs the apphost exe and
# RID-specific assets (the System.IO.Ports dedupe law).
# 2026-09-02 (dedupe determinism): -p:Platform=x64 aligns the host chain with
# the shell/emulator chains — without it the host compiled OUR dlls as AnyCPU
# (bin\Release) while the WinUI chains compiled x64 (bin\x64\Release), and the
# payload dedupe reported 7 keep-first mismatches on iCemDataMonitor.*.dll
# (same sources, different MVID/platform flags). One platform = one binary.
Invoke-Checked {
    dotnet publish (Join-Path $Repo 'iCemDataMonitor.Host') `
        -c $Configuration -r win-x64 -p:Platform=x64 `
        "-p:SatelliteResourceLanguages=en%3Bru" `
        -o (Join-Path $Stage 'host')
} "publish Host ($Configuration, framework-dependent win-x64, Platform=x64)"

# ----------------------------------------------------- 4. publish shell -----
# 2026-08-31: moved to the MSB4166-retry wrapper too — the transient node
# crash hit the SHELL publish on the 1.4.1 build (it is no longer exclusive
# to the emulator's graph; both WinUI publishes are equally large).
Invoke-CheckedWithRetry {
    dotnet publish (Join-Path $Repo 'iCemDataMonitor.Shell.WinUI') `
        -c $Configuration -p:Platform=x64 "-p:SatelliteLanguages=en%3Bru" `
        -o (Join-Path $Stage 'shell')
} "publish Shell.WinUI ($Configuration, Platform=x64, SatelliteLanguages=en;ru)"

# ---------------------------------------------- 4.5. publish emulator --------
# 2026-08-27 (owner decree «эмулятор — отдельный независимый процесс, своя
# папка»): iCemEmulator is a STANDALONE WinUI+CLI app with the engine
# IN-PROCESS — it is no longer an emule\ subfolder of the host. It publishes
# into its OWN folder iCemMonitor\iCemEmulator\ (EMULATORDIR in Product.wxs).
# MSB4166-retry wrapper: the WinUI publish chain is the largest MSBuild graph
# in this pipeline and twice hit a transient node crash on this stand.
# 2026-09-02 (warning hygiene): -r win-x64 added. Without it the emulator
# restore resolved RID-LESS NuGet assets while the shell (csproj RID) resolved
# runtimes\win assets — the payload dedupe then warned «KEEP-FIRST:
# System.IO.Ports.dll differs across stages» (lib\net10.0 vs runtimes\win\
# lib\net10.0). Same spirit as the 2026-09-01 WebView2 pin («Блок 2 п.5»):
# both stages MUST carry the identical DLL. The RID stays OFF the csproj so
# the dev-loop output pin (bin\x64\<Config>\net10.0-windows10.0.19041.0\
# iCemEmulator.exe, no \win-x64 folder) is untouched; publish uses -o anyway.
Invoke-CheckedWithRetry {
    dotnet publish (Join-Path $Repo 'iCemEmulator\iCemEmulator.csproj') `
        -c $Configuration -p:Platform=x64 -r win-x64 "-p:SatelliteLanguages=en%3Bru" `
        -o (Join-Path $Stage 'emulator')
} "publish iCemEmulator ($Configuration, Platform=x64, RID=win-x64, SatelliteLanguages=en;ru)"
if (-not (Test-Path -LiteralPath (Join-Path $Stage 'emulator\iCemEmulator.exe'))) {
    throw 'iCemEmulator.exe missing from the publish output — emulator stage incomplete'
}
# Satellite hygiene (owner request 2026-08-28 «языки оставь русский/английский»):
# SatelliteLanguages=en;ru trims XAML locale resources at publish time, but the
# self-contained WindowsAppSDK runtime still lays down ~70+ language resource
# folders (af-ZA, am-ET, as-IN...). Prune EVERY stage to en*/ru* culture
# folders; neutral (non-culture) files stay untouched. (2026-08-28 fix: the
# old emulator-only prune used -notmatch '^en' and silently deleted ru-RU.)
function Remove-ForeignLanguageFolders([string]$folder) {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return }
    $pruned = Get-ChildItem -LiteralPath $folder -Recurse -Directory | Where-Object {
        $_.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]{2,8}){1,2}$' -and
        $_.Name -notmatch '^(en|ru)(-|$)'
    }
    foreach ($d in $pruned) {
        $bytes = (Get-ChildItem -LiteralPath $d.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum
        Remove-Item -LiteralPath $d.FullName -Recurse -Force
        Write-Host ("   language prune: {0} ({1:N0} bytes)" -f $d.FullName.Substring($folder.Length + 1), $bytes)
    }
}
Remove-ForeignLanguageFolders (Join-Path $Stage 'shell')
Remove-ForeignLanguageFolders (Join-Path $Stage 'emulator')
Remove-ForeignLanguageFolders (Join-Path $Stage 'host')
Write-Host '== emulator layout OK (own folder iCemEmulator\)'

# ------------------------------------------- 4.6. WinUI diet (2026-08-31) ---
# Owner decree «Делаем 1+2»: shell + emulator merge FLAT into the monitor
# root at install time (their payloads are 94% byte-identical — one WinUI
# runtime set instead of two). Two enablers live here:
#
# (a) REGISTRY RENAME: at the flat root only ONE templates\variable_registry.json
#     can exist and it must be the HOST's full production registry (3507 vars).
#     The emulator's tuned sim registry (140 vars, 25 simulation.base values
#     differ) is renamed to variable_registry.emulator.json — the engine's
#     SimFileResolver (iCemEmule.Core, emulator-override law 2026-08-31)
#     prefers the override when present. Zero behavior drift for both apps.
$emuReg = Join-Path $Stage 'emulator\templates\variable_registry.json'
if (Test-Path -LiteralPath $emuReg) {
    Move-Item -LiteralPath $emuReg -Destination (Join-Path $Stage 'emulator\templates\variable_registry.emulator.json') -Force
    Write-Host '== emulator registry renamed -> variable_registry.emulator.json (override law)'
}
#
# (b) PRUNE: files the WindowsAppSDK self-contained runtime lays down but our
#     apps never touch — PROVEN 2026-08-31 on the merged-folder smoke:
#     * onnxruntime.dll / DirectML.dll / Microsoft.Windows.AI.MachineLearning.dll
#       (the Windows AI/ML stack, ~40 MB — lazily loaded by WinAppSDK, only
#       for Windows AI APIs we never call; emulator window + shell splash
#       rendered fine without them);
#     * Microsoft.Windows.Widgets.dll (Windows Widgets board, unused);
#     * *.xbf loose compiled-XAML leftovers — WinUI 3 loads XAML from the
#       embedded .pri; the merged smoke ran every window xbf-free.
$winUiPrune = @('onnxruntime.dll', 'DirectML.dll', 'Microsoft.Windows.AI.MachineLearning.dll', 'Microsoft.Windows.Widgets.dll')
foreach ($f in @('shell', 'emulator')) {
    $dir = Join-Path $Stage $f
    foreach ($n in $winUiPrune) {
        $p = Join-Path $dir $n
        if (Test-Path -LiteralPath $p) {
            $sz = (Get-Item -LiteralPath $p).Length
            Remove-Item -LiteralPath $p -Force
            Write-Host ("   winui prune: {0}\{1} ({2:N0} bytes)" -f $f, $n, $sz)
        }
    }
    Get-ChildItem -LiteralPath $dir -Filter '*.xbf' -File | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        Write-Host ("   winui prune: {0}\{1} ({2:N0} bytes)" -f $f, $_.Name, $_.Length)
    }
}

# ---------------------------------------------------- 5. config.ini ---------
# 2026-08-28 (owner decree «миграция программы на ProgramData +
# config.ini рядом с бинарником; web включён по дефолту»): the MSI NO LONGER
# SHIPS any ini file. The HOST owns config creation/migration at first run
# (IniConfig 2026-08-28 law): canonical
#   C:\ProgramData\Halliburton\iCem\DataMonitoring\config.ini
# (next to Data.dat and — after the ProgramData migration — next to the exe);
# legacy station.ini (canonical folder / next-to-exe) is loaded and
# best-effort copied to config.ini, NEVER fatal; missing config => the host
# creates the default with web_on=1. Shipping an admin-owned ini would make
# it non-writable for the running user (ProgramData ACL) — creation at
# runtime by the running user is the writable-by-design path. The old
# staging + normalization steps are retired with the shipped-ini era.

# ---------------------------------------------------------- 6. AddIn --------
# 2026-09-02: the AddIn (Burservis.iCem.DataMonitor, net48) links against the
# proprietary Halliburton iCem SDK via ABSOLUTE HintPaths
# (C:\Program Files\Halliburton\iCem\Bin\*.dll) - a machine-local vendor
# install that can never be committed to the sources-only repo. On hosts
# WITHOUT the iCem SDK (CI runners) the AddIn build fails closed. Only the
# explicit GitHub Actions smoke option permits an incomplete package; such a fixture is not a production handoff.
$iCemBin = 'C:\Program Files\Halliburton\iCem\Bin'
if (Test-Path -LiteralPath $iCemBin -PathType Container) {
    Invoke-Checked {
        dotnet build (Join-Path $Repo 'Burservis.iCem.DataMonitor\Burservis.iCem.DataMonitor.csproj') `
            -c $Configuration
    } "build Burservis.iCem.DataMonitor ($Configuration)"
    $addinDll = Join-Path $Repo "Burservis.iCem.DataMonitor\bin\$Configuration\Burservis.iCem.DataMonitor.dll"
    if (-not (Test-Path -LiteralPath $addinDll)) { throw "AddIn dll not found: $addinDll" }
    Copy-Item -LiteralPath $addinDll -Destination (Join-Path $Stage 'addin\') -Force
    Write-Host '== AddIn staged'
} else {
    if (-not $AllowMissingAddinForCi) {
        throw "vendor iCem SDK not found ($iCemBin); production MSI requires the real AddIn."
    }
    Write-Warning "CI SMOKE ONLY: vendor SDK missing; this MSI is incomplete and must never be released."
}

# ------------------------------------------------------------- 7. prune -----
# PS 5.1 BUG: -Include is silently IGNORED with -LiteralPath (deletes
# EVERYTHING) - use an explicit Where-Object filter instead.
Write-Host '== pruning stage'
Get-ChildItem -LiteralPath $Stage -Recurse -File | Where-Object {
    $_.Extension -in @('.pdb', '.zip') -or
    $_.Name -in @('crash_log.txt', 'run.bat') -or
    $_.Name -like 'iCemDataMonitor.Tray*'
} | Remove-Item -Force
Get-ChildItem -LiteralPath $Stage -Recurse -Directory |
    Where-Object { $_.Name -in @('_archive', 'testhost') } |
    Remove-Item -Recurse -Force
# .xml / .ico / config are KEPT on purpose (owner decision).

# ------------------------------------------------- 8. runtime cache ---------
# RETIRED 2026-08-31 (self-contained decree): no runtime installers are
# downloaded or embedded anymore — the host/cli publish carries the runtime.
# The cache folder lives in installer\_archive\burn\runtime-cache for
# archaeology only.
# 2026-09-02 amendment («убераем self contained»): runtime installers are BACK
# in installer\ — but owner-DROPPED (never downloaded by the build) and NEVER
# embedded: step 11.5 copies them next to the MSI as the 3-file handoff.

# ------------------------------------------------- 9. wix extensions --------
Write-Host '== ensuring wix extensions'
$extList = (& wix extension list -g | Out-String)
if ($extList -notmatch 'WixToolset\.Util\.wixext.*5\.0\.2') {
    wix extension add -g WixToolset.Util.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'failed to add WixToolset.Util.wixext' }
}
if ($extList -notmatch 'WixToolset\.UI\.wixext.*5\.0\.2') {
    wix extension add -g WixToolset.UI.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'failed to add WixToolset.UI.wixext' }
}

# ------------------------------------------------- 9.5. UI bitmaps ----------
# 2026-08-31 (owner: «логотип надо перенести» в большой MSI-визард):
# regenerate the branded wizard bitmaps from the source PNG before the
# payload harvest, so WixUIBannerBmp/WixUIDialogBmp always match the logo.
Write-Host '== regenerating branded UI bitmaps'
& (Join-Path $InstallerDir 'gen-ui-bmps.ps1')
if ($LASTEXITCODE -ne 0) { throw 'gen-ui-bmps.ps1 failed' }

# ------------------------------------------------- 10. payload harvest ------
# 2026-08-31 (decree «1+2»): the emulator stage merges FLAT into MONITORROOT
# (keep-first priority host > shell > emulator; conflicts verified safe —
# same-version System.IO.Ports, shell-wins WebView2 which the emulator never
# loads, xbf/AI pruned in step 4.6). EMULATORDIR is gone from Product.wxs.
# 2026-09-02 (TFM-asset law): priority FLIPPED to shell > host > emulator.
# The host chain targets PLAIN net10.0 and resolves plain NuGet assets
# (ReactiveUI lib\net8.0, System.Reactive lib\net6.0); the WinUI chains target
# net10.0-windows10.0.19041 and resolve the WINDOWS-specific assets of the
# SAME packages (lib\net*-windows10.0.19041) — different binaries by design.
# The payload is Windows-x64-only, and the shell/emulator are the apps that
# actually exercise ReactiveUI, so the windows assets must win; the host
# loads them fine (platform-moniker assets bind on Windows). Field evidence:
# 11 keep-first mismatches on the first framework-dependent build.
$PayloadWxs = Join-Path $InstallerDir 'payload.wxs'
Write-Host "== harvesting payload.wxs ($PayloadWxs)"
& (Join-Path $InstallerDir 'gen-payload.ps1') -StageRoot $Stage -OutputPath $PayloadWxs `
    -RootFolders @('shell', 'host', 'emulator') -EmulatorFolder '' `
    -ExpectedAssetMismatch @('ReactiveUI.dll', 'System.Reactive.dll')
if ($LASTEXITCODE -ne 0) { throw 'gen-payload.ps1 failed' }

# ----------------------------------------------------- 11. wix build -------
Write-Host "== wix build -> $MsiPath"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Invoke-Checked {
    wix build -arch x64 `
        -dcl high `
        -ext WixToolset.Util.wixext/5.0.2 `
        -ext WixToolset.UI.wixext/5.0.2 `
        -define "MsiVersion=$MsiVersion" `
        -define "AssetsDir=$(Join-Path $InstallerDir 'assets')" `
        -out $MsiPath `
        (Join-Path $InstallerDir 'Product.wxs') `
        $PayloadWxs
} 'wix build'
# 2026-09-01 (build audit): -dcl high replaces the mszip default — the payload
# is ~160 MB of self-contained runtime, high (LZX) compresses it visibly
# better; build time grows, MSI size shrinks. Language audit (same day): the
# stage carries ONLY en/ru (.mui 12 files/240 KB; zero .NET satellite
# assemblies; resx-free sources), see Remove-ForeignLanguageFolders above.
# 2026-09-02 amendment: payload is framework-dependent now (~35 MB) — -dcl high
# stays (still the better ratio, the WinAppSDK runtimes dominate the size).

# ------------------------------------- 11.5. Welcome/Exit page patch --------
# RETIRED 2026-09-01 (owner decree «все равно костыли — не используй SQL»):
# no more post-build Control-table surgery. The wizard ships 100% STOCK
# WixUI geometry; branding lives entirely inside ui-dialog.bmp, which is
# painted AROUND the stock text zones (left art panel, right side kept
# white — see gen-ui-bmps.ps1). patch-welcome-page.ps1 is archived in
# installer\_archive\.

# 2026-09-02 (owner decree «после подтверждения, что файл msi создался»):
# explicit CONFIRMATION gate — the MSI must exist AND be non-empty before
# anything is cleaned; a zero-byte MSI (broken wix run) aborts the build
# with the stage intact.
$final = Get-Item -LiteralPath $MsiPath
if ($final.Length -le 0) { throw "MSI is EMPTY (0 bytes): $MsiPath" }
Write-Host ""
Write-Host "BUILD OK: $($final.FullName) ($($final.Length) bytes)"

# ----------------------------------------- 11.5. runtime handoff bundle ------
# 2026-09-02 (owner decree «убераем self contained — я буду передавать 3 файла
# для установки»): the install handoff = MSI + the TWO runtime installers the
# owner drops into installer\. They are NOT harvested into the MSI (gen-payload
# reads stage\ only) — copy them NEXT TO the MSI so dist\legacy\ is the complete
# 3-file handoff. Wildcards: runtime patch versions bump without script edits.
# Missing runtime = WARNING (the MSI itself is valid), not a build failure.
Write-Host '== runtime handoff bundle (output folder = MSI + 2 runtime installers)'
$runtimeKinds = @('aspnetcore-runtime-*-win-x64.exe', 'windowsdesktop-runtime-*-win-x64.exe')
foreach ($kind in $runtimeKinds) {
    $found = @(Get-ChildItem -LiteralPath $InstallerDir -Filter $kind -File -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) {
        Write-Warning "runtime installer missing in installer\: $kind — handoff bundle incomplete"
        continue
    }
    foreach ($rt in $found) {
        Copy-Item -LiteralPath $rt.FullName -Destination $OutDir -Force
        Write-Host "   shipped: $($rt.Name) ($($rt.Length) bytes)"
    }
}

# --------------------------------------------- 12. post-build cleanup --------
# 2026-09-02 (owner decree «нужно подчищать за собой + сборки release\debug
# от dotnet — в проекте должны оставаться исходники без бинарников»):
# now that the MSI is CONFIRMED, the build erases its own traces so the
# repo stays sources-only:
#   * installer\stage — the transient publish output (steps 2/4/4.5),
#     ~200 MB of self-contained runtimes (2026-09-02: ~35 MB framework-
#     dependent now), recreated on every run;
#   * every bin\ and obj\ folder under the repo — dotnet publish/build
#     (Release/Debug) artifacts of the WHOLE transitive graph (Host, Shell,
#     Emulator, AddIn, Core, Data, Transport, Plugins, Sdk, test suites).
# The MSI in dist\legacy\ is the ONLY binary that survives (it is the deliverable).
# A FAILED build throws BEFORE this step, so the stage is always available
# for inspection; -KeepBuildArtifacts opts out for debugging. Removal is
# best-effort per folder: a locked file is reported as a warning, not a
# build failure (the MSI is already done and valid at this point).
if (-not $KeepBuildArtifacts) {
    Write-Host '== post-build cleanup (delivery output is preserved)'
    $dirs = @()
    if (Test-Path -LiteralPath $Stage) { $dirs += Get-Item -LiteralPath $Stage }
    $dirs += Get-ChildItem -LiteralPath $Repo -Recurse -Directory |
        Where-Object { $_.Name -in @('bin', 'obj') }
    # Deepest-first: a parent (stage) never shadows an enumerated child.
    # Explicit delivery overrides may be inside bin/obj or contain such folders.
    # Keep the complete delivery tree and every ancestor that contains it.
    $dirs = @($dirs | Where-Object { -not (Test-BuildPathOverlap $_.FullName $OutDir) } |
        Sort-Object { $_.FullName.Length } -Descending)
    $freed = 0; $left = 0
    foreach ($d in $dirs) {
        # 2026-09-02 fix: an EMPTY bin\obj folder makes Measure-Object emit
        # NOTHING ($null), and reading .Sum on $null under StrictMode 2.0
        # threw PropertyNotFoundException → the trap failed the WHOLE build
        # even though the MSI was already confirmed. Measure null-safely.
        $measure = Get-ChildItem -LiteralPath $d.FullName -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum
        $bytes = 0
        if ($null -ne $measure -and $null -ne $measure.Sum) { $bytes = $measure.Sum }
        Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $d.FullName) {
            $left++
            Write-Warning "locked — left on disk: $($d.FullName)"
        } else {
            $freed += $bytes
        }
    }
    Write-Host ("   removed {0} folder(s), freed {1:N0} bytes" -f ($dirs.Count - $left), $freed)
    if ($left -gt 0) { Write-Host ("   {0} folder(s) locked (file in use)" -f $left) }
    Write-Host "   delivery output preserved: $OutDir"
}

# ---------------------------------------------- 13. distribution zip ---------
# RETIRED 2026-08-31 (owner decree «не нужен zip в сборке»): the MSI is the
# single deliverable in dist\legacy\. The plain zip existed to dodge the browser's
# Mark-of-the-Web on the old Burn exe — with Burn gone the MSI handoff does
# not need it.

# -------------------------------- 14. close-out (log flush + keypress) -------
# 2026-09-02 (owner request «закрыть после нажатия клавиши»): the success
# path mirrors the trap above — the transcript is flushed first, then the
# window waits for a keypress so warnings can be read; -NoPause skips the
# wait (automated callers: make-release.ps1, the VS Code task pass it).
Stop-Transcript | Out-Null
Write-Host ""
Write-Host "Build log: $BuildLog"
if (-not $NoPause) {
    Write-Host 'Press any key to close...'
    try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { }
}
