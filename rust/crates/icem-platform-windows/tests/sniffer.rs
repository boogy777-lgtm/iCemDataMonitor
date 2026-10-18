use icem_platform_windows::sniffer::{
    Capture, CaptureDevice, MAX_PACKETS_PER_POLL, MAX_TRACKED_MACS, State, select_adapter,
};
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
    time::Duration,
};
type TestResult = Result<(), Box<dyn std::error::Error>>;
#[derive(Default)]
struct Trace {
    opens: usize,
    closes: usize,
    reads: usize,
    fail_open: bool,
    repeat: bool,
    frames: VecDeque<Result<Option<Vec<u8>>, String>>,
}
struct Device(Arc<Mutex<Trace>>);
impl CaptureDevice for Device {
    fn open(&mut self, _: Option<&str>) -> Result<String, String> {
        let mut t = self.0.lock().map_err(|_| "trace poisoned")?;
        t.opens += 1;
        if t.fail_open {
            Err("AccessDenied".into())
        } else {
            Ok("ethernet-test".into())
        }
    }
    fn next_packet(&mut self) -> Result<Option<Vec<u8>>, String> {
        let mut t = self.0.lock().map_err(|_| "trace poisoned")?;
        t.reads += 1;
        t.frames.pop_front().unwrap_or_else(|| {
            if t.repeat {
                Ok(Some(frame(8, 0)))
            } else {
                Ok(None)
            }
        })
    }
    fn close(&mut self) {
        if let Ok(mut t) = self.0.lock() {
            t.closes += 1;
        }
    }
}
fn frame(control: u8, mac: usize) -> Vec<u8> {
    let mut f = icem_protocols::ace::build([0; 6], 17, 100, &[0; 124]).to_vec();
    f[6..12].copy_from_slice(&(mac as u64).to_be_bytes()[2..]);
    f[16] = control;
    f
}
#[test]
fn legacy_witness_predicate_counters_age_rate_and_pause() -> TestResult {
    let t = Arc::new(Mutex::new(Trace::default()));
    let mut capture = Capture::new(Device(t.clone()), None, true);
    assert_eq!(capture.status().state, State::Starting);
    assert!(!capture.status().available);
    assert_eq!(capture.status().last_frame_age_sec, -1.);
    assert!(capture.toggle().is_err());
    let mut weak = frame(9, 2);
    weak[18] = 0; // witness accepts, emitter parser refuses
    assert!(icem_protocols::ace::parse(&weak).is_none());
    let mut unicast = frame(8, 3);
    unicast[0] = 2;
    let mut long = frame(8, 3);
    long.push(0);
    t.lock().map_err(|_| "trace")?.frames.extend(
        [frame(8, 1), weak, long, vec![3], unicast]
            .into_iter()
            .map(|f| Ok(Some(f))),
    );
    capture.poll(Duration::ZERO);
    let s = capture.status();
    assert_eq!((s.total_frames, s.ace_frames, s.distinct_macs), (3, 2, 2));
    assert_eq!(s.state, State::Running);
    capture.poll(Duration::from_secs(1));
    assert_eq!(capture.status().frames_per_sec, 3.);
    assert_eq!(capture.status().last_frame_age_sec, 1.);
    capture.poll(Duration::from_secs(2));
    assert_eq!(capture.status().frames_per_sec, 0.);
    capture.toggle()?;
    assert!(capture.status().available);
    assert_eq!(capture.status().state, State::Paused);
    t.lock()
        .map_err(|_| "trace")?
        .frames
        .push_back(Ok(Some(frame(8, 4))));
    capture.poll(Duration::from_secs(3));
    assert_eq!(capture.status().total_frames, 3);
    capture.toggle()?;
    capture.poll(Duration::from_secs(4));
    assert_eq!(capture.status().total_frames, 3); // discarded paused data stays discarded
    capture.stop();
    capture.stop();
    capture.poll(Duration::from_secs(99));
    assert!(capture.toggle().is_err());
    assert_eq!(capture.status().state, State::Stopped);
    drop(capture);
    let t = t.lock().map_err(|_| "trace")?;
    assert_eq!((t.opens, t.closes), (1, 1));
    Ok(())
}
#[test]
fn bounded_busy_poll_fault_retry_and_retained_pause_plan() -> TestResult {
    let t = Arc::new(Mutex::new(Trace {
        repeat: true,
        ..Trace::default()
    }));
    let mut capture = Capture::new(Device(t.clone()), None, true);
    capture.poll(Duration::ZERO);
    assert_eq!(t.lock().map_err(|_| "trace")?.reads, MAX_PACKETS_PER_POLL);
    assert_eq!(capture.status().total_frames, MAX_PACKETS_PER_POLL as u64);
    capture.toggle()?;
    t.lock()
        .map_err(|_| "trace")?
        .frames
        .push_back(Err("adapter removed / EOF".into()));
    capture.poll(Duration::from_secs(1));
    assert_eq!(capture.status().state, State::Fault);
    assert!(!capture.status().available);
    assert!(!capture.status().enabled);
    assert_eq!(capture.status().last_error, "adapter removed / EOF");
    capture.poll(Duration::from_secs(30));
    assert_eq!(t.lock().map_err(|_| "trace")?.opens, 1);
    capture.poll(Duration::from_secs(31));
    assert_eq!(t.lock().map_err(|_| "trace")?.opens, 2);
    assert_eq!(capture.status().state, State::Paused);
    assert_eq!(capture.status().total_frames, MAX_PACKETS_PER_POLL as u64);
    Ok(())
}
#[test]
fn open_failure_and_oversized_frame_fail_closed_without_retrying_toggle() -> TestResult {
    let t = Arc::new(Mutex::new(Trace {
        fail_open: true,
        ..Trace::default()
    }));
    let mut capture = Capture::new(Device(t.clone()), None, true);
    capture.poll(Duration::ZERO);
    for second in 1..30 {
        capture.poll(Duration::from_secs(second));
    }
    assert_eq!(t.lock().map_err(|_| "trace")?.opens, 1);
    assert_eq!(capture.status().last_error, "AccessDenied");
    assert!(capture.toggle().is_err());
    {
        let mut t = t.lock().map_err(|_| "trace")?;
        t.fail_open = false;
        t.frames.push_back(Ok(Some(vec![3; 65536])));
    }
    capture.poll(Duration::from_secs(30));
    assert!(!capture.status().available);
    assert_eq!(capture.status().total_frames, 0);
    assert_eq!(t.lock().map_err(|_| "trace")?.closes, 2);
    Ok(())
}
#[test]
fn mac_cardinality_is_bounded_and_overflow_is_explicit() -> TestResult {
    let t = Arc::new(Mutex::new(Trace::default()));
    t.lock()
        .map_err(|_| "trace")?
        .frames
        .extend((0..MAX_TRACKED_MACS + 2).map(|i| Ok(Some(frame(8, i)))));
    let mut capture = Capture::new(Device(t), None, true);
    for second in 0..20 {
        capture.poll(Duration::from_secs(second));
    }
    let s = capture.status();
    assert_eq!(s.ace_frames, (MAX_TRACKED_MACS + 2) as u64);
    assert_eq!(s.distinct_macs, MAX_TRACKED_MACS);
    assert!(s.mac_limit_reached);
    Ok(())
}
#[test]
fn adapter_selection_follows_actual_legacy_code() -> TestResult {
    let adapters = [
        ("loop", "Intel Loopback"),
        ("wan", "Ethernet WAN Miniport"),
        ("nic", "Intel(R) Ethernet"),
        ("second", "Realtek"),
    ];
    assert_eq!(select_adapter(&adapters, None)?, "nic");
    assert_eq!(select_adapter(&adapters, Some("intel ethernet"))?, "nic");
    assert_eq!(select_adapter(&adapters, Some("second"))?, "second");
    assert_eq!(select_adapter(&adapters, Some("Intel(R)"))?, "nic");
    assert!(select_adapter(&adapters, Some("missing")).is_err());
    assert!(select_adapter(&[], None).is_err());
    Ok(())
}
#[test]
fn legacy_startup_plan_is_distinct_from_actual_capture_gate_d06() -> TestResult {
    let trace = Arc::new(Mutex::new(Trace::default()));
    let mut capture = Capture::new(Device(trace), None, false);
    capture.poll(Duration::ZERO);
    assert_eq!(capture.status().state, State::Running);
    assert!(!capture.status().enabled);
    capture.toggle()?; // controller toggles false -> true; capture remains running
    assert!(capture.status().enabled);
    assert_eq!(capture.status().state, State::Running);
    capture.toggle()?;
    assert!(!capture.status().enabled);
    assert_eq!(capture.status().state, State::Paused);
    Ok(())
}
