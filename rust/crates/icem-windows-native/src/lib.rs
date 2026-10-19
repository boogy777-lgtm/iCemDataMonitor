#![deny(unsafe_code)]
//! Sole Win32 FFI boundary; see ADR 0001. The rest of the workspace forbids unsafe.
#[cfg(windows)]
#[allow(unsafe_code)]
mod windows;
#[cfg(windows)]
pub use windows::{Mapping, NamedMutex, NamedMutexGuard, NativeError, signal_existing_event};

#[cfg(windows)]
#[allow(unsafe_code)]
mod pcap;
#[cfg(windows)]
#[allow(unsafe_code)]
mod serial;
#[cfg(windows)]
pub use pcap::{Adapter, CaptureSession, PacketSession, Pcap, adapter_mac, npcap_available};
#[cfg(windows)]
pub use serial::{SerialPort, serial_ports};

#[cfg(windows)]
pub use windows::{OwnedEvent, event_exists};

#[cfg(windows)]
#[allow(unsafe_code)]
mod console;
#[cfg(windows)]
pub use console::{
    ConsoleControl, console_diagnostic, console_restored, console_state, console_visible,
    inherited_console_visible,
};

#[cfg(windows)]
#[allow(unsafe_code)]
mod autostart;
#[cfg(windows)]
pub use autostart::AutostartEntry;

#[cfg(windows)]
#[allow(unsafe_code)]
mod log_directory;
#[cfg(windows)]
pub use log_directory::LogDirectory;

#[cfg(windows)]
#[allow(unsafe_code)]
mod owned_window;
#[cfg(windows)]
pub use owned_window::{request_window_visible, window_visibility};
