#![forbid(unsafe_code)]
//! Station configuration; INI read/parse is implemented, write/migration/XML remain pending.
pub const STATION_ROOT: &str = r"C:\ProgramData\Halliburton\iCem\DataMonitoring";
pub const DEFAULT_WEB_PORT: u16 = 8090;
pub const DEFAULT_MODBUS_PORT: u16 = 502;
pub const DEFAULT_MODBUS_TICK_MS: u16 = 500;
pub const SOURCE_INI_SCHEMA: &str = include_str!("../../../../specs/dotnet-server/Э5_config.csv");

mod ini;
pub use ini::{ConfigError, IniOptions};

pub mod emulator;
pub use emulator::{EmuSettings, SettingsPaths, LoadSettings, SettingsError};
