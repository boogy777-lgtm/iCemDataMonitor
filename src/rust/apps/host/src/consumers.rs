//! Host-only lifecycle registry. No protocol dispatch or source access.
use std::{
    collections::BTreeMap,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct ConsumerId(pub &'static str);
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Status {
    Running,
    Stopped,
    Stopping,
    Faulted,
}
pub trait Lifecycle {
    fn status(&self) -> Status;
    fn cancel(&mut self) -> Result<(), String>;
    fn stop(&mut self, deadline: Instant) -> Result<(), String>;
}
/// Fixed maximum; rejected entries are returned to the caller, never silently lost.
pub struct Registry {
    entries: BTreeMap<ConsumerId, Box<dyn Lifecycle>>,
}
pub const MAX_CONSUMERS: usize = 16;
impl Registry {
    pub fn new() -> Self {
        Self {
            entries: BTreeMap::new(),
        }
    }
    pub fn register(
        &mut self,
        id: ConsumerId,
        handle: Box<dyn Lifecycle>,
    ) -> Result<(), Box<dyn Lifecycle>> {
        if self.entries.len() == MAX_CONSUMERS || self.entries.contains_key(&id) {
            return Err(handle);
        }
        self.entries.insert(id, handle);
        Ok(())
    }
    pub fn status(&self, id: ConsumerId) -> Option<Status> {
        self.entries.get(&id).map(|entry| entry.status())
    }
    /// Cancel every consumer first. One slow stop cannot delay another cancellation.
    /// A single shared deadline bounds the entire drain; failed handles stay registered.
    pub fn stop_all(&mut self, budget: Duration) -> Result<(), String> {
        let deadline = Instant::now() + budget;
        let mut errors = Vec::new();
        for (id, entry) in &mut self.entries {
            if let Err(error) = entry.cancel() {
                errors.push(format!("{}: {error}", id.0));
            }
        }
        for (id, entry) in &mut self.entries {
            if let Err(error) = entry.stop(deadline) {
                errors.push(format!("{}: {error}", id.0));
            }
        }
        if errors.is_empty() {
            Ok(())
        } else {
            Err(errors.join("; "))
        }
    }
}

/// Common server harness. Processing remains on each existing server's own runtime;
/// no acquisition callback, queue, extra worker, or network call occurs under publication lock.
pub trait Server {
    fn running(&self) -> bool;
    fn cancel(&self);
    fn stop_until(&mut self, deadline: Instant) -> Result<(), String>;
}
pub type Slot<S> = Arc<Mutex<Option<S>>>;
pub struct ServerHandle<S> {
    slot: Slot<S>,
    stopping: bool,
    failed: bool,
}
impl<S: Server> ServerHandle<S> {
    pub fn new(server: Option<S>) -> (Slot<S>, Self) {
        let slot = Arc::new(Mutex::new(server));
        (
            Arc::clone(&slot),
            Self {
                slot,
                stopping: false,
                failed: false,
            },
        )
    }
}
impl<S: Server> Lifecycle for ServerHandle<S> {
    fn status(&self) -> Status {
        match self.slot.lock() {
            Err(_) => Status::Faulted,
            Ok(slot) => match slot.as_ref() {
                None if self.failed => Status::Faulted,
                None => Status::Stopped,
                Some(server) if self.stopping && server.running() => Status::Stopping,
                Some(server) if server.running() => Status::Running,
                Some(_) if self.stopping => Status::Stopped,
                Some(_) => Status::Faulted,
            },
        }
    }
    fn cancel(&mut self) -> Result<(), String> {
        let slot = self.slot.lock().map_err(|_| "consumer slot poisoned")?;
        self.stopping = true;
        if let Some(server) = slot.as_ref() {
            server.cancel();
        }
        Ok(())
    }
    fn stop(&mut self, deadline: Instant) -> Result<(), String> {
        self.cancel()?;
        let mut slot = self.slot.lock().map_err(|_| "consumer slot poisoned")?;
        if let Some(server) = slot.as_mut()
            && let Err(error) = server.stop_until(deadline)
        {
            self.failed = true;
            return Err(error);
        }
        *slot = None;
        self.failed = false;
        Ok(())
    }
}

#[cfg(test)]
#[allow(clippy::unwrap_used)]
mod tests {
    use super::*;
    use crate::{modbus_consumer, web_consumer};
    use icem_protocols::modbus::RegisterBank;
    use icem_transport::modbus_tcp::ServerOptions;
    use icem_web::{HostLayers, WebState};
    use std::{
        collections::BTreeMap,
        net::{TcpListener, TcpStream},
    };
    type TestResult = Result<(), Box<dyn std::error::Error>>;
    #[test]
    fn real_typed_adapters_start_fail_cancel_and_release_sockets() -> TestResult {
        let bank = Arc::new(Mutex::new(
            RegisterBank::new(650).map_err(|e| format!("{e:?}"))?,
        ));
        let config = || modbus_consumer::Config {
            address: "127.0.0.1:0".parse().unwrap(),
            enabled: true,
            options: ServerOptions::default(),
        };
        let (modbus, handle) = modbus_consumer::start(config(), Arc::clone(&bank))?;
        let modbus_address = modbus.lock().unwrap().as_ref().unwrap().local_addr();
        let mut occupied = config();
        occupied.address = modbus_address;
        assert!(modbus_consumer::start(occupied, bank).is_err());
        let (state, _requests) = WebState::new(
            HostLayers::default(),
            std::env::temp_dir().join("icem-p5-theme"),
            BTreeMap::new(),
        );
        let (web, web_handle) = web_consumer::start(
            web_consumer::Config {
                address: Some("127.0.0.1:0".parse()?),
            },
            state.clone(),
        )?;
        let web_address = web.lock().unwrap().as_ref().unwrap().local_addr();
        assert!(
            web_consumer::start(
                web_consumer::Config {
                    address: Some(web_address)
                },
                state
            )
            .is_err()
        );
        let mut registry = Registry::new();
        assert!(
            registry
                .register(ConsumerId("modbus"), Box::new(handle))
                .is_ok()
        );
        assert!(
            registry
                .register(ConsumerId("web"), Box::new(web_handle))
                .is_ok()
        );
        for id in [ConsumerId("modbus"), ConsumerId("web")] {
            assert_eq!(registry.status(id), Some(Status::Running));
        }
        assert!(TcpStream::connect(modbus_address).is_ok());
        assert!(TcpStream::connect(web_address).is_ok());
        registry
            .entries
            .get_mut(&ConsumerId("modbus"))
            .unwrap()
            .stop(Instant::now() + Duration::from_secs(1))?;
        assert_eq!(registry.status(ConsumerId("modbus")), Some(Status::Stopped));
        assert_eq!(registry.status(ConsumerId("web")), Some(Status::Running));
        assert!(TcpStream::connect(web_address).is_ok());
        let _ = registry.stop_all(Duration::ZERO);
        assert_eq!(registry.entries.len(), 2);
        registry.stop_all(Duration::from_secs(2))?;
        assert!(modbus.lock().unwrap().is_none());
        assert!(web.lock().unwrap().is_none());
        for id in [ConsumerId("modbus"), ConsumerId("web")] {
            assert_eq!(registry.status(id), Some(Status::Stopped));
        }
        assert!(TcpListener::bind(modbus_address).is_ok());
        assert!(TcpListener::bind(web_address).is_ok());
        registry.stop_all(Duration::ZERO)?;
        Ok(())
    }
    struct Slow {
        running: Arc<std::sync::atomic::AtomicBool>,
        cancelled: Arc<std::sync::atomic::AtomicBool>,
    }
    impl Server for Slow {
        fn running(&self) -> bool {
            self.running.load(std::sync::atomic::Ordering::Relaxed)
        }
        fn cancel(&self) {
            self.cancelled
                .store(true, std::sync::atomic::Ordering::Relaxed);
        }
        fn stop_until(&mut self, deadline: Instant) -> Result<(), String> {
            while self.running() && Instant::now() < deadline {
                std::thread::yield_now();
            }
            if self.running() {
                Err("injected deadline".into())
            } else {
                Ok(())
            }
        }
    }
    #[test]
    fn acquisition_and_latest_readers_progress_during_slow_stop_and_source_outage() -> TestResult {
        use icem_core::{Freshness, SnapshotOrigin, SnapshotRead, SnapshotSource, SourceError};
        use std::sync::atomic::{AtomicBool, AtomicU16, Ordering};
        struct Source {
            next: Arc<AtomicU16>,
            fail: Arc<AtomicBool>,
        }
        impl SnapshotSource for Source {
            fn snapshot(&mut self) -> Result<SnapshotRead, SourceError> {
                if self.fail.load(Ordering::Relaxed) {
                    return Err(SourceError::Unavailable);
                }
                let next = self.next.fetch_add(1, Ordering::Relaxed);
                let mut bytes =
                    include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin").to_vec();
                for address in [200, 400] {
                    bytes[64 + address * 2..66 + address * 2].copy_from_slice(&next.to_be_bytes());
                }
                Ok(SnapshotRead {
                    snapshot: Arc::new(
                        icem_bus::Snapshot::decode(&bytes)
                            .map_err(|_| SourceError::InvalidLayout)?,
                    ),
                    freshness: Freshness::Fresh,
                    origin: SnapshotOrigin::Captured,
                })
            }
        }
        let fail = Arc::new(AtomicBool::new(false));
        let fault = fail.clone();
        let mut acquisition = crate::acquisition::Acquisition::start(move || {
            Ok(Box::new(Source {
                next: Arc::new(AtomicU16::new(1)),
                fail: fault,
            }))
        })?;
        let reader = acquisition.snapshot_reader();
        let until = |test: &dyn Fn() -> bool| {
            let deadline = Instant::now() + Duration::from_secs(2);
            while !test() {
                assert!(Instant::now() < deadline);
                std::thread::sleep(Duration::from_millis(2));
            }
        };
        until(&|| reader.latest().unwrap().snapshot.is_some());
        let held = reader.latest().unwrap().snapshot.unwrap();
        let (_, slow) = ServerHandle::new(Some(Slow {
            running: Arc::new(AtomicBool::new(true)),
            cancelled: Arc::new(AtomicBool::new(false)),
        }));
        let mut registry = Registry::new();
        assert!(
            registry
                .register(ConsumerId("slow"), Box::new(slow))
                .is_ok()
        );
        assert!(registry.stop_all(Duration::from_millis(450)).is_err());
        let current = reader.latest().unwrap().snapshot.unwrap();
        assert_eq!(current.pair_bits(4)?, held.pair_bits(4)?);
        for address in [200, 400] {
            assert_ne!(current.pair_bits(address)?, held.pair_bits(address)?);
        }
        fail.store(true, Ordering::Relaxed);
        until(&|| {
            matches!(
                reader.latest().unwrap().status,
                icem_core::publication::CaptureStatus::Failed(SourceError::Unavailable)
            )
        });
        let failed = reader.latest().unwrap();
        assert!(failed.snapshot.is_some());
        let completed = failed.completed_at_monotonic;
        until(&|| reader.latest().unwrap().completed_at_monotonic > completed);
        assert!(Arc::ptr_eq(
            failed.snapshot.as_ref().unwrap(),
            reader.latest().unwrap().snapshot.as_ref().unwrap()
        ));
        assert_eq!(registry.status(ConsumerId("slow")), Some(Status::Stopping));
        acquisition.stop(Duration::from_secs(1))?;
        Ok(())
    }
    #[test]
    fn bounded_registry_retains_deadline_owner_and_cancels_every_consumer_first() -> TestResult {
        use std::sync::atomic::{AtomicBool, Ordering};
        const IDS: [&str; MAX_CONSUMERS] = [
            "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p",
        ];
        let mut registry = Registry::new();
        let mut switches = Vec::new();
        for id in IDS {
            let running = Arc::new(AtomicBool::new(true));
            let cancelled = Arc::new(AtomicBool::new(false));
            let (_, handle) = ServerHandle::new(Some(Slow {
                running: running.clone(),
                cancelled: cancelled.clone(),
            }));
            assert!(registry.register(ConsumerId(id), Box::new(handle)).is_ok());
            switches.push((ConsumerId(id), running, cancelled));
        }
        let (_, extra) = ServerHandle::new(None::<Slow>);
        assert!(
            registry
                .register(ConsumerId("overflow"), Box::new(extra))
                .is_err()
        );
        let (_, duplicate) = ServerHandle::new(None::<Slow>);
        assert!(
            registry
                .register(switches[0].0, Box::new(duplicate))
                .is_err()
        );
        let at = Instant::now();
        assert!(registry.stop_all(Duration::from_millis(20)).is_err());
        assert!(at.elapsed() < Duration::from_secs(1));
        for (id, running, cancelled) in &switches {
            assert!(cancelled.load(Ordering::Relaxed));
            assert_eq!(registry.status(*id), Some(Status::Stopping));
            running.store(false, Ordering::Relaxed);
        }
        registry.stop_all(Duration::ZERO)?;
        assert_eq!(registry.entries.len(), MAX_CONSUMERS);
        Ok(())
    }
}
