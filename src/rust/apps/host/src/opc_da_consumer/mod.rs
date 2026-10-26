//! Child-process ownership isolates COM and blocking pipes from acquisition.
mod config;
use crate::consumers::{Lifecycle, Status};
pub use config::Config;
use icem_core::publication::SnapshotReader;
use icem_opc_da_codec::{Frame, READY, write_frame};
use std::{
    io::Read,
    process::{Command, Stdio},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
struct RunningGuard(Arc<AtomicBool>);
impl Drop for RunningGuard {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}
pub struct Sidecar {
    child: Arc<Mutex<std::process::Child>>,
    cancel: Arc<AtomicBool>,
    alive: Arc<AtomicBool>,
    running: Arc<AtomicBool>,
    done: mpsc::Receiver<Result<(), String>>,
    worker: Option<JoinHandle<()>>,
    waited: bool,
}
pub fn start(config: Config, reader: SnapshotReader) -> Result<Sidecar, String> {
    if !cfg!(windows) {
        return Err("OPC DA requires Windows".into());
    }
    let mut command = Command::new(config.sidecar);
    command.arg("--ipc-v1");
    spawn(command, reader, config.interval, config.watchdog)
}
fn spawn(
    mut command: Command,
    reader: SnapshotReader,
    interval: Duration,
    watchdog: Duration,
) -> Result<Sidecar, String> {
    let mut process = command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::inherit())
        .spawn()
        .map_err(|e| format!("OPC DA spawn: {e}"))?;
    let Some(mut input) = process.stdin.take() else {
        let _ = process.kill();
        let _ = process.wait();
        return Err("OPC DA stdin unavailable".into());
    };
    let Some(mut output) = process.stdout.take() else {
        let _ = process.kill();
        let _ = process.wait();
        return Err("OPC DA stdout unavailable".into());
    };
    let child = Arc::new(Mutex::new(process));
    let cancel = Arc::new(AtomicBool::new(false));
    let alive = Arc::new(AtomicBool::new(true));
    let running = Arc::new(AtomicBool::new(false));
    let finished = Arc::new(AtomicBool::new(false));
    let heartbeat = Arc::new(Mutex::new(Instant::now()));
    let (completed, done) = mpsc::sync_channel(1);
    let (ready_send, ready) = mpsc::sync_channel(1);
    let watch_cancel = cancel.clone();
    let watch_finished = finished.clone();
    let watch_beat = heartbeat.clone();
    let watch_child = child.clone();
    let watch_running = running.clone();
    let monitor = thread::Builder::new()
        .name("opc-da-watchdog".into())
        .spawn(move || {
            let _running_guard = RunningGuard(watch_running);
            loop {
                let expired = watch_beat.lock().map_or(true, |beat| {
                    Instant::now()
                        .checked_duration_since(*beat)
                        .is_none_or(|age| age >= watchdog)
                });
                let stop = watch_cancel.load(Ordering::Acquire)
                    || watch_finished.load(Ordering::Acquire)
                    || expired;
                let mut child = watch_child
                    .lock()
                    .map_err(|_| "OPC DA child poisoned".to_owned())?;
                if let Some(status) = child.try_wait().map_err(|e| e.to_string())? {
                    return if stop || status.success() {
                        Ok(())
                    } else {
                        Err(format!("OPC DA child exited: {status}"))
                    };
                }
                if stop {
                    child.kill().map_err(|e| format!("OPC DA kill: {e}"))?;
                    child.wait().map_err(|e| format!("OPC DA reap: {e}"))?;
                    return if expired && !watch_cancel.load(Ordering::Acquire) {
                        Err("OPC DA watchdog expired".into())
                    } else {
                        Ok(())
                    };
                }
                drop(child);
                thread::sleep(Duration::from_millis(10));
            }
        })
        .map_err(|e| {
            if let Ok(mut child) = child.lock() {
                let _ = child.kill();
                let _ = child.wait();
            }
            format!("OPC DA watchdog spawn: {e}")
        })?;
    let io_cancel = cancel.clone();
    let io_alive = alive.clone();
    let io_running = running.clone();
    let io_finished = finished.clone();
    let worker_result = thread::Builder::new()
        .name("opc-da-ipc".into())
        .spawn(move || {
            let result = (|| -> Result<(), String> {
                let mut ack = [0; 8];
                output
                    .read_exact(&mut ack)
                    .map_err(|e| format!("OPC DA ready: {e}"))?;
                if &ack != READY {
                    return Err("OPC DA invalid ready version".into());
                }
                io_running.store(true, Ordering::Release);
                let _ = ready_send.send(());
                let mut sequence = 0u64;
                while !io_cancel.load(Ordering::Acquire) {
                    sequence = sequence.checked_add(1).ok_or("OPC DA sequence exhausted")?;
                    let state = reader
                        .latest()
                        .map_err(|e| format!("OPC DA publication: {e:?}"))?;
                    // Only one encoded frame exists; a stalled child cannot queue snapshots.
                    *heartbeat.lock().map_err(|_| "OPC DA heartbeat poisoned")? = Instant::now();
                    // READY/previous ACK grants receiver credit before this age sample.
                    let frame = Frame::from_publication(state, sequence, Instant::now());
                    write_frame(&mut input, &frame).map_err(|e| format!("OPC DA write: {e}"))?;
                    output
                        .read_exact(&mut ack)
                        .map_err(|e| format!("OPC DA ack: {e}"))?;
                    if u64::from_le_bytes(ack) != sequence {
                        return Err("OPC DA sequence mismatch".into());
                    }
                    // Idle interval is cancellable and does not consume watchdog budget.
                    let until = Instant::now() + interval;
                    while !io_cancel.load(Ordering::Acquire) && Instant::now() < until {
                        *heartbeat.lock().map_err(|_| "OPC DA heartbeat poisoned")? =
                            Instant::now();
                        thread::sleep(Duration::from_millis(10));
                    }
                }
                Ok(())
            })();
            io_finished.store(true, Ordering::Release);
            drop(input);
            drop(output);
            let watched = monitor
                .join()
                .map_err(|_| "OPC DA watchdog panicked".into())
                .and_then(|r| r);
            io_running.store(false, Ordering::Release);
            io_alive.store(false, Ordering::Release);
            let result = if io_cancel.load(Ordering::Acquire) {
                watched
            } else {
                result.and(watched)
            };
            let _ = completed.send(result);
        });
    let worker = match worker_result {
        Ok(worker) => worker,
        Err(error) => {
            cancel.store(true, Ordering::Release);
            return Err(format!("OPC DA worker spawn: {error}"));
        }
    };
    let mut handle = Sidecar {
        child,
        cancel,
        alive,
        running,
        done,
        worker: Some(worker),
        waited: false,
    };
    if ready
        .recv_timeout(watchdog + Duration::from_millis(200))
        .is_err()
    {
        let _ = handle.stop(Instant::now() + Duration::from_secs(1));
        return Err("OPC DA startup handshake failed".into());
    }
    Ok(handle)
}
impl Lifecycle for Sidecar {
    fn status(&self) -> Status {
        if self.cancel.load(Ordering::Acquire) {
            if self.alive.load(Ordering::Acquire) {
                Status::Stopping
            } else {
                Status::Stopped
            }
        } else if self.running.load(Ordering::Acquire) && self.alive.load(Ordering::Acquire) {
            Status::Running
        } else {
            Status::Faulted
        }
    }
    fn cancel(&mut self) -> Result<(), String> {
        self.cancel.store(true, Ordering::Release);
        // Retain ownership in the handle even if the monitor encounters an OS error.
        let mut child = self.child.lock().map_err(|_| "OPC DA child poisoned")?;
        if child.try_wait().map_err(|e| e.to_string())?.is_none() {
            child
                .kill()
                .map_err(|e| format!("OPC DA cancel kill: {e}"))?;
        }
        Ok(())
    }

