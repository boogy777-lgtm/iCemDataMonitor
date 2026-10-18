iCem Data Monitor - Rust development candidate, Windows x64

This MSI contains the Rust Host, Shell, independent emulator and the original
.NET Framework 4.8 Burservis.iCem.DataMonitor AddIn DLL. It is not yet a proven
production replacement. Keep the installed EXEs/assets/templates together.

Runtime: %ProgramData%\Halliburton\iCem\DataMonitoring
AddIn: selected iCem folder\Bin\Modules\Burservis.iCem.DataMonitor.dll
The Burservis wizard branding comes from the original legacy installer assets.
The candidate does not automatically replace a registered legacy .NET MSI.

Optional PowerShell 7.4+ launchers:
  .\Start-Host.ps1   (optional: -StationConfig 'C:\path\to\config.ini')
  .\Start-Shell.ps1 -WebPort 8090
  .\Start-Emulator.ps1

Open the desktop shortcut or icem-shell-dev.exe. Shell starts an absent Host
once and waits up to 30 seconds. Existing Host is reused. Missing config.ini is
created with standard defaults; legacy station.ini is copied without deletion.
Invalid existing configuration is preserved and reported; correct it before
restarting. Repair restores program files and preserves your settings.
Shell reads the configured web_port unless explicitly overridden. The original AddIn owns Data.dat in the iCem process.
The emulator remains independent and fresh COM/LAN streams default OFF.
No live config, Data.dat or proprietary SDK DLL is shipped.

The optional desktop shortcut targets Shell. The optional autostart entry targets
Host --autostart, never the emulator. Host creates missing config after acquiring its existing singleton.
Upgrade and uninstall preserve runtime-created config/data/logs; uninstall removes
only MSI-owned files, DLL, shortcut and Run value. The candidate is visible in
installed applications. No recursive wipe or process-name kill scripts run.
The installer first requests normal closure of applications/services using its
files. Select the explicit force checkbox only after saving your work in iCem;
unresponsive file owners may then be forcibly closed. This choice applies only to
this operation. Follow any Windows reboot request before considering setup complete.
Rerun the same MSI and choose Repair to restore missing/corrupt files. Saved paths
and options are recovered automatically; explicit command-line choices win.

Rust/.NET SDK is not needed to run Rust EXEs. The AddIn uses the .NET Framework
runtime of the original iCem application. Npcap/COM drivers remain external.
No .NET10/WinAppSDK/WebView2 runtime is bundled. Manifest hashes establish file
integrity and source traceability, not Authenticode signing or functional parity.
Hardware, MMF/ACL, Explorer/GPU/DPI/IME, visual and production cutover acceptance
remain separate gates.

