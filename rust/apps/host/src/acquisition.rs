//! Sole physical source owner. Source and its native handles never leave this thread.
use std::sync::{Arc, Condvar, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use icem_core::publication::{
    PublishedSnapshot, SnapshotPublisher, SnapshotReader, latest_snapshot,
};
use icem_core::{Freshness, SnapshotOrigin, SnapshotRead, SnapshotSource, SourceError};
use icem_web::BusReply;

pub const PERIOD: Duration = Duration::from_millis(200);
pub const STOP_DEADLINE: Duration = Duration::from_secs(2);

/// Missed slots are skipped; a slow physical read cannot trigger catch-up reads.
struct Schedule {
    due: Instant,
}
impl Schedule {
    fn new(now: Instant) -> Self {
        Self { due: now }
    }
    fn completed(&mut self, now: Instant) {
        self.due += PERIOD;
        if self.due <= now {
            self.due = now + PERIOD;
        }
    }
}

#[derive(Clone, Copy, Default)]
struct Compatibility {
    freshness: Option<Freshness>,
    is_open: bool,
    error: Option<SourceError>,
}

/// Read-only capability; the compatibility lock keeps source facts and publication
/// coherent. It is never held during source I/O or consumer processing.
#[derive(Clone)]
pub struct Reader {
    publication: SnapshotReader,
    compatibility: Arc<Mutex<Compatibility>>,
}
impl Reader {
    fn state(&self) -> Result<(PublishedSnapshot, Compatibility), String> {
        let facts = self
            .compatibility
            .lock()
            .map_err(|_| "acquisition lock poisoned")?;
        let state = self
            .publication
            .latest()
            .map_err(|e| format!("publication: {e:?}"))?;
        Ok((state, *facts))
    }

    // Temporary external-facade bridge. P4 replaces owned reply with Arc projection.
    pub fn reply(&self) -> Result<(BusReply, Option<SourceError>), String> {
        let (state, facts) = self.state()?;
        let read = facts.freshness.and_then(|freshness| {
            state.snapshot.map(|snapshot| SnapshotRead {
                snapshot: (*snapshot).clone(),
                freshness,
                origin: match state.status {
                    icem_core::publication::CaptureStatus::Captured => SnapshotOrigin::Captured,
                    _ => SnapshotOrigin::Retained,
                },
            })
        });
        Ok((
            BusReply {
                read,
                is_open: facts.is_open,
            },
            facts.error,
        ))
    }
}

pub struct Acquisition {
    reader: Reader,
    cancel: Arc<(Mutex<bool>, Condvar)>,
    done: mpsc::Receiver<Result<(), String>>,
    worker: Option<JoinHandle<()>>,
    shutdown_waited: bool,
}
impl Acquisition {
    /// Only the factory crosses threads. Its product need not implement Send.
    pub fn start<F>(factory: F) -> Result<Self, String>
    where
        F: FnOnce() -> Result<Box<dyn SnapshotSource>, String> + Send + 'static,
    {
        let (publisher, publication) = latest_snapshot();
        let compatibility = Arc::new(Mutex::new(Compatibility::default()));
        let reader = Reader {
            publication,
            compatibility: Arc::clone(&compatibility),
        };
        let cancel = Arc::new((Mutex::new(false), Condvar::new()));
        let cancellation = Arc::clone(&cancel);
        let (completed, done) = mpsc::sync_channel(1);
        let (ready, initialized) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("bus-acquisition".into())
            .spawn(move || {
                let result = match factory() {
                    Ok(mut source) => {
                        let _ = ready.send(Ok(()));
                        acquire(&mut *source, publisher, &compatibility, &cancellation)
                    }
                    Err(error) => {
                        let _ = ready.send(Err(error.clone()));
                        Err(error)
                    }
                };
                let _ = completed.send(result);
            })
            .map_err(|e| e.to_string())?;
        let mut owner = Self {
            reader,
            cancel,
            done,
            worker: Some(worker),
            shutdown_waited: false,
        };
        match initialized.recv_timeout(STOP_DEADLINE) {
            Ok(Ok(())) => Ok(owner),
            Ok(Err(error)) => {
                let _ = owner.stop(STOP_DEADLINE);
                Err(error)
            }
            Err(error) => {
                owner.request_stop();
                Err(format!("acquisition startup deadline: {error}"))
            }
        }
    }
    pub fn reader(&self) -> Reader {
        self.reader.clone()
    }

    pub fn stop(&mut self, deadline: Duration) -> Result<(), String> {
        self.request_stop();
        self.shutdown_waited = true;
        if self.worker.is_none() {
            return Ok(());
        }
        let result = self
            .done
            .recv_timeout(deadline)
            .map_err(|e| format!("acquisition shutdown deadline: {e}"))?;
        if let Some(worker) = self.worker.take() {
            worker
                .join()
                .map_err(|_| "acquisition worker panicked".to_owned())?;
        }
        result
    }
    fn request_stop(&self) {
        let (lock, wake) = &*self.cancel;
        if let Ok(mut stop) = lock.lock() {
            *stop = true;
        }
        wake.notify_all();
    }
}
impl Drop for Acquisition {
    fn drop(&mut self) {
        if self.worker.is_some() && !self.shutdown_waited {
            // Bounded even during error unwinding. Native handles remain worker-owned;
            // a timed-out worker exits after its in-flight bounded native call.
            let _ = self.stop(STOP_DEADLINE);
        }
    }
}
fn acquire(
    source: &mut dyn SnapshotSource,
    mut publisher: SnapshotPublisher,
    compatibility: &Mutex<Compatibility>,
    cancel: &(Mutex<bool>, Condvar),
) -> Result<(), String> {
    let mut schedule = Schedule::new(Instant::now());
    loop {
        let (lock, wake) = cancel;
        let mut stop = lock
            .lock()
            .map_err(|_| "acquisition cancellation lock poisoned")?;
        while !*stop && Instant::now() < schedule.due {
            let remaining = schedule.due.saturating_duration_since(Instant::now());
            stop = wake
                .wait_timeout(stop, remaining)
                .map_err(|_| "acquisition cancellation lock poisoned")?
                .0;
        }
        if *stop {
            return Ok(());
        }
        drop(stop);
        let read = source.snapshot();
        // Successful capture time is observed after copy/decode, never before a
        // potentially 500ms mutex wait. Retained/error never advance it in core.
        let completed_at = Instant::now();
        let error = source.last_error().or_else(|| read.as_ref().err().copied());
        let facts = Compatibility {
            freshness: read.as_ref().ok().map(|read| read.freshness),
            is_open: source.is_open(),
            error,
        };
        {
            let mut compatibility = compatibility
                .lock()
                .map_err(|_| "acquisition lock poisoned")?;
            publisher
                .publish(read, completed_at, completed_at, error)
                .map_err(|e| format!("publication: {e:?}"))?;
            *compatibility = facts;
        }
        schedule.completed(completed_at);
    }
}

#[cfg(test)]
#[allow(clippy::unwrap_used)]
mod tests {
    use super::*;
    use icem_core::publication::CaptureStatus;
    use std::rc::Rc;
    use std::sync::atomic::{AtomicUsize, Ordering};
    const FIXTURE: &[u8] = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin");
    fn read(origin: SnapshotOrigin) -> SnapshotRead {
        SnapshotRead {
            snapshot: icem_bus::Snapshot::decode(FIXTURE).unwrap(),
            freshness: Freshness::Fresh,
            origin,
        }
    }
    fn until(mut condition: impl FnMut() -> bool) {
        let deadline = Instant::now() + Duration::from_secs(3);
        while !condition() {
            assert!(Instant::now() < deadline, "test deadline expired");
            thread::sleep(Duration::from_millis(2));
        }
    }
    #[test]
    fn injected_schedule_skips_slow_read_slots_without_catchup() {
        let base = Instant::now();
        let mut schedule = Schedule::new(base);
        assert_eq!(schedule.due, base);
        schedule.completed(base + Duration::from_millis(10));
        assert_eq!(schedule.due, base + PERIOD);
        schedule.completed(base + Duration::from_millis(700));
        assert_eq!(schedule.due, base + Duration::from_millis(900));
        schedule.completed(base + Duration::from_millis(901));
        assert_eq!(schedule.due, base + Duration::from_millis(1100));
    }
    struct NonSendSource {
        calls: Arc<AtomicUsize>,
        _native_thread_marker: Rc<()>,
    }
    impl SnapshotSource for NonSendSource {
        fn snapshot(&mut self) -> Result<SnapshotRead, SourceError> {
            match self.calls.fetch_add(1, Ordering::SeqCst) {
                0 => Err(SourceError::Unavailable),
                1 => Ok(read(SnapshotOrigin::Retained)),
                2 => Ok(read(SnapshotOrigin::Captured)),
                3 => Ok(read(SnapshotOrigin::Retained)),
                _ => Err(SourceError::LockTimeout),
            }
        }
    }
    #[test]
    fn requests_and_slow_consumer_do_not_drive_source_or_refresh_retained_time() {
        let calls = Arc::new(AtomicUsize::new(0));
        let count = Arc::clone(&calls);
        let mut owner = Acquisition::start(move || {
            Ok(Box::new(NonSendSource {
                calls: count,
                _native_thread_marker: Rc::new(()),
            }))
        })
        .unwrap();
        let reader = owner.reader();
        until(|| {
            reader.state().unwrap().0.status == CaptureStatus::Failed(SourceError::Unavailable)
        });
        assert!(reader.state().unwrap().0.snapshot.is_none());
        until(|| {
            matches!(
                reader.state().unwrap().0.status,
                CaptureStatus::Retained { .. }
            )
        });
        assert!(reader.state().unwrap().0.snapshot.is_none());
        until(|| reader.state().unwrap().0.capture_generation == 1);
        let old = reader.state().unwrap().0;
        let held = old.snapshot.clone().unwrap();
        let before = calls.load(Ordering::SeqCst);
        for _ in 0..500 {
            let _ = reader.reply().unwrap();
        }
        assert!(calls.load(Ordering::SeqCst) <= before + 1);
        until(|| {
            reader.state().unwrap().0.status == CaptureStatus::Failed(SourceError::LockTimeout)
        });
        let failed = reader.state().unwrap().0;
        assert_eq!(failed.capture_generation, old.capture_generation);
        assert_eq!(failed.captured_at_monotonic, old.captured_at_monotonic);
        assert!(Arc::ptr_eq(&held, failed.snapshot.as_ref().unwrap()));
        assert!(failed.completed_at_monotonic > old.completed_at_monotonic);
        owner.stop(Duration::from_secs(1)).unwrap();
        let stopped = calls.load(Ordering::SeqCst);
        thread::sleep(PERIOD);
        assert_eq!(calls.load(Ordering::SeqCst), stopped);
    }
    #[test]
    fn initial_absence_and_cancellation_interrupt_wait_not_native_call() {
        let (entered, started) = mpsc::sync_channel(1);
        let (release, released) = mpsc::sync_channel(1);
        struct Blocked {
            entered: mpsc::SyncSender<()>,
            release: mpsc::Receiver<()>,
        }
        impl SnapshotSource for Blocked {
            fn snapshot(&mut self) -> Result<SnapshotRead, SourceError> {
                self.entered.send(()).unwrap();
                self.release.recv().unwrap();
                Ok(read(SnapshotOrigin::Captured))
            }
        }
        let mut owner = Acquisition::start(move || {
            Ok(Box::new(Blocked {
                entered,
                release: released,
            }))
        })
        .unwrap();
        started.recv_timeout(Duration::from_secs(1)).unwrap();
        assert!(owner.reader().state().unwrap().0.snapshot.is_none());
        assert!(owner.stop(Duration::from_millis(10)).is_err());
        release.send(()).unwrap();
        owner.stop(Duration::from_secs(1)).unwrap();
        assert_eq!(owner.reader().state().unwrap().0.capture_generation, 1);
    }
    #[test]
    fn factory_failure_is_reported_at_startup() {
        assert!(
            matches!(Acquisition::start(|| Err("factory failure".into())), Err(error) if error == "factory failure")
        );
    }
}
