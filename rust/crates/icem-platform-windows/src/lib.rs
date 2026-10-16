#![forbid(unsafe_code)]
//! M0 native integration boundary. NO Windows implementation yet.
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
