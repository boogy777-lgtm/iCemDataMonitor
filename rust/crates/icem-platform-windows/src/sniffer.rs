//! Passive Host capture. Called only by the existing serialized Host owner.
//! No emitter, bus writer, frame queue or background task is reachable here.
use std::{collections::BTreeSet, time::Duration};

pub const MAX_PACKETS_PER_POLL: usize = 64;
pub const MAX_TRACKED_MACS: usize = 1024;
const RETRY: Duration = Duration::from_secs(30);

/// open/close are synchronous driver operations. next_packet MUST be nonblocking
/// and return an owned Ethernet frame of at most 65535 bytes. No borrowed ABI data.
pub trait CaptureDevice {
    fn open(&mut self, hint: Option<&str>) -> Result<String, String>;
    fn next_packet(&mut self) -> Result<Option<Vec<u8>>, String>;
    fn close(&mut self);
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum State {
    Starting,
    Running,
    Paused,
    Fault,
    Stopped,
}
impl State {
    pub fn name(self) -> &'static str {
        match self {
            Self::Starting => "STARTING",
            Self::Running => "RUNNING",
            Self::Paused => "PAUSED",
            Self::Fault => "FAULT",
            Self::Stopped => "STOPPED",
        }
    }
}
#[derive(Clone, Debug, PartialEq)]
pub struct Status {
    pub state: State,
    pub available: bool,
    /// Controller session plan. Legacy startup capture runs even when this is false (D06).
    pub enabled: bool,
    pub adapter: String,
    pub last_error: String,
    pub total_frames: u64,
    pub ace_frames: u64,
    pub distinct_macs: usize,
    pub mac_limit_reached: bool,
    pub frames_per_sec: f64,
    pub last_frame_age_sec: f64,
}
pub struct Capture<D: CaptureDevice> {
    device: D,
    hint: Option<String>,
    status: Status,
    retry_at: Duration,
    last_frame: Option<Duration>,
    window_at: Duration,
    window_frames: u64,
    macs: BTreeSet<[u8; 6]>,
    stopped: bool,
    now: Duration,
    processing: bool,
}
impl<D: CaptureDevice> Capture<D> {
    pub fn new(device: D, hint: Option<String>, enabled: bool) -> Self {
        Self {
            device,
            hint,
            status: Status {
                state: State::Starting,
                available: false,
                enabled,
                adapter: String::new(),
                last_error: String::new(),
                total_frames: 0,
                ace_frames: 0,
                distinct_macs: 0,
                mac_limit_reached: false,
                frames_per_sec: 0.,
                last_frame_age_sec: -1.,
            },
            retry_at: Duration::ZERO,
            last_frame: None,
            window_at: Duration::ZERO,
            window_frames: 0,
            macs: BTreeSet::new(),
            stopped: false,
            now: Duration::ZERO,
            processing: true,
        }
    }
    pub fn status(&self) -> Status {
        let mut status = self.status.clone();
        status.last_frame_age_sec = self
            .last_frame
            .map_or(-1., |at| self.now.saturating_sub(at).as_secs_f64());
        status.distinct_macs = self.macs.len();
        status
    }
    /// A failed operation does not change the processing plan. No mutation retry.
    pub fn toggle(&mut self) -> Result<(), String> {
        if self.stopped || !self.status.available {
            return Err("sniffer capture is unavailable".into());
        }
        self.status.enabled = !self.status.enabled;
        self.processing = self.status.enabled;
        self.status.state = if self.processing {
            State::Running
        } else {
            State::Paused
        };
        self.status.last_error.clear();
        self.status.frames_per_sec = 0.;
        self.window_frames = 0;
        self.window_at = self.now;
        Ok(())
    }
    /// At most 64 owned packets, with no wait for traffic. Host shutdown/commands
    /// regain control after each batch, including under a continuously busy NIC.
    pub fn poll(&mut self, now: Duration) {
        self.now = now;
        if self.stopped {
            return;
        }
        if !self.status.available {
            if now < self.retry_at {
                return;
            }
            match self.device.open(self.hint.as_deref()) {
                Ok(adapter) => {
                    self.status.adapter = adapter;
                    self.status.available = true;
                    self.status.last_error.clear();
                    self.status.state = if self.processing {
                        State::Running
                    } else {
                        State::Paused
                    };
                    self.window_at = now;
                }
                Err(error) => {
                    self.fail(error, now);
                    return;
                }
            }
        }
        for _ in 0..MAX_PACKETS_PER_POLL {
            match self.device.next_packet() {
                Ok(Some(frame)) if frame.len() <= 65535 => {
                    // Software mirror of the native ether multicast filter.
                    // Paused capture drains/discards; no application backlog.
                    if self.processing && frame.len() >= 14 && frame[0] & 1 != 0 {
                        self.observe(&frame, now);
                    }
                }
                Ok(Some(_)) => {
                    self.fail("capture frame exceeds 65535 bytes".into(), now);
                    return;
                }
                Ok(None) => break,
                Err(error) => {
                    self.fail(error, now);
                    return;
                }
            }
        }
        let elapsed = now.saturating_sub(self.window_at);
        if elapsed >= Duration::from_secs(1) {
            self.status.frames_per_sec = self.window_frames as f64 / elapsed.as_secs_f64();
            self.window_frames = 0;
            self.window_at = now;
        }
    }
    fn observe(&mut self, frame: &[u8], now: Duration) {
        self.status.total_frames = self.status.total_frames.saturating_add(1);
        self.window_frames = self.window_frames.saturating_add(1);
        self.last_frame = Some(now);
        // AceSnifferService.IsAceFrame is intentionally weaker than the emitter
        // codec predicate: exactly 152 bytes and LLC 03/03/08 or 09 only.
        if frame.len() == 152 && frame[14..16] == [3, 3] && matches!(frame[16], 8 | 9) {
            self.status.ace_frames = self.status.ace_frames.saturating_add(1);
            let mac = [frame[6], frame[7], frame[8], frame[9], frame[10], frame[11]];
            if self.macs.len() < MAX_TRACKED_MACS {
                self.macs.insert(mac);
            } else if !self.macs.contains(&mac) {
                self.status.mac_limit_reached = true;
            }
        }
    }
    fn fail(&mut self, error: String, now: Duration) {
        self.device.close();
        self.status.available = false;
        self.status.state = State::Fault;
        self.status.last_error = error;
        self.status.frames_per_sec = 0.;
        self.window_frames = 0;
        self.retry_at = now.saturating_add(RETRY);
    }
    pub fn stop(&mut self) {
        if !self.stopped {
            self.device.close();
            self.stopped = true;
            self.status.available = false;
            self.status.state = State::Stopped;
            self.status.frames_per_sec = 0.;
        }
    }
}
impl<D: CaptureDevice> Drop for Capture<D> {
    fn drop(&mut self) {
        self.stop();
    }
}

