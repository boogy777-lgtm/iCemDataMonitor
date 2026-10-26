//! Synchronous emitter adapters. Protocol/domain code owns no native handles.
use icem_emulation::{
    lan::VectorData,
    runtime_ports::{ComEmitter, LanEmitter},
};
use std::time::Duration;

/// An exclusive serial endpoint. Successful writes must consume the whole slice.
pub trait SerialDevice: Send {
    fn open(&mut self, name: &str) -> Result<(), String>;
    fn close(&mut self);
    fn write_all(&mut self, bytes: &[u8]) -> Result<(), String>;
    fn ports(&self) -> Vec<String>;
}
#[derive(Default)]
pub struct ComSerialEmitter<D = SystemSerial> {
    device: D,
    port: Option<String>,
    lines: u64,
}
impl<D: SerialDevice> ComSerialEmitter<D> {
    pub fn new(device: D) -> Self {
        Self {
            device,
            port: None,
            lines: 0,
        }
    }
    pub fn device(&self) -> &D {
        &self.device
    }
}
impl<D: SerialDevice> ComEmitter for ComSerialEmitter<D> {
    fn open(&mut self, port: &str) -> Result<(), String> {
        self.close();
        self.device.open(port)?;
        self.port = Some(port.into());
        self.lines = 0;
        Ok(())
    }
    fn close(&mut self) {
        self.device.close();
        self.port = None;
    }
    fn is_open(&self) -> bool {
        self.port.is_some()
    }
    fn port_name(&self) -> Option<&str> {
        self.port.as_deref()
    }
    fn lines_sent(&self) -> u64 {
        self.lines
    }
    fn send_line(&mut self, line: &str) -> Result<(), String> {
        if !self.is_open() {
            return Err("COM port is closed".into());
        }
        if line.len() > 65_534 {
            return Err("Serial line exceeds 65534 bytes".into());
        }
        let mut bytes = Vec::with_capacity(line.len() + 2);
        bytes.extend_from_slice(line.as_bytes());
        bytes.extend_from_slice(b"\r\n");
        self.device.write_all(&bytes)?;
        self.lines = self.lines.saturating_add(1);
        Ok(())
    }
    fn ports(&self) -> Vec<String> {
        self.device.ports()
    }
}

/// Injection seam matching the legacy send-device contract. No packet references escape.
pub trait PacketDevice: Send {
    fn open(&mut self, name: &str) -> Result<(), String>;
    fn close(&mut self);
    fn mac(&self) -> Option<[u8; 6]>;
    fn send(&mut self, frame: &[u8]) -> Result<(), String>;
    fn adapters(&self) -> Vec<String>;
    fn npcap_available(&self) -> bool;
}
#[derive(Default)]
pub struct AceLanEmitter<D = SystemPacket> {
    device: D,
    adapter: Option<String>,
    frames: u64,
}
/// Preserve C# floating-point/truncation order (6 -> 938, 20 -> 885 ms).
pub fn burst_start_ms(count: usize) -> u64 {
    let fraction = if count > 6 {
        0.938_f64.min(1.0 - count as f64 * 5.25 / 1000.0 - 0.01)
    } else {
        0.938
    };
    (fraction.max(0.0) * 1000.0) as u64
}
impl<D: PacketDevice> AceLanEmitter<D> {
    pub fn new(device: D) -> Self {
        Self {
            device,
            adapter: None,
            frames: 0,
        }
    }
    pub fn device(&self) -> &D {
        &self.device
    }
    /// Inject only the wait operation for deterministic pacing/failure tests.
    pub fn send_with_wait(
        &mut self,
        vectors: &[VectorData],
        equipment: u32,
        mut wait: impl FnMut(Duration),
    ) -> Result<(), String> {
        if self.adapter.is_none() {
            return Err("LAN adapter is closed".into());
        }
        if vectors.is_empty() {
            return Ok(());
        }
        let mac = self
            .device
            .mac()
            .ok_or("Src MAC not set and adapter has no MAC. Use the textbox.")?;
        let mut failure = None;
        for (index, vector) in vectors.iter().enumerate() {
            wait(Duration::from_millis(if index == 0 {
                burst_start_ms(vectors.len())
            } else {
                5
            }));
            let frame =
                icem_protocols::ace::build(mac, equipment, vector.sv_number, &vector.payload);
            match self.device.send(&frame) {
                Ok(()) => self.frames = self.frames.saturating_add(1),
                Err(error) => {
                    failure.get_or_insert(error);
                }
            }
        }
        failure.map_or(Ok(()), Err)
    }
}
impl<D: PacketDevice> LanEmitter for AceLanEmitter<D> {
    fn open(&mut self, adapter: &str) -> Result<(), String> {
        self.close();
        self.device.open(adapter)?;
        self.adapter = Some(adapter.into());
        self.frames = 0;
        Ok(())
    }
    fn close(&mut self) {
        self.device.close();
        self.adapter = None;
    }
    fn is_open(&self) -> bool {
        self.adapter.is_some()
    }
    fn adapter_name(&self) -> Option<&str> {
        self.adapter.as_deref()
    }
    fn frames_sent(&self) -> u64 {
        self.frames
    }
    fn send_superframe(&mut self, vectors: &[VectorData], equipment: u32) -> Result<(), String> {
        self.send_with_wait(vectors, equipment, std::thread::sleep)
    }
    fn adapters(&self) -> Vec<String> {
        self.device.adapters()
    }
    fn npcap_available(&self) -> bool {
        self.device.npcap_available()
    }
}

