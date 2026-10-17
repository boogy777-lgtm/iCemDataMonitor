//! Runtime-owned, synchronous I/O ports. Construction never opens an endpoint.
//! Serial adapters append CRLF; LAN adapters preserve vector order and pacing.
use crate::lan::VectorData;

pub trait ComEmitter: Send {
    fn open(&mut self, port: &str) -> Result<(), String>;
    fn close(&mut self);
    fn is_open(&self) -> bool;
    fn port_name(&self) -> Option<&str>;
    fn lines_sent(&self) -> u64;
    fn send_line(&mut self, line: &str) -> Result<(), String>;
    fn ports(&self) -> Vec<String>;
}

pub trait LanEmitter: Send {
    fn open(&mut self, adapter: &str) -> Result<(), String>;
    fn close(&mut self);
    fn is_open(&self) -> bool;
    fn adapter_name(&self) -> Option<&str>;
    fn frames_sent(&self) -> u64;
    fn send_superframe(&mut self, vectors: &[VectorData], equipment: u32) -> Result<(), String>;
    fn adapters(&self) -> Vec<String>;
    fn npcap_available(&self) -> bool;
}
