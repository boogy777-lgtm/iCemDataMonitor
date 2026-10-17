# Windows x64 Rust package

Build on Windows x64 in **PowerShell 7.4+**, with Visual Studio C++ Build Tools
(MSVC x64 and Windows SDK), Git and rustup. No Linux build is required.
The compiler pin/components come from `../rust-toolchain.toml`.

From the repository root:

```powershell
pwsh -NoProfile -File .\rust\tools\Build-WindowsRelease.ps1 -Version 0.1.0-dev
```

From `rust/`:

```powershell
.\tools\Build-WindowsRelease.ps1 -Version 0.1.0-dev
```

Outputs:

- `rust/dist/iCemDataMonitor-rust-dev-0.1.0-dev-windows-x64.zip`
- the adjacent `.zip.sha256`

`-OutputDirectory D:\Releases` selects another output directory. Existing artifacts
are not overwritten. Version is the archive label; this script does not rewrite
Cargo package versions or the original .NET VERSION/installer.

The script requires a clean Git checkout, verifies the pinned toolchain, fetches
locked MSVC dependencies, runs the full baseline/scaffold/packaging tests, and then
builds only the three runtime binaries listed in `windows-payload.json` with
`--release --locked --offline --target x86_64-pc-windows-msvc` and static CRT.
Unset custom RUSTFLAGS/encoded/target Rust flags before release; the script owns
and restores its target flags. It never runs legacy CI or publishes a GitHub Release.

The JSON plan is the single inventory for package binaries, sidecars, launchers
and canonical emulator JSON templates. Web SPA is already embedded in Host.
User config, Data.dat, source code, test fixtures and AddIn DLLs are not bundled.
No .NET runtime, WinAppSDK or WebView2 payload is added. Npcap/COM drivers remain
external requirements for the physical channels; driver redistribution is not part
of this script. Windows platform/GPU/native runtime acceptance remains separate.

Each EXE must have an AMD64 PE32+ header. `release-manifest.json` records the source
commit, compiler, target, profile and every packaged file's size/SHA256 (except the
manifest itself). Packaging validates both the staging tree and extracted ZIP;
missing/extra/tampered files and path collisions fail before archive publication.
Hashes establish integrity, not signing. Binaries are not Authenticode-signed.

Extract the complete ZIP and read `README-Windows.txt`. For example:

```powershell
.\Start-Host.ps1 -StationConfig 'C:\path\to\existing\config.ini'
.\Start-Shell.ps1 -WebPort 8090
.\Start-Emulator.ps1
```

Host must already be started before Shell; use the station config's actual web_port.
Emulator is independent and uses its own per-user development XML by default.
The launch scripts resolve resources relative to themselves, not the caller's cwd.
Default tray icons in the Rust apps resolve relative to the executable as well.
For source-tree debug desktop runs, copy `rust/assets/*.ico` to
`rust/target/debug/assets/` after `cargo build`; release packaging supplies these
sidecars automatically. Hardware streams are OFF in a fresh emulator config.

`.github/workflows/rust-windows-release.yml` runs this command on a Windows runner
for packaging PRs or manual workflow dispatch (input `version`). It then extracts
the archive into a path with spaces/Unicode outside the checkout, runs Host and
headless emulator status/quit smoke checks plus Shell loader validation, and runs
the existing Chrome/theme contracts against the extracted release Host EXE.
Only a successful pipeline uploads `windows-x64-release` containing ZIP/checksum.
It does not create a tag, merge a PR or publish a public release.
Rust migration CI is Windows-only too; browser checks moved from Linux to Windows.

## M7 boundary

This packages the current development implementation, not a production/MSI
replacement. The original WiX remains in `../../installer`. Its MSI still installs
the .NET application; it is not silently repurposed to distribute Rust.
Production filename/upgrade identity/cutover and unchanged AddIn placement remain
M7 acceptance work. No claim of full visual, hardware, install/upgrade parity is made.