    fn stop(&mut self, deadline: Instant) -> Result<(), String> {
        self.cancel()?;
        self.waited = true;
        if self.worker.is_none() {
            return Ok(());
        }
        let result = self
            .done
            .recv_timeout(deadline.saturating_duration_since(Instant::now()))
            .map_err(|e| format!("OPC DA shutdown deadline: {e}"))?;
        if let Some(worker) = self.worker.take() {
            worker
                .join()
                .map_err(|_| "OPC DA worker panicked".to_owned())?;
        }
        result
    }
}
impl Drop for Sidecar {
    fn drop(&mut self) {
        if self.waited {
            let _ = self.cancel();
        } else {
            let _ = self.stop(Instant::now() + Duration::from_secs(2));
        }
    }
}
#[cfg(all(test, unix))]
mod tests {
    use super::*;
    fn helper(code: &str) -> Command {
        let mut command = Command::new("python3");
        command.args(["-u", "-c", code]);
        command
    }
    #[test]
    fn hung_child_is_killed_and_shutdown_unblocks_pipe() -> Result<(), String> {
        let (_, reader) = icem_core::publication::latest_snapshot();
        let mut sidecar = spawn(
            helper(
                "import sys,time;sys.stdout.buffer.write(b'ICDAv001');sys.stdout.buffer.flush();time.sleep(60)",
            ),
            reader,
            Duration::from_millis(50),
            Duration::from_millis(200),
        )?;
        let deadline = Instant::now() + Duration::from_secs(2);
        while sidecar.alive.load(Ordering::Acquire) && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(sidecar.status(), Status::Faulted);
        assert!(sidecar.stop(deadline).is_err());
        sidecar.stop(deadline)?;
        Ok(())
    }
    #[test]
    fn latest_only_real_pipe_ack_and_deadline() -> Result<(), String> {
        let (_, reader) = icem_core::publication::latest_snapshot();
        let code = "import sys,struct\nr=sys.stdin.buffer;w=sys.stdout.buffer\nw.write(b'ICDAv001');w.flush()\nwhile True:\n h=r.read(4)\n if not h:break\n n=struct.unpack('<I',h)[0];b=r.read(n);w.write(b[8:16]);w.flush()";
        let mut sidecar = spawn(
            helper(code),
            reader,
            Duration::from_millis(50),
            Duration::from_millis(500),
        )?;
        assert_eq!(sidecar.status(), Status::Running);
        thread::sleep(Duration::from_millis(140));
        sidecar.stop(Instant::now() + Duration::from_secs(1))?;
        assert_eq!(sidecar.status(), Status::Stopped);
        sidecar.stop(Instant::now())?;
        let (_, reader) = icem_core::publication::latest_snapshot();
        assert!(
            spawn(
                helper("import sys;sys.stdout.buffer.write(b'badready');sys.stdout.buffer.flush()"),
                reader,
                Duration::from_millis(50),
                Duration::from_millis(200)
            )
            .is_err()
        );
        Ok(())
    }
    #[test]
    fn blocked_large_write_is_killed_and_acquisition_reader_stays_independent() -> Result<(), String>
    {
        use icem_core::{Freshness, SnapshotOrigin, SnapshotRead};
        let (mut publisher, reader) = icem_core::publication::latest_snapshot();
        let mut bytes = include_bytes!("../../../../tests/fixtures/bus-v5-minimal.bin").to_vec();
        bytes[8..10].copy_from_slice(&u16::MAX.to_le_bytes());
        bytes.resize(64 + usize::from(u16::MAX) * 2, 0);
        let snapshot = Arc::new(icem_bus::Snapshot::decode(&bytes).map_err(|e| e.to_string())?);
        let now = Instant::now();
        publisher
            .publish(
                Ok(SnapshotRead {
                    snapshot: snapshot.clone(),
                    freshness: Freshness::Fresh,
                    origin: SnapshotOrigin::Captured,
                }),
                now,
                now,
                None,
            )
            .map_err(|e| format!("{e:?}"))?;
        let independent = reader.clone();
        let mut child = spawn(
            helper(
                "import sys,time;sys.stdout.buffer.write(b'ICDAv001');sys.stdout.buffer.flush();time.sleep(60)",
            ),
            reader,
            Duration::from_millis(50),
            Duration::from_millis(200),
        )?;
        for _ in 0..2000 {
            assert!(
                independent
                    .latest()
                    .map_err(|e| format!("{e:?}"))?
                    .snapshot
                    .is_some_and(|s| Arc::ptr_eq(&s, &snapshot))
            );
        }
        let deadline = Instant::now() + Duration::from_secs(2);
        while child.alive.load(Ordering::Acquire) && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(child.status(), Status::Faulted);
        assert!(child.stop(deadline).is_err());
        Ok(())
    }
}
