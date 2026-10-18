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

## Legacy installer reuse and explicit boundaries

The Rust installer projects the **unchanged** `../../installer/Product.wxs` into
its own temporary build directory. It also reuses the original
`gen-payload.ps1`, `gen-ui-bmps.ps1`, conflict script and its executable harness.
There is no independent copy of the legacy wizard or payload harvester to drift.

| Behavior | Rust MSI authoring |
|---|---|
| Burservis branding | Original `Burservis_logo_cyr_red.png`, regenerated banner/dialog BMPs, original app icon; stock WiX geometry, no SQL patch |
| Runtime path | `%ProgramData%\Halliburton\iCem\DataMonitoring` (`MONITORROOT`), flat Rust EXEs/resources |
| AddIn path | `[ICEMDIR]\Bin\Modules\Burservis.iCem.DataMonitor.dll`; provenance remains in runtime `addin/` |
| iCem discovery | Original canonical-directory search and directory-selection wizard; explicit command-line ICEMDIR is respected |
| Process/service gate | Original checks and close/refresh actions; runtime image names adapt from manifest roles; adapted harness must pass |
| Desktop/autostart | Original opt-in controls; shortcut targets Rust Shell, HKCU Run targets Rust Host with `--autostart`; emulator never autostarts |
| Upgrade N → N+1 | Separate stable Rust UpgradeCode; increasing numeric versions, downgrade blocked; user data survives upgrade |
| Full uninstall | Legacy behavior: remove DLL/owned files/options and wipe runtime folder, including runtime-created config/data/logs |
| UI registration | Legacy hidden ARP policy retained; uninstall via `msiexec /x <MSI-or-ProductCode>` |
| Legacy .NET cutover | Existing legacy MSI is detected and blocks candidate installation; it is never removed automatically |

The projected wipe CustomActionData uses `[MONITORROOT]`, preserving normal legacy
behavior while ensuring explicit test-path overrides cannot wipe the canonical
folder. **Full uninstall deletes runtime-created data. Back it up when needed.**
The old development MSI's preserve-on-uninstall rule is superseded by the requested
legacy cleanup behavior. No user config or Data.dat is shipped/created by packaging.
The .NET legacy MSI itself is unchanged, including its own update policy.

This remains a **Rust development candidate**, retaining `*-dev.exe` names.
Production cutover is not enabled merely by adding an installer. Host restoration,
ensure-host, existing/custom station-config startup and hardware/UI equivalence
still need their own acceptance. Host requires an existing station configuration;
the current Shell does not start Host automatically. Supplied PowerShell launchers
remain available; see README-Windows.txt and the Host status documents.

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
MSIs with explicitly synthetic PE/runtime and managed DLL fixtures. It exercises
**real legacy WiX authoring, branding generation/harness and Windows Installer**:
install N, two mounts, shortcut/autostart targets, upgrade N+1, blocked downgrade,
then full-uninstall cleanup. It publishes logs, never a fixture MSI. This proves
installer mechanics, not an actual AddIn build or Rust runtime behavior.

A manually requested real release additionally requires the real SDK-built AddIn,
full baseline/scaffold/Rust checks, optimized MSVC runtime binaries, extracted EXE
smoke checks and Chrome/theme contracts. The prior release attempt on `5d464d1`
failed before MSI creation in the unchanged Rust pipe rebind test; a separate full
Windows Rust CI passed that same head. That lifecycle race is recorded in PR #4,
not bypassed by packaging. SDK-backed release, visual wizard acceptance, iCem DLL
loading, production/offline/rollback/failure/running-process acceptance remain open.