#[derive(Default)]
pub struct SystemCapture {
    #[cfg(windows)]
    session: Option<icem_windows_native::CaptureSession>,
}
impl CaptureDevice for SystemCapture {
    fn open(&mut self, hint: Option<&str>) -> Result<String, String> {
        self.close();
        #[cfg(windows)]
        {
            let pcap = icem_windows_native::Pcap::load()?;
            let adapters = pcap.adapters()?;
            let candidates: Vec<_> = adapters
                .iter()
                .map(|a| (a.name.as_str(), a.description.as_str()))
                .collect();
            let name = select_adapter(&candidates, hint)?;
            self.session = Some(pcap.open_capture(&name)?);
            Ok(name)
        }
        #[cfg(not(windows))]
        {
            let _ = hint;
            Err("Npcap capture requires Windows".into())
        }
    }
    fn next_packet(&mut self) -> Result<Option<Vec<u8>>, String> {
        #[cfg(windows)]
        {
            self.session
                .as_mut()
                .ok_or("capture is closed")?
                .next_packet()
        }
        #[cfg(not(windows))]
        {
            Err("Npcap capture requires Windows".into())
        }
    }
    fn close(&mut self) {
        #[cfg(windows)]
        {
            self.session = None;
        }
    }
}
/// Actual legacy auto-selection (despite its obsolete manual-only comment).
pub fn select_adapter(adapters: &[(&str, &str)], hint: Option<&str>) -> Result<String, String> {
    let hint = hint
        .filter(|s| !s.trim().is_empty())
        .map(str::to_ascii_lowercase);
    adapters
        .iter()
        .find(|(name, description)| {
            let desc = description.to_ascii_lowercase();
            match &hint {
                Some(hint) => {
                    name.to_ascii_lowercase().contains(hint)
                        || desc.contains(hint)
                        || desc.replace("(r)", "").replace("(tm)", "").contains(hint)
                }
                None => {
                    ["intel", "ethernet", "gigabit", "realtek", "broadcom"]
                        .iter()
                        .any(|s| desc.contains(s))
                        && !desc.contains("loopback")
                        && !desc.contains("wan miniport")
                }
            }
        })
        .map(|(name, _)| (*name).to_owned())
        .ok_or_else(|| "sniffer Ethernet adapter not found".into())
}
