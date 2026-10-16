#![deny(unsafe_code)]
//! Sole Win32 FFI boundary; see ADR 0001. The rest of the workspace forbids unsafe.
#[cfg(windows)]
#[allow(unsafe_code)]
mod windows;
#[cfg(windows)]
pub use windows::{Mapping, NamedMutex, NamedMutexGuard, NativeError};
