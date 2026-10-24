//! Independent read-only OPC UA SDK owner. No MMF/source capability.
mod config;
mod model;
use crate::consumers::{Lifecycle, Status};
pub use config::Config;
use icem_core::publication::SnapshotReader;
use opcua::server::{
    ServerBuilder,
    diagnostics::NamespaceMetadata,
    node_manager::memory::{SimpleNodeManager, simple_node_manager},
};
use std::{
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
pub const NAMESPACE: &str = "urn:icem:bus:v1";
pub struct Server {
    cancel: Arc<AtomicBool>,
    alive: Arc<AtomicBool>,
    done: mpsc::Receiver<Result<(), String>>,
    worker: Option<JoinHandle<()>>,
    waited: bool,
}
pub fn start(config: Config, reader: SnapshotReader) -> Result<Server, String> {
    // Binding is real and synchronous: occupied port fails before registration.
    let listener =
        std::net::TcpListener::bind(config.bind).map_err(|e| format!("OPC UA bind: {e}"))?;
    listener.set_nonblocking(true).map_err(|e| e.to_string())?;
    let cancel = Arc::new(AtomicBool::new(false));
    let alive = Arc::new(AtomicBool::new(true));
    let (completed, done) = mpsc::sync_channel(1);
    let (ready_tx, ready) = mpsc::sync_channel(1);
    let worker_cancel = cancel.clone();
    let worker_alive = alive.clone();
    let worker = thread::Builder::new().name("opcua-state".into()).spawn(move || {
        let result = (|| {
            let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build().map_err(|e| e.to_string())?;
            runtime.block_on(async move {
                let mut builder = ServerBuilder::new_anonymous("iCem read-only Bus")
                    .application_uri("urn:icem:host:opcua")
                    .host(if config.bind.is_ipv6() { format!("[{}]", config.bind.ip()) } else { config.bind.ip().to_string() }).port(config.bind.port())
                    .create_sample_keypair(false)
                    .with_node_manager(simple_node_manager(NamespaceMetadata { namespace_uri: NAMESPACE.into(), ..Default::default() }, "icem-bus"));
                let limits = builder.limits_mut();
                limits.max_sessions = 4;
                limits.max_message_size = 1_048_576;
                limits.max_chunk_count = 16;
                limits.send_buffer_size = 65536;
                limits.receive_buffer_size = 65536;
                limits.max_array_length = 65535;
                limits.max_string_length = 4096;
                limits.max_byte_string_length = 65536;
                limits.max_browse_continuation_points = 4;
                limits.max_history_continuation_points = 1;
                limits.max_query_continuation_points = 1;
                limits.operational.max_nodes_per_read = 1024;
                limits.operational.max_nodes_per_browse = 64;
                limits.operational.max_references_per_browse_node = 1024;
                limits.operational.max_monitored_items_per_call = 1024;
                let subs = &mut limits.subscriptions;
                subs.max_subscriptions_per_session = 4;
                subs.max_pending_publish_requests = 8;
                subs.max_publish_requests_per_subscription = 2;
                subs.min_sampling_interval_ms = 100.0;
                subs.min_publishing_interval_ms = 100.0;
                subs.max_monitored_items_per_sub = 1024;
                subs.max_monitored_item_queue_size = 2;
                subs.max_queued_notifications = 2048;
                subs.max_notifications_per_publish = 1024;
                subs.max_keep_alive_count = 100;
                subs.max_lifetime_count = 300;
                builder.config_mut().tcp_config.hello_timeout = 2;
                let (server, handle) = builder.build()?;
                let manager = handle.node_managers().get_of_type::<SimpleNodeManager>().ok_or("OPC UA manager missing")?;
                let ns = handle.get_namespace_index(NAMESPACE).ok_or("OPC UA namespace missing")?;
                let mut model = model::Model::new(manager, ns)?;
                model.update(reader.latest().map_err(|e| format!("OPC UA reader: {e:?}"))?, handle.subscriptions(), Instant::now())?;
                let listener = tokio::net::TcpListener::from_std(listener).map_err(|e| e.to_string())?;
                // SDK0.19 replaces start_time only after node-manager initialization.
                let initial_start = handle.info().start_time.load_full();
                let mut ready_tx = Some(ready_tx);
                let serve = server.run_with(listener);
                tokio::pin!(serve);
                let mut heartbeat = tokio::time::interval(Duration::from_millis(25));
                heartbeat.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
                let mut next_update = Instant::now();
                loop {
                    tokio::select! {
                        result = &mut serve => return result,
                        _ = heartbeat.tick() => {
                            if !Arc::ptr_eq(&initial_start, &handle.info().start_time.load_full())
                                && let Some(ready) = ready_tx.take() { let _ = ready.send(()); }
                            if worker_cancel.load(Ordering::Acquire) {
                                handle.cancel();
                                return tokio::time::timeout(Duration::from_millis(500), &mut serve).await.map_err(|_| "OPC UA SDK shutdown timeout".to_owned())?;
                            }
                            let now = Instant::now();
                            if now >= next_update {
                                model.update(reader.latest().map_err(|e| format!("OPC UA reader: {e:?}"))?, handle.subscriptions(), now)?;
                                next_update = now + config.interval;
                            }
                        }
                    }
                }
            })
        })();
        worker_alive.store(false, Ordering::Release);
        let _ = completed.send(result);
    }).map_err(|e| format!("OPC UA worker: {e}"))?;
    let mut owner = Server {
        cancel,
        alive,
        done,
        worker: Some(worker),
        waited: false,
    };
    if ready.recv_timeout(Duration::from_secs(10)).is_err() {
        let error = owner
            .done
            .try_recv()
            .ok()
            .and_then(Result::err)
            .unwrap_or_else(|| "OPC UA startup deadline".into());
        let _ = owner.stop(Instant::now() + Duration::from_secs(1));
        return Err(error);
    }
    Ok(owner)
}
impl Lifecycle for Server {
    fn status(&self) -> Status {
        match (
            self.cancel.load(Ordering::Acquire),
            self.alive.load(Ordering::Acquire)
                && self
                    .worker
                    .as_ref()
                    .is_some_and(|worker| !worker.is_finished()),
        ) {
            (true, true) => Status::Stopping,
            (true, false) => Status::Stopped,
            (false, true) => Status::Running,
            (false, false) => Status::Faulted,
        }
    }
    fn cancel(&mut self) -> Result<(), String> {
        self.cancel.store(true, Ordering::Release);
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
            .map_err(|e| format!("OPC UA shutdown deadline: {e}"))?;
        if let Some(worker) = self.worker.take() {
            worker.join().map_err(|_| "OPC UA worker panicked")?;
        }
        result
    }
}
impl Drop for Server {
    fn drop(&mut self) {
        if self.waited {
            self.cancel.store(true, Ordering::Release);
        } else {
            let _ = self.stop(Instant::now() + Duration::from_secs(2));
        }
    }
}
#[cfg(test)]
#[allow(clippy::unwrap_used)]
mod tests;
