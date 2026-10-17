# Windows x64 Rust installer and N+1 releases

Build on Windows x64 in **PowerShell 7.4+**, with Visual Studio C++ Build Tools
(MSVC x64 and Windows SDK), Git, rustup and .NET SDK 10 for the pinned WiX 5.0.2
build tool. .NET SDK is a build dependency; the Rust EXEs need no .NET runtime.
No Linux build is required. Rust comes from `../rust-toolchain.toml` (1.99.1).

From the repository root:

```powershell
pwsh -NoProfile -File .\rust\tools\Build-WindowsRelease.ps1 -Version 0.1.0
```

Delivery files in `rust/dist/`:

- `iCemDataMonitor-rust-dev-0.1.0-windows-x64.msi`
- the adjacent `.msi.sha256`

A verified ZIP/checksum remains a build intermediate in that directory. The GitHub
workflow uploads only the MSI/checksum as `windows-x64-release`. Existing files
are not overwritten. `-OutputDirectory D:\Releases` selects another output folder.
Version must be canonical `x.y.z`, at most `255.255.65535`; it labels the installer
and payload, without rewriting Cargo versions or legacy VERSION/installer.

The script requires a clean Git checkout, verifies the pinned compiler, fetches
locked MSVC dependencies, runs full baseline/scaffold/packaging tests, then builds
the three runtime binaries in `windows-payload.json` with
`--release --locked --offline --target x86_64-pc-windows-msvc` and static CRT.
Unset custom RUSTFLAGS/encoded/target flags; the script owns/restores its flags.
WiX is restored from `installer/.config/dotnet-tools.json`; warnings/validation
errors fail the MSI build. No legacy build/tests are invoked by this script.

## Contents and installation ownership

The JSON payload plan inventories Host, Shell, the actual independent emulator
engine, original icons, launchers and canonical emulator JSON templates. Web SPA
is embedded in Host. No source, fixtures, user station config, Data.dat, AddIn,
.NET runtime, WinAppSDK or WebView2 payload is bundled. Npcap/COM drivers remain
external requirements. Packaged apps retain development EXE names.

Each EXE must have an AMD64 PE32+ header. `release-manifest.json` records source
commit, compiler, target/profile/static CRT and every file size/SHA256. ZIP staging
and extracted contents are verified. The MSI is generated from that verified
inventory, including the manifest; a newly added payload file automatically becomes
an MSI component. Case collisions, escape paths, missing/extra/tampered files and
accidental overwrite fail. Hashes establish integrity, not Authenticode signing.

`windows-installer.json` fixes this **development** product's UpgradeCode and
separate installation folder: `%ProgramFiles%\Burservis\iCemDataMonitor Rust Development`.
It installs per machine (administrator approval), registers in installed apps and
supports standard `msiexec` removal. It does not use the legacy MSI UpgradeCode,
write its paths/AddIn, install drivers/services, autostart or launch apps. Close
Rust apps before updating. Component identities depend on target paths, not the
build directory. A higher MSI version upgrades the previous development version
transactionally; downgrades are blocked. User-created files are not MSI-owned and
are preserved on upgrade/uninstall. This is not the production cutover policy.

After installing, read `README-Windows.txt` in the install folder. With PowerShell
7.4+, start from that folder (station config must already exist):

```powershell
.\Start-Host.ps1 -StationConfig 'C:\path\to\existing\config.ini'
.\Start-Shell.ps1 -WebPort 8090
.\Start-Emulator.ps1
```

Host must already be running before Shell; use the station config's actual web_port.
The emulator is independent, with per-user development XML by default; a fresh
config starts hardware streams OFF. Launchers and default tray icons resolve
relative to the package/executable rather than the caller's cwd. Source-tree debug
desktop runs must copy `rust/assets/*.ico` into `rust/target/debug/assets/`.

## GitHub Release: N to N+1

After this workflow reaches main, open **Actions → rust-windows-release → Run workflow**:

- `version: next` chooses the next patch after the highest `rust-vX.Y.Z` tag or
  existing release/draft, starting at `0.1.0`. Numeric sorting handles `0.1.9` →
  `0.1.10`. Legacy `vX.Y.Z` tags do not participate or trigger the old release flow.
- An explicit higher `x.y.z` selects a minor/major release. Patch exhaustion at
  65535 requires that explicit choice. Existing/lower versions are rejected.
- `publish: false` only builds/checks and uploads an Actions artifact.
- `publish: true` also creates a **draft prerelease** `rust-vX.Y.Z` containing the
  verified MSI/checksum. Publication is restricted to the existing private repo;
  only the manual version resolver (to see reserved drafts) and publication job
  get `contents: write`; PR/build jobs remain read-only. It does not merge PRs or publish a public
  release. Review/publish the draft from Releases when desired.

Manual release runs are serialized to prevent N+1 races. Failed mutations are not
retried and existing release assets are not replaced. Local build scripts never
create tags/releases themselves.

Before uploading an MSI, Windows CI verifies the full source baseline and Rust
checks, runs extracted EXEs outside the checkout in a Unicode/space path, checks
Host binding, emulator status/quit ACK, missing-icon cleanup and Shell loader
errors, then installs N, builds an N+1 packaging probe from the same binary bytes,
upgrades, rejects downgrade and uninstalls. It checks file hashes, one installed
product, and preservation of a test-owned untracked file. The probe tests installer
mechanics, not functional differences between application versions. Chrome/theme
contracts run against the extracted optimized Host. MSI logs and browser evidence
are uploaded separately. No Linux binary is built.

`tools/Test-WindowsMsiUpgrade.ps1` is for disposable Windows CI/VMs only; it refuses
an already installed development product. Outside Actions it requires the explicit
`-AllowMachineChanges` switch. This is an actual machine installation test.

## M7 boundary

This MSI distributes the current development implementation. The original WiX in
`../../installer` continues to install .NET and is unchanged. Full production
filename/upgrade identity/path cutover, AddIn deployment, offline/rollback/failure
acceptance on a clean customer VM, running-process policy, hardware and visual
parity remain open. CI installation evidence covers its exact Windows runner and
commit; it does not prove those separate gates. Signing is also not implemented.
