use icem_desktop::{Bridge, UiPort};
use serde_json::{Value, json};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicUsize, Ordering},
    mpsc,
};
use std::time::Duration;
const DEADLINE: Duration = Duration::from_secs(2);
struct Port {
    started: mpsc::Sender<()>,
    release: Option<Mutex<mpsc::Receiver<()>>>,
    finished: mpsc::Sender<()>,
    calls: Arc<AtomicUsize>,
}
impl UiPort for Port {
    fn emulator(
        &self,
        command: icem_emulation::control::Command,
    ) -> icem_emulation::control::Execution {
        use icem_emulation::control::{
            ControlResult, Execution,
            application::{ErrorCategory, Outcome},
        };
        match self.request(Value::Null) {
            Ok(_) => Execution {
                command: command.name(),
                result: ControlResult::success(None),
                outcome: Outcome::Applied,
                error: None,
            },
            Err(e) => Execution::rejected(command.name(), ErrorCategory::Internal, e),
        }
    }

    fn request(&self, _: Value) -> Result<Value, String> {
        self.calls.fetch_add(1, Ordering::AcqRel);
        self.started.send(()).map_err(|e| e.to_string())?;
        if let Some(release) = &self.release {
            release
                .lock()
                .map_err(|e| e.to_string())?
                .recv_timeout(DEADLINE)
                .map_err(|e| e.to_string())?;
        }
        Ok(json!({"ok":true}))
    }
}
impl Drop for Port {
    fn drop(&mut self) {
        let _ = self.finished.send(());
    }
}
#[test]
fn close_does_not_wait_for_inflight_io_and_never_executes_queued_mutations()
-> Result<(), Box<dyn std::error::Error>> {
    let (started, began) = mpsc::channel();
    let (release, gate) = mpsc::channel();
    let (finished, ended) = mpsc::channel();
    let calls = Arc::new(AtomicUsize::new(0));
    let (bridge, completion) = Bridge::start(Arc::new(Port {
        started,
        release: Some(Mutex::new(gate)),
        finished,
        calls: calls.clone(),
    }))?;
    bridge.send_emulator(icem_emulation::control::Command::SetEquipment(
        icem_emulation::control::application::Equipment(42),
    ))?;
    began.recv_timeout(DEADLINE)?;
    bridge.send(json!({"cmd":"local_logs","action":"clear"}))?;
    bridge.send_emulator(icem_emulation::control::Command::SetFirmware(true))?;
    let (closed, close_done) = mpsc::channel();
    let closer = std::thread::spawn(move || {
        drop(bridge);
        let _ = closed.send(());
    });
    // Backend stays blocked until AFTER UI disposal. Old join-on-close cannot pass.
    let result = close_done.recv_timeout(Duration::from_millis(500));
    release.send(())?;
    closer.join().map_err(|_| "close thread panicked")?;
    result?;
    completion.finish()?;
    ended.recv_timeout(DEADLINE)?;
    assert_eq!(calls.load(Ordering::Acquire), 1);
    Ok(())
}
#[test]
fn close_releases_response_backpressure_and_skips_later_commands()
-> Result<(), Box<dyn std::error::Error>> {
    let (started, began) = mpsc::channel();
    let (finished, ended) = mpsc::channel();
    let calls = Arc::new(AtomicUsize::new(0));
    let (bridge, completion) = Bridge::start(Arc::new(Port {
        started,
        release: None,
        finished,
        calls: calls.clone(),
    }))?;
    // Fill all 16 replies, then leave the worker trying to deliver reply 17.
    for _ in 0..17 {
        bridge.send(json!({"cmd":"status"}))?;
        began.recv_timeout(DEADLINE)?;
    }
    bridge.send(json!({"cmd":"local_logs","action":"clear"}))?;
    drop(bridge);
    completion.finish()?;
    ended.recv_timeout(DEADLINE)?;
    assert_eq!(calls.load(Ordering::Acquire), 17);
    Ok(())
}
#[test]
fn explicit_completion_releases_backpressure_even_if_a_view_handle_is_retained()
-> Result<(), Box<dyn std::error::Error>> {
    let (started, began) = mpsc::channel();
    let (finished, ended) = mpsc::channel();
    let calls = Arc::new(AtomicUsize::new(0));
    let (bridge, completion) = Bridge::start(Arc::new(Port {
        started,
        release: None,
        finished,
        calls: calls.clone(),
    }))?;
    for _ in 0..17 {
        bridge.send(json!({"cmd":"status"}))?;
        began.recv_timeout(DEADLINE)?;
    }
    let (done, stopped) = mpsc::channel();
    let closer = std::thread::spawn(move || {
        let _ = done.send(completion.finish());
    });
    stopped.recv_timeout(DEADLINE)??;
    closer.join().map_err(|_| "completion thread panicked")?;
    ended.recv_timeout(DEADLINE)?;
    assert!(
        bridge
            .send(json!({"cmd":"local_logs","action":"clear"}))
            .is_err()
    );
    assert_eq!(calls.load(Ordering::Acquire), 17);
    drop(bridge);
    Ok(())
}
