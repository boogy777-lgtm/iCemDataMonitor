//! Bounded LAN transmission owner. Pacing waits never hold the engine gate.
//! Close/reopen interrupt the pending burst; no packet is sent after Close ACK.
use crate::transport::{PacketDevice, SystemPacket, burst_start_ms};
use icem_emulation::{lan::VectorData, runtime_ports::LanEmitter};
use std::{
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
const CONTROL_TIMEOUT: Duration = Duration::from_secs(3);
enum Message {
    Open(String, mpsc::SyncSender<Result<(), String>>),
    Close(mpsc::SyncSender<()>),
    Adapters(mpsc::SyncSender<Vec<String>>),
    Available(mpsc::SyncSender<bool>),
    Burst(Vec<VectorData>, u32),
}
struct Burst {
    vectors: Vec<VectorData>,
    equipment: u32,
    index: usize,
    due: Instant,
}
pub struct QueuedAceLanEmitter {
    sender: Option<mpsc::SyncSender<Message>>,
    worker: Option<JoinHandle<()>>,
    adapter: Option<String>,
    frames: Arc<AtomicU64>,
    busy: Arc<AtomicBool>,
}
impl QueuedAceLanEmitter {
    pub fn system() -> std::io::Result<Self> {
        Self::new(SystemPacket::default())
    }
    pub fn new<D: PacketDevice + 'static>(device: D) -> std::io::Result<Self> {
        Self::with_pacing(
            device,
            Duration::from_millis(938),
            Duration::from_millis(5),
            false,
        )
    }
    /// Test seam uses the same state machine with a shortened initial delay.
    #[doc(hidden)]
    pub fn with_test_pacing<D: PacketDevice + 'static>(
        device: D,
        start: Duration,
        interval: Duration,
    ) -> std::io::Result<Self> {
        Self::with_pacing(device, start, interval, true)
    }
    fn with_pacing<D: PacketDevice + 'static>(
        device: D,
        start: Duration,
        interval: Duration,
        test: bool,
    ) -> std::io::Result<Self> {
        let (sender, receiver) = mpsc::sync_channel(4);
        let frames = Arc::new(AtomicU64::new(0));
        let busy = Arc::new(AtomicBool::new(false));
        let worker_frames = frames.clone();
        let worker_busy = busy.clone();
        let worker = thread::Builder::new()
            .name("emulator-lan-send".into())
            .spawn(move || {
                run(
                    device,
                    receiver,
                    &worker_frames,
                    &worker_busy,
                    start,
                    interval,
                    test,
                );
            })?;
        Ok(Self {
            sender: Some(sender),
            worker: Some(worker),
            adapter: None,
            frames,
            busy,
        })
    }
    fn query<T>(&self, make: impl FnOnce(mpsc::SyncSender<T>) -> Message) -> Result<T, String> {
        let (reply, wait) = mpsc::sync_channel(1);
        self.sender
            .as_ref()
            .ok_or("LAN worker stopped")?
            .try_send(make(reply))
            .map_err(|e| e.to_string())?;
        wait.recv_timeout(CONTROL_TIMEOUT)
            .map_err(|e| format!("LAN worker: {e}"))
    }
    pub fn is_busy(&self) -> bool {
        self.busy.load(Ordering::Acquire)
    }
}
impl LanEmitter for QueuedAceLanEmitter {
    fn is_sending(&self) -> bool {
        self.is_busy()
    }
    fn open(&mut self, adapter: &str) -> Result<(), String> {
        self.adapter = None;
        self.query(|r| Message::Open(adapter.into(), r))??;
        self.adapter = Some(adapter.into());
        Ok(())
    }
    fn close(&mut self) {
        if let Err(error) = self.query(Message::Close) {
            eprintln!("LAN close: {error}");
        }
        self.adapter = None;
    }
    fn is_open(&self) -> bool {
        self.adapter.is_some()
    }
    fn adapter_name(&self) -> Option<&str> {
        self.adapter.as_deref()
    }
    fn frames_sent(&self) -> u64 {
        self.frames.load(Ordering::Acquire)
    }
    fn send_superframe(&mut self, vectors: &[VectorData], equipment: u32) -> Result<(), String> {
        if self.adapter.is_none() {
            return Err("LAN adapter is closed".into());
        }
        if vectors.is_empty() {
            return Ok(());
        }
        if vectors.len() > 4096 {
            return Err("LAN superframe exceeds worker capacity".into());
        }
        if self
            .busy
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err("previous LAN superframe is still in flight".into());
        }
        let result = self
            .sender
            .as_ref()
            .ok_or_else(|| "LAN worker stopped".to_owned())
            .and_then(|s| {
                s.try_send(Message::Burst(vectors.to_vec(), equipment))
                    .map_err(|e| e.to_string())
            });
        if result.is_err() {
            self.busy.store(false, Ordering::Release);
        }
        result
    }
    fn adapters(&self) -> Vec<String> {
        self.query(Message::Adapters).unwrap_or_default()
    }
    fn npcap_available(&self) -> bool {
        self.query(Message::Available).unwrap_or(false)
    }
}
impl Drop for QueuedAceLanEmitter {
    fn drop(&mut self) {
        // Disconnect wakes recv_timeout immediately, including during the 938 ms wait.
        self.sender.take();
        if let Some(worker) = self.worker.take()
            && worker.join().is_err()
        {
            eprintln!("LAN worker panicked");
        }
    }
}
fn run<D: PacketDevice>(
    mut device: D,
    receiver: mpsc::Receiver<Message>,
    frames: &AtomicU64,
    busy: &AtomicBool,
    start: Duration,
    interval: Duration,
    test: bool,
) {
    let mut burst: Option<Burst> = None;
    loop {
        let message = if let Some(current) = &burst {
            receiver.recv_timeout(current.due.saturating_duration_since(Instant::now()))
        } else {
            receiver
                .recv()
                .map_err(|_| mpsc::RecvTimeoutError::Disconnected)
        };
        match message {
            Ok(Message::Open(name, reply)) => {
                burst = None;
                busy.store(false, Ordering::Release);
                device.close();
                let result = device.open(&name);
                frames.store(0, Ordering::Release);
                let _ = reply.send(result);
            }
            Ok(Message::Close(reply)) => {
                burst = None;
                busy.store(false, Ordering::Release);
                device.close();
                let _ = reply.send(());
            }
            Ok(Message::Adapters(reply)) => {
                let _ = reply.send(device.adapters());
            }
            Ok(Message::Available(reply)) => {
                let _ = reply.send(device.npcap_available());
            }
            Ok(Message::Burst(vectors, equipment)) => {
                let delay = if test {
                    start
                } else {
                    Duration::from_millis(burst_start_ms(vectors.len()))
                };
                burst = Some(Burst {
                    vectors,
                    equipment,
                    index: 0,
                    due: Instant::now() + delay,
                });
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                if let Some(current) = &mut burst {
                    if let Some(mac) = device.mac() {
                        let vector = &current.vectors[current.index];
                        let packet = icem_protocols::ace::build(
                            mac,
                            current.equipment,
                            vector.sv_number,
                            &vector.payload,
                        );
                        match device.send(&packet) {
                            Ok(()) => {
                                frames.fetch_add(1, Ordering::AcqRel);
                            }
                            Err(error) => eprintln!("LAN send: {error}"),
                        }
                    }
                    current.index += 1;
                    if current.index == current.vectors.len() {
                        burst = None;
                        busy.store(false, Ordering::Release);
                    } else {
                        current.due = Instant::now() + interval;
                    }
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        }
    }
    device.close();
    busy.store(false, Ordering::Release);
}
