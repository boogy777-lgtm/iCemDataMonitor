//! Independent emulator diagnostic I/O; no Host or engine dependency.
use std::time::{Duration, Instant};

#[derive(Clone, Debug)]
pub struct Adapter {
    pub name: String,
    pub description: String,
    pub mac: Option<[u8; 6]>,
}
#[derive(Debug)]
pub struct WaitError {
    pub captured: u64,
    pub message: String,
}
pub trait Port {
    fn adapters(&mut self) -> Result<Vec<Adapter>, String>;
    fn open_capture(&mut self, adapter: &Adapter) -> Result<(), String>;
    fn open_sender(&mut self, adapter: &Adapter) -> Result<(), String>;
    fn close_capture(&mut self);
    fn close_sender(&mut self);
    fn send(&mut self, frame: &[u8]) -> Result<(), String>;
    fn wait(&mut self, milliseconds: u64) -> Result<u64, WaitError>;
    fn elapsed_ms(&self) -> u64;
}
#[derive(Default)]
pub struct SystemPort {
    #[cfg(windows)]
    capture: Option<icem_windows_native::CaptureSession>,
    #[cfg(windows)]
    sender: Option<icem_windows_native::PacketSession>,
    started: Option<Instant>,
}
/// A bounded drain retains all matches observed before a read fault.
pub fn drain(mut next: impl FnMut() -> Result<Option<Vec<u8>>, String>) -> Result<u64, WaitError> {
    let mut captured = 0;
    for _ in 0..256 {
        match next() {
            Ok(Some(frame)) => {
                if icem_protocols::ace::parse(&frame).is_some() {
                    captured += 1;
                }
            }
            Ok(None) => break,
            Err(message) => return Err(WaitError { captured, message }),
        }
    }
    Ok(captured)
}
impl Port for SystemPort {
    fn adapters(&mut self) -> Result<Vec<Adapter>, String> {
        #[cfg(windows)]
        {
            icem_windows_native::Pcap::load()?.adapters().map(|all| {
                all.into_iter()
                    .map(|a| Adapter {
                        mac: icem_windows_native::adapter_mac(&a.name),
                        name: a.name,
                        description: a.description,
                    })
                    .collect()
            })
        }
        #[cfg(not(windows))]
        {
            Err("Npcap requires Windows".into())
        }
    }
    fn open_capture(&mut self, adapter: &Adapter) -> Result<(), String> {
        #[cfg(windows)]
        {
            self.capture =
                Some(icem_windows_native::Pcap::load()?.open_diagnostic_capture(&adapter.name)?);
            self.started.get_or_insert_with(Instant::now);
            Ok(())
        }
        #[cfg(not(windows))]
        {
            let _ = adapter;
            Err("Npcap requires Windows".into())
        }
    }
    fn open_sender(&mut self, adapter: &Adapter) -> Result<(), String> {
        #[cfg(windows)]
        {
            self.sender = Some(icem_windows_native::Pcap::load()?.open(&adapter.name, false)?);
            Ok(())
        }
        #[cfg(not(windows))]
        {
            let _ = adapter;
            Err("Npcap requires Windows".into())
        }
    }
    fn close_capture(&mut self) {
        #[cfg(windows)]
        {
            self.capture = None;
        }
    }
    fn close_sender(&mut self) {
        #[cfg(windows)]
        {
            self.sender = None;
        }
    }
    fn send(&mut self, frame: &[u8]) -> Result<(), String> {
        #[cfg(windows)]
        {
            self.sender.as_mut().ok_or("sender is closed")?.send(frame)
        }
        #[cfg(not(windows))]
        {
            let _ = frame;
            Err("Npcap requires Windows".into())
        }
    }
    fn wait(&mut self, milliseconds: u64) -> Result<u64, WaitError> {
        let started = Instant::now();
        let mut captured = 0;
        while started.elapsed() < Duration::from_millis(milliseconds) {
            let batch = {
                #[cfg(windows)]
                {
                    self.capture
                        .as_mut()
                        .map_or(Ok(0), |capture| drain(|| capture.next_packet()))
                }
                #[cfg(not(windows))]
                {
                    Ok::<u64, WaitError>(0)
                }
            };
            match batch {
                Ok(count) => captured += count,
                Err(mut error) => {
                    error.captured += captured;
                    return Err(error);
                }
            }
            std::thread::sleep(Duration::from_millis(1));
        }
        Ok(captured)
    }
    fn elapsed_ms(&self) -> u64 {
        self.started.map_or(0, |t| {
            t.elapsed().as_millis().min(u128::from(u64::MAX)) as u64
        })
    }
}
#[cfg(test)]
mod tests {
    #[test]
    fn partial_fault_and_bounded_drain() {
        let frame = icem_protocols::ace::build([0; 6], 1, 2, &[0; 124]).to_vec();
        let mut calls = 0;
        let error = super::drain(|| {
            calls += 1;
            if calls == 4 {
                Err("fault".into())
            } else {
                Ok(Some(frame.clone()))
            }
        })
        .err()
        .unwrap_or_else(|| panic!("expected read fault"));
        assert_eq!(error.captured, 3);
        assert_eq!(
            super::drain(|| Ok(Some(frame.clone()))).unwrap_or_else(|e| panic!("{e:?}")),
            256
        );
    }
}
