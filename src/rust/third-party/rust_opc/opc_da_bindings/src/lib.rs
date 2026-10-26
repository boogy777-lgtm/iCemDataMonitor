mod abi;
include!(concat!(env!("OUT_DIR"), "/bindings-module.rs"));
pub mod client;
pub mod server;

// Generated Windows ABI symbols are private implementation details.
pub(crate) use bindings::*;
