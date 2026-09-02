# =====================================================================
# add-autostart.ps1 - MSI post-install option (OptionsDlg).
#
# Registers iCemDataMonitor.Host in the CURRENT USER's Run key (HKCU,
# no admin rights needed). Same convention as tools\install-autostart.ps1
# (E.2.4): value name "iCemDataMonitorHost", value "<host.exe>" --autostart
# (the flag hides the console window at logon). Installed to
# iCemMonitor\scripts\ and invoked by the MSI only when the "Добавить
# в автозагрузку" checkbox is checked. Idempotent.
# =====================================================================

$hostExe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\iCemDataMonitor.Host.exe'))
if (-not (Test-Path -LiteralPath $hostExe)) { throw "host exe not found: $hostExe" }

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'iCemDataMonitorHost'
$valueData = '"' + $hostExe + '" --autostart'

New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name $valueName -Value $valueData

Write-Host "[add-autostart] Run-key: $valueName = $valueData"
exit 0