#[derive(Default)]
pub struct SystemSerial {
    #[cfg(windows)]
    port: Option<icem_windows_native::SerialPort>,
}
impl SerialDevice for SystemSerial {
    fn open(&mut self, name: &str) -> Result<(), String> {
        #[cfg(windows)]
        {
            self.port =
                Some(icem_windows_native::SerialPort::open(name).map_err(|e| e.to_string())?);
            Ok(())
        }
        #[cfg(not(windows))]
        {
            let _ = name;
            Err("COM requires Windows".into())
        }
    }
    fn close(&mut self) {
        #[cfg(windows)]
        {
            self.port = None;
        }
    }
    fn write_all(&mut self, bytes: &[u8]) -> Result<(), String> {
        #[cfg(windows)]
        {
            self.port
                .as_mut()
                .ok_or("COM port is closed")?
                .write_all(bytes)
                .map_err(|e| e.to_string())
        }
        #[cfg(not(windows))]
        {
            let _ = bytes;
            Err("COM requires Windows".into())
        }
    }
    fn ports(&self) -> Vec<String> {
        #[cfg(windows)]
        {
            icem_windows_native::serial_ports().unwrap_or_default()
        }
        #[cfg(not(windows))]
        {
            Vec::new()
        }
    }
}
#[derive(Default)]
pub struct SystemPacket {
    #[cfg(windows)]
    session: Option<icem_windows_native::PacketSession>,
    #[cfg(windows)]
    mac: Option<[u8; 6]>,
}
impl PacketDevice for SystemPacket {
    fn open(&mut self, name: &str) -> Result<(), String> {
        self.close();
        #[cfg(windows)]
        {
            if name.trim().is_empty() {
                return Err("Adapter name must not be empty.".into());
            }
            let pcap = icem_windows_native::Pcap::load()?;
            let key = name.to_ascii_lowercase();
            let adapter = pcap
                .adapters()?
                .into_iter()
                .find(|a| {
                    format!("{} ({})", a.description, a.name)
                        .to_ascii_lowercase()
                        .contains(&key)
                })
                .ok_or_else(|| format!("Adapter not found: '{name}'"))?;
            let session = pcap.open(&adapter.name, false)?;
            self.mac = icem_windows_native::adapter_mac(&adapter.name);
            self.session = Some(session);
            Ok(())
        }
        #[cfg(not(windows))]
        {
            let _ = name;
            Err("Npcap requires Windows".into())
        }
    }
    fn close(&mut self) {
        #[cfg(windows)]
        {
            self.session = None;
            self.mac = None;
        }
    }
    fn mac(&self) -> Option<[u8; 6]> {
        #[cfg(windows)]
        {
            self.mac
        }
        #[cfg(not(windows))]
        {
            None
        }
    }
    fn send(&mut self, frame: &[u8]) -> Result<(), String> {
        #[cfg(windows)]
        {
            self.session
                .as_mut()
                .ok_or("LAN adapter is closed")?
                .send(frame)
        }
        #[cfg(not(windows))]
        {
            let _ = frame;
            Err("Npcap requires Windows".into())
        }
    }
    fn adapters(&self) -> Vec<String> {
        #[cfg(windows)]
        {
            icem_windows_native::Pcap::load()
                .and_then(|p| p.adapters())
                .unwrap_or_default()
                .into_iter()
                .map(|a| format!("{} ({})", a.description, a.name))
                .collect()
        }
        #[cfg(not(windows))]
        {
            Vec::new()
        }
    }
    fn npcap_available(&self) -> bool {
        #[cfg(windows)]
        {
            icem_windows_native::npcap_available()
        }
        #[cfg(not(windows))]
        {
            false
        }
    }
}
