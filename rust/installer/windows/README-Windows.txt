iCem Data Monitor - Rust development package, Windows x64

Extract the complete ZIP before launching. Keep EXEs, assets and templates together.
PowerShell 7 is required for the launch scripts. Rust/.NET SDK is not needed to run
the Rust EXEs. Npcap and a configured COM/com0com port are external requirements
only when the corresponding physical emulator channel is used.

Launch from PowerShell 7 (paths containing spaces/Unicode are supported):
  .\Start-Host.ps1 -StationConfig 'C:\path\to\existing\config.ini'
  .\Start-Shell.ps1 -WebPort 8090
  .\Start-Emulator.ps1

Start Host first; Shell does not yet implement ensure-host. Use the actual web_port
from the existing config for Start-Shell. Host never creates the producer Data.dat;
the unchanged .NET48 AddIn in the original application owns that bus.
Emulator is independent; its launch script uses a separate per-user development
XML file. COM/LAN streams default OFF on a new config. Host config is mandatory and
is never overwritten by packaging. No live config, Data.dat or AddIn DLL is bundled.

The native runtime EXEs retain their *-dev names. The preview/DFB/LAN trace tools
are not desktop applications and are deliberately excluded. Web SPA is embedded
in Host; it does not require WebView2.

release-manifest.json records source commit, compiler, target, file sizes and SHA256.
The adjacent .zip.sha256 file checks the downloaded archive. These are integrity
checks, not Authenticode signatures. Binaries use a release profile with static CRT.
The artifact version is a package label; Cargo package versions are not rewritten.

This is a reproducible package layout for the current migration, not an MSI or a
production replacement. Hardware timing, MMF/ACL, tray/Explorer, GPU/DPI/IME, visual
parity, autostart installation and MSI upgrade acceptance remain separate gates.
