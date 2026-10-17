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
pub mod transport;
pub use transport::{AceLanEmitter, ComSerialEmitter, PacketDevice, SerialDevice, burst_start_ms};

#[cfg(windows)]
pub use icem_windows_native::{console_visible, set_console_visible};

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
