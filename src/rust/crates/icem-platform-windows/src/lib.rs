#![forbid(unsafe_code)]
//! Windows application adapters; audited raw FFI is isolated in icem-windows-native.
//! MMF synchronization/ACL/abandoned mutex proof is a mandatory M2 gate.
pub const BUS_MAPPING: &str = r"Global\iCemDataMonitorBus_v1";
pub const BUS_MUTEX: &str = r"Global\iCemDataMonitorBusMutex_v1";
pub const HOST_MUTEX: &str = r"Global\iCemDataMonitorHost_v1";
pub const SHELL_MUTEX: &str = r"Global\iCemDataMonitorTray_v1";
pub const EMULATOR_MUTEX: &str = r"Global\iCemEmulator_v1";
pub const HOST_RESTART: &str = r"Local\iCemDataMonitorTray_v1_Restart";
pub const HOST_SHUTDOWN: &str = r"Local\iCemDataMonitorTray_v1_HostShutdown";
pub const SHELL_SHOW: &str = r"Local\iCemDataMonitorTray_v1_ShowWindow";
pub const SHELL_SHUTDOWN: &str = r"Local\iCemDataMonitorTray_v1_Shutdown";
pub const THEME_CHANGED: &str = r"Local\iCemDataMonitorTray_v1_UiTheme";

/// Application-owned sidecar assets live beside the EXE, independently of cwd.
/// User settings and station data deliberately use their separate path policies.
pub fn application_asset(name: &str) -> std::io::Result<std::path::PathBuf> {
    asset_at(&std::env::current_exe()?, name)
}
fn asset_at(exe: &std::path::Path, name: &str) -> std::io::Result<std::path::PathBuf> {
    use std::path::{Component, Path};
    let mut components = Path::new(name).components();
    if !matches!(components.next(), Some(Component::Normal(_))) || components.next().is_some() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "asset must be one filename",
        ));
    }
    let path = exe.with_file_name("assets").join(name);
    if !path.is_file() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            format!("application asset missing: {}", path.display()),
        ));
    }
    Ok(path)
}

#[cfg(test)]
mod asset_tests {
    #[test]
    fn relocated_assets_require_sidecars_and_reject_parent_paths() -> std::io::Result<()> {
        let root = std::env::temp_dir().join(format!("icem-assets-{}", std::process::id()));
        std::fs::create_dir_all(root.join("assets"))?;
        let exe = root.join("app.exe");
        let path = root.join("assets/relocated.ico");
        std::fs::write(&path, b"test resource")?;
        assert_eq!(super::asset_at(&exe, "relocated.ico")?, path);
        assert!(super::asset_at(&exe, "../relocated.ico").is_err());
        assert!(super::asset_at(&exe, "missing.ico").is_err());
        std::fs::remove_dir_all(root)
    }
}

#[cfg(windows)]
mod bus_reader;
#[cfg(windows)]
pub use bus_reader::WindowsBusReader;

#[cfg(windows)]
pub fn signal_theme_changed() -> Result<bool, icem_windows_native::NativeError> {
    icem_windows_native::signal_existing_event(THEME_CHANGED)
}

/// The caller keeps the mutex and its thread-bound guard alive through teardown.
#[cfg(windows)]
pub fn emulator_mutex() -> Result<icem_windows_native::NamedMutex, icem_windows_native::NativeError>
{
    icem_windows_native::NamedMutex::open_or_create(EMULATOR_MUTEX)
}

mod lan_worker;
pub use lan_worker::QueuedAceLanEmitter;
pub mod console;
pub mod sniffer;
pub mod transport;
pub use transport::{AceLanEmitter, ComSerialEmitter, PacketDevice, SerialDevice, burst_start_ms};

#[cfg(windows)]
pub use icem_windows_native::console_visible;

#[cfg(windows)]
pub fn host_autostart()
-> Result<icem_windows_native::AutostartEntry, Box<dyn std::error::Error + Send + Sync>> {
    let exe = std::env::current_exe()?;
    let exe = exe.to_str().ok_or("Host executable path is not Unicode")?;
    Ok(icem_windows_native::AutostartEntry::new(
        r"Software\Microsoft\Windows\CurrentVersion\Run",
        r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        "iCemDataMonitorHost",
        exe,
    )?)
}

pub mod sniffer_catalog;

pub mod landiag;
