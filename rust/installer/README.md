# Windows installer: Rust applications + original net48 AddIn

Build on Windows x64 in PowerShell 7.4+, with Git, rustup, MSVC x64/Windows SDK,
.NET SDK 10 and the installed Halliburton iCem SDK. Rust is pinned by
`rust-toolchain.toml`; WiX/extensions use the existing 5.0.2 pin in
`installer/.config/dotnet-tools.json`. No Linux application build is required.

From the repository root:

```powershell
pwsh -NoProfile -File .\rust\tools\Build-WindowsRelease.ps1 -Version 0.1.0
```

Deliverables: `rust/dist/iCemDataMonitor-rust-dev-0.1.0-windows-x64.msi` and
`.msi.sha256`. A ZIP/checksum remains an internal verified build intermediate.
Only the MSI/checksum is uploaded for a real release. `-OutputDirectory` selects
another output folder. Existing files/versions are not overwritten.

## AddIn is mandatory

The builder first compiles the unchanged
`Burservis.iCem.DataMonitor/Burservis.iCem.DataMonitor.csproj` as Release x64,
.NET Framework 4.8, using its original vendor reference paths under
`C:\Program Files\Halliburton\iCem\Bin`. Every SDK reference must exist; absence
fails before the expensive Rust build. The previous legacy script's warning/skip
behavior is deliberately not used for a complete package. No SDK stubs are used.
Intermediate/output files go to an owned temporary folder; the .NET source/project
and original build script are not rewritten.

An SDK machine can prepare a bundle separately:

```powershell
.\rust\tools\Build-LegacyAddin.ps1 -OutputDirectory D:\Builds\net48-addin
.\rust\tools\Build-WindowsRelease.ps1 -Version 0.1.0 -AddinDirectory D:\Builds\net48-addin
```

