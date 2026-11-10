# Shared installer build inputs

This layout increment gives the common Setup launcher, Release-only producer,
build/read-only validation and shared WiX/MSI helpers one owner. The launcher
keeps its exact executable/resource names, embedded MSI/family/hash contract,
selected-ProductCode revalidation and explicit use of each product's own MSI `/x`.
Family Product authoring and identity remain in their respective product trees.

Both Rust and .NET release consumers call `tools/Build-WindowsSetup.ps1` directly;
the family fixtures use `tools/Test-WindowsSetupBuild.ps1`. No forwarding wrapper
or duplicate launcher is retained. Routing/family-gate tooling is in repository
root `tools/ci`; Rust cache/vendor/toolchain tooling remains under `rust/tools/ci`.
