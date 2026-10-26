mod abi;
include!(concat!(env!("OUT_DIR"), "/bindings-module.rs"));
pub mod client;
pub mod server;

// Keep generated COM ABI symbols inside this crate; use the safe client/server
// facades for application code.
pub(crate) use bindings::*;