The directory contains exactly `Burservis.iCem.DataMonitor.dll` and
`addin-manifest.json`. The manifest records the source commit, canonical source
hashes (project, all explicit C# compile inputs and applicable root build files),
SDK reference hashes, build tool and DLL hash/size. Metadata inspection requires
AMD64 managed DLL, exact assembly identity and the net48 target attribute; it
never loads/executes the supplied assembly. The bundle must match current AddIn
sources. CRLF checkout conversion does not invalidate source hashes. Proprietary
SDK DLLs are never redistributed. Bundle metadata/hashes provide traceability and
integrity, not signing or independent proof of execution compatibility. Use a
trusted SDK machine; final iCem load/behavior acceptance remains open.

## Audited MSI ownership (architecture v3.1)

See [the mechanism audit](../docs/INSTALLER_AUDIT.ru.md). Rust owns a small
`Product.wxs`; it no longer projects the entire legacy wizard/policy. The original
`gen-payload.ps1`, `gen-ui-bmps.ps1`, Burservis PNG and icon remain reused unchanged.
There is one inventory, not a second manually maintained MSI file list.

| Behavior | Rust MSI |
|---|---|
| Branding | Original Burservis logo, regenerated banner/dialog BMPs and original app icon; no post-build SQL patches |
| Runtime | `%ProgramData%\Halliburton\iCem\DataMonitoring` (`MONITORROOT`) |
| Registries | Original Host registry (3507 variables) under `templates/`; original emulator registry (140 variables) and other templates under `emulator/templates/`; launchers read role paths from manifest |
| AddIn | `[ICEMDIR]\Bin\Modules\Burservis.iCem.DataMonitor.dll`; provenance stays in runtime `addin/` |
| iCem path | Standard default in the directory wizard; explicit CLI/wizard ICEMDIR remains authoritative |
| In-use files | Native Restart Manager: graceful shutdown by default, explicit per-operation force checkbox for unresponsive file owners; no WMI/process-name kills or kill on Cancel |
| Desktop/autostart | Opt-in, default OFF. Desktop targets Shell; installing user's HKCU Run targets Host `--autostart`; emulator never autostarts. Task Manager disable state is not erased |
| N → N+1 | Separate stable Rust UpgradeCode and component GUID namespace; new ProductCode, increasing numeric versions, transactional removal, blocked downgrade; saved paths/options restored automatically |
| Uninstall | Removes only MSI-owned files/AddIn/options; preserves user-created config/data/logs. No recursive wipe |
| Registration | Visible in installed applications; standard MSI uninstall/repair |
| Legacy cutover | Registered legacy MSI detected with OnlyDetect and blocks installation; never automatically removed |

Repeat launching the same MSI repairs missing **and corrupt** owned files, with
saved runtime/AddIn paths and options. The maintenance wizard offers Repair and
shows the options page, including explicit force consent; silent repeated `/i`
also performs full repair. Explicit path/option properties override saved values.
`CREATEDESKTOPSHORTCUT=0 ADDAUTOSTART=0` disables the native option components;
subsequent launches preserve that choice. `MSIRMSHUTDOWN=1` is the silent equivalent
of the force checkbox; it is never saved. Restart Manager first requests graceful
closure, then can force unresponsive owners after native deadlines. User-created
config/data/logs are preserved. A reboot-required result is not a verified complete
installation; the release gate requires exit 0 and verifies the installed hashes.
No config or Data.dat is shipped/created by packaging. Legacy .NET MSI is separate
and unchanged.

This remains a **Rust development candidate**, retaining `*-dev.exe` names.
Production cutover is not enabled merely by adding an installer. [Host E11](../docs/HOST_E11.ru.md) implements automatic Shell ensure, bounded startup,
CLI and copy-only configuration discovery/default creation. Invalid existing settings
remain intact and block startup with diagnostics. The MSI grants inherited
Users Modify on the canonical station directory, including relocated runtime installs, matching legacy. Supplied PowerShell
launchers remain optional. Hardware/UI/iCem acceptance stays open.

## Payload integrity

`windows-payload.json` schema 2 inventories three runtime roles, original icons,
logo, launchers, canonical templates and mandatory AddIn mount. New runtime files
enter this inventory once. The unchanged legacy harvester receives a staged runtime
and a separate AddIn mount; its output must cover every file exactly once and
contain exactly one DLL in `Payload.Addin`. Manifest/source collisions, non-AMD64
EXEs, missing/extra/tampered files, absent/mismatched AddIn and escaping paths fail.

`release-manifest.json` schema 2 records source commit, compiler, release/static
CRT target and all payload SHA256/size values. ZIP staging/extraction is validated.
MSI is built with high CAB compression and warnings/validation errors fail the
build. No .NET10/WinAppSDK/WebView2 runtime payload is added; net48 belongs to the
original iCem process. Npcap/COM drivers remain external. Signing is not implemented.
Only owned temporary build directories are cleaned; unrelated bin/obj trees and
original assets are not deleted or regenerated in place.

## GitHub: N+1 and SDK build options

After the workflows reach main:

1. On a trusted Windows machine, register a self-hosted runner with labels
   `Windows`, `X64`, `icem-sdk` and the real licensed SDK at the original paths.
   Runner registration/SDK provisioning is external; this change does not perform it.
2. Either run **rust-windows-release** with `sdk_runner: true`, or first run
   **rust-addin-build** on that runner and supply its successful run ID as
   `addin_run_id` to a hosted **rust-windows-release** run.
3. The release workflow verifies the artifact comes from the successful manual SDK
   workflow in this repository, binds its manifest commit to that run, and compares
   AddIn input hashes with the current source. A plain hosted runner with neither
   SDK nor verified AddIn bundle cannot make a complete release.
4. `version: next` advances the highest `rust-vX.Y.Z` tag/release/draft patch,
   starting at `0.1.0`. Explicit higher `x.y.z` selects a minor/major version.
   Legacy `vX.Y.Z` tags are separate. MSI limits are `255.255.65535`.
5. `publish: false` uploads an Actions artifact; `true` also creates a private-repo
   draft prerelease after all real release checks. It never merges PRs, retries
   mutations or replaces existing assets. Manual runs are serialized.

## Evidence: installer contracts versus real release

On PRs, the Windows `installer-contracts` job validates packaging and builds two
MSIs with test runtime bytes (a Windows system EXE) and a synthetic managed DLL. It exercises
**native WiX authoring, original branding generation and Windows Installer**:
install N, two mounts, shortcut/autostart targets, repeated repair of missing/corrupt files, saved preferences, graceful/explicit force file-owner shutdown with same-name foreign process isolation, upgrade N+1, blocked downgrade,
then removal of owned resources while retaining unowned data. Default opt-out is also tested. It publishes logs, never a fixture MSI. This proves
installer mechanics, not an actual AddIn build or Rust runtime behavior.

The native wizard is also exercised through Windows UI Automation: checkbox
defaults, saved and explicit zero choices, force on/off, Back, Cancel and a repair
that commits opt-out, then reopens with it unchecked. UI uses null for unchecked;
only the execute sequence converts this to numeric zero. Force consent is never
saved. Interaction checks do not establish visual/DPI acceptance.

A manually requested real release additionally requires the real SDK-built AddIn,
full baseline/scaffold/Rust checks, optimized MSVC runtime binaries, extracted EXE
smoke checks and Chrome/theme contracts. The prior release attempt on `5d464d1`
failed before MSI creation in the unchanged Rust pipe rebind test; a separate full
Windows Rust CI passed that same head. That lifecycle race is recorded in PR #4,
not bypassed by packaging. SDK-backed release, visual wizard acceptance, iCem DLL
loading, production/offline/rollback/failure/running-process acceptance remain open.

