iCem Data Monitor - Rust development candidate, Windows x64

This MSI contains the Rust Host, Shell, independent emulator and the original
.NET Framework 4.8 Burservis.iCem.DataMonitor AddIn DLL. It is not yet a proven
production replacement. Keep the installed EXEs/assets/templates together.

Runtime: %ProgramData%\Halliburton\iCem\DataMonitoring
AddIn: selected iCem folder\Bin\Modules\Burservis.iCem.DataMonitor.dll
The Burservis wizard branding comes from the original legacy installer assets.
The candidate does not automatically replace a registered legacy .NET MSI.

PowerShell 7.4+ launchers (Host config must already exist):
  .\Start-Host.ps1 -StationConfig 'C:\path\to\existing\config.ini'
  .\Start-Shell.ps1 -WebPort 8090
  .\Start-Emulator.ps1

Start Host first. Shell does not yet implement ensure-host. Use the station
config's actual web_port. The original AddIn owns Data.dat in the iCem process.
The emulator remains independent and fresh COM/LAN streams default OFF.
No live config, Data.dat or proprietary SDK DLL is shipped.

The optional desktop shortcut targets Shell. The optional autostart entry targets
Host --autostart, never the emulator. Current Host startup needs existing config.
Upgrade and uninstall preserve runtime-created config/data/logs; uninstall removes
only MSI-owned files, DLL, shortcut and Run value. The candidate is visible in
installed applications. No process-kill/service-stop or recursive wipe scripts run.
Close iCem/monitor applications before installing; in-use file handling uses MSI.
For silent upgrades, pass the same custom paths and chosen options explicitly;
automatic path/preference recovery has not been implemented.

Rust/.NET SDK is not needed to run Rust EXEs. The AddIn uses the .NET Framework
runtime of the original iCem application. Npcap/COM drivers remain external.
No .NET10/WinAppSDK/WebView2 runtime is bundled. Manifest hashes establish file
integrity and source traceability, not Authenticode signing or functional parity.
Hardware, MMF/ACL, Explorer/GPU/DPI/IME, visual and production cutover acceptance
remain separate gates.
