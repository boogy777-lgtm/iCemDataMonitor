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
pub use pcap::{Adapter, PacketSession, Pcap, adapter_mac, npcap_available};
#[cfg(windows)]
pub use serial::{SerialPort, serial_ports};

#[cfg(windows)]
pub use windows::{OwnedEvent, console_visible, event_exists, set_console_visible};

#[cfg(windows)]
#[allow(unsafe_code)]
mod autostart;
#[cfg(windows)]
pub use autostart::AutostartEntry;
