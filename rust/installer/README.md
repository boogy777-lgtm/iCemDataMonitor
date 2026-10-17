# Rust packaging — M7

The original WiX source remains under ../../installer (repository root).
No Rust installer exists yet. Do not run the original build-msi script expecting Rust.
M7 must preserve observed install/upgrade/uninstall paths and semantics, AddIn placement,
shortcuts, app icons, config retention and autostart; establish that from Product.wxs.
Production names are iCemDataMonitor.Host.exe, iCemDataMonitor.WinUI.exe (compatibility
filename only, even after the engine changes), iCemEmulator.exe and the unchanged AddIn.
Rust development binaries intentionally use *-dev names to prevent accidental deployment.
