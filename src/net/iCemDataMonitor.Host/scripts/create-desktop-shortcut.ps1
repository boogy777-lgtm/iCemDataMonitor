# =====================================================================
# create-desktop-shortcut.ps1 - MSI post-install option (OptionsDlg).
#
# Creates a desktop shortcut to the iCem Data Monitor panel
# (iCemDataMonitor.WinUI.exe). Installed to iCemMonitor\scripts\ and
# invoked by the MSI only when the "Создать ярлык на рабочем столе"
# checkbox is checked. Intentionally trivial (one COM call) — UAC keeps
# the moment predictable. Safe to re-run (idempotent).
# =====================================================================

$ws = New-Object -ComObject WScript.Shell
$lnkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'iCem Data Monitor.lnk'
$target = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\iCemDataMonitor.WinUI.exe'))
if (-not (Test-Path -LiteralPath $target)) { throw "panel exe not found: $target" }

$s = $ws.CreateShortcut($lnkPath)
$s.TargetPath = $target
$s.WorkingDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$s.Save()

Write-Host "[create-desktop-shortcut] created: $lnkPath -> $target"
exit 0
