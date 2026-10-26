use icem_emulation::{lan::VectorData, runtime_ports::LanEmitter};
use icem_platform_windows::{PacketDevice, QueuedAceLanEmitter};
use std::{
    sync::{Arc, Mutex, mpsc},
    time::{Duration, Instant},
};
#[derive(Default)]
struct Trace {
    open: bool,
    packets: Vec<Vec<u8>>,
    closes: usize,
}
struct Device {
    trace: Arc<Mutex<Trace>>,
    sent: mpsc::Sender<usize>,
    fail_second: bool,
}
impl PacketDevice for Device {
    fn open(&mut self, _: &str) -> Result<(), String> {
        self.trace.lock().map_err(|e| e.to_string())?.open = true;
        Ok(())
    }
    fn close(&mut self) {
        let mut t = self
            .trace
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        t.open = false;
        t.closes += 1;
    }
    fn mac(&self) -> Option<[u8; 6]> {
        Some([1, 2, 3, 4, 5, 6])
    }
    fn send(&mut self, bytes: &[u8]) -> Result<(), String> {
        let mut t = self.trace.lock().map_err(|e| e.to_string())?;
        assert!(t.open);
        t.packets.push(bytes.to_vec());
        let n = t.packets.len();
        let _ = self.sent.send(n);
        if self.fail_second && n == 2 {
            Err("injected send failure".into())
        } else {
            Ok(())
        }
    }
    fn adapters(&self) -> Vec<String> {
        vec!["fake".into()]
    }
    fn npcap_available(&self) -> bool {
        true
    }
}
fn vectors() -> Vec<VectorData> {
    vec![
        VectorData {
            sv_number: 7,
            name: "a".into(),
            payload: [0; 124],
        },
        VectorData {
            sv_number: 8,
            name: "b".into(),
            payload: [1; 124],
        },
    ]
}
#[test]
fn control_and_close_interrupt_a_delayed_burst_and_drop_joins()
-> Result<(), Box<dyn std::error::Error>> {
    let trace = Arc::new(Mutex::new(Trace::default()));
    let (tx, rx) = mpsc::channel();
    let device = Device {
        trace: trace.clone(),
        sent: tx,
        fail_second: false,
    };
    let mut emitter =
        QueuedAceLanEmitter::with_test_pacing(device, Duration::from_secs(10), Duration::ZERO)?;
    emitter.open("fake")?;
    emitter.send_superframe(&vectors(), 12)?;
    assert!(emitter.is_busy());
    assert_eq!(emitter.frames_sent(), 0);
    // This query is serviced during pacing. An implementation that sleeps in send blocks/fails here.
    assert_eq!(emitter.adapters(), vec!["fake"]);
    assert!(emitter.npcap_available());
    assert!(emitter.send_superframe(&vectors(), 12).is_err());
    emitter.close();
    assert!(!emitter.is_busy());
    assert!(!emitter.is_open());
    assert!(rx.try_recv().is_err());
    emitter.open("fake")?;
    emitter.send_superframe(&vectors(), 12)?;
    drop(emitter);
    let t = trace.lock().map_err(|e| e.to_string())?;
    assert!(!t.open);
    assert!(t.packets.is_empty());
    Ok(())
}
#[test]
fn close_between_packets_cancels_rest_and_reopen_resets_success_counter()
-> Result<(), Box<dyn std::error::Error>> {
    let trace = Arc::new(Mutex::new(Trace::default()));
    let (tx, rx) = mpsc::channel();
    let mut emitter = QueuedAceLanEmitter::with_test_pacing(
        Device {
            trace: trace.clone(),
            sent: tx,
            fail_second: false,
        },
        Duration::ZERO,
        Duration::from_secs(10),
    )?;
    emitter.open("fake")?;
    emitter.send_superframe(&vectors(), 42)?;
    assert_eq!(rx.recv_timeout(Duration::from_secs(1))?, 1);
    emitter.close();
    assert_eq!(emitter.frames_sent(), 1);
    assert!(!emitter.is_busy());
    assert!(rx.try_recv().is_err());
    let t = trace.lock().map_err(|e| e.to_string())?;
    let expected = icem_protocols::ace::build([1, 2, 3, 4, 5, 6], 42, 7, &[0; 124]);
    assert_eq!(t.packets, vec![expected.to_vec()]);
    drop(t);
    emitter.open("fake")?;
    assert_eq!(emitter.frames_sent(), 0);
    drop(emitter);
    Ok(())
}
#[test]
fn only_successful_packets_are_counted_and_backlog_is_bounded()
-> Result<(), Box<dyn std::error::Error>> {
    let trace = Arc::new(Mutex::new(Trace::default()));
    let (tx, rx) = mpsc::channel();
    let mut emitter = QueuedAceLanEmitter::with_test_pacing(
        Device {
            trace: trace.clone(),
            sent: tx,
            fail_second: true,
        },
        Duration::ZERO,
        Duration::ZERO,
    )?;
    emitter.open("fake")?;
    emitter.send_superframe(&vectors(), 42)?;
    rx.recv_timeout(Duration::from_secs(1))?;
    rx.recv_timeout(Duration::from_secs(1))?;
    let limit = Instant::now() + Duration::from_secs(1);
    while emitter.is_busy() && Instant::now() < limit {
        std::thread::yield_now();
    }
    assert!(!emitter.is_busy());
    assert_eq!(emitter.frames_sent(), 1);
    let too_big = vec![vectors()[0].clone(); 4097];
    assert!(emitter.send_superframe(&too_big, 42).is_err());
    assert!(!emitter.is_busy());
    Ok(())
}
