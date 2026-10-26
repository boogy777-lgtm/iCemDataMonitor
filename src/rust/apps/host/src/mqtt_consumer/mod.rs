//! Independent latest-state MQTT owner. No MMF handles, commands or adapter coupling.
mod config;
mod payload;
use crate::consumers::{Lifecycle, Status};
pub use config::Config;
use icem_core::publication::{PublishedSnapshot, SnapshotReader};
use rumqttc::{AsyncClient, Event, Incoming, MqttOptions, Outgoing, QoS};
use std::{
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
pub struct Publisher {
    cancel: Arc<AtomicBool>,
    alive: Arc<AtomicBool>,
    connected: Arc<AtomicBool>,
    done: mpsc::Receiver<Result<(), String>>,
    worker: Option<JoinHandle<()>>,
    waited: bool,
}
pub fn start(config: Config, reader: SnapshotReader) -> Result<Publisher, String> {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| format!("MQTT runtime: {e}"))?;
    let cancel = Arc::new(AtomicBool::new(false));
    let alive = Arc::new(AtomicBool::new(true));
    let connected = Arc::new(AtomicBool::new(false));
    let (completed, done) = mpsc::sync_channel(1);
    let worker_cancel = cancel.clone();
    let worker_alive = alive.clone();
    let worker_connected = connected.clone();
    let worker = thread::Builder::new()
        .name("mqtt-state".into())
        .spawn(move || {
            let result = runtime.block_on(run(config, reader, &worker_cancel, &worker_connected));
            worker_connected.store(false, Ordering::Release);
            worker_alive.store(false, Ordering::Release);
            let _ = completed.send(result);
        })
        .map_err(|e| format!("MQTT worker: {e}"))?;
    Ok(Publisher {
        cancel,
        alive,
        connected,
        done,
        worker: Some(worker),
        waited: false,
    })
}
impl Lifecycle for Publisher {
    fn status(&self) -> Status {
        if self.cancel.load(Ordering::Acquire) {
            if self.alive.load(Ordering::Acquire) {
                Status::Stopping
            } else {
                Status::Stopped
            }
        } else if self.connected.load(Ordering::Acquire) {
            Status::Running
        } else {
            Status::Faulted
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
            .map_err(|e| format!("MQTT shutdown deadline: {e}"))?;
        if let Some(worker) = self.worker.take() {
            worker
                .join()
                .map_err(|_| "MQTT worker panicked".to_owned())?;
        }
        result
    }
}
impl Drop for Publisher {
    fn drop(&mut self) {
        if self.waited {
            self.cancel.store(true, Ordering::Release);
        } else {
            let _ = self.stop(Instant::now() + Duration::from_secs(2));
        }
    }
}
/// One pending Arc, one serialized request (queued OR protocol in-flight).
#[derive(Default)]
struct LatestPending {
    state: Option<PublishedSnapshot>,
}
impl LatestPending {
    fn replace(&mut self, state: PublishedSnapshot) {
        self.state = Some(state);
    }
    fn take(&mut self) -> Option<PublishedSnapshot> {
        self.state.take()
    }
}
fn connection(config: &Config) -> (AsyncClient, rumqttc::EventLoop) {
    let mut options = MqttOptions::new(
        &config.client_id,
        config.broker.ip().to_string(),
        config.broker.port(),
    );
    options
        .set_clean_session(true)
        .set_keep_alive(Duration::from_secs(5))
        .set_inflight(1)
        .set_max_packet_size(1024, payload::MAX_PAYLOAD + 2048);
    let (client, mut events) = AsyncClient::new(options, 1);
    let mut network = events.network_options();
    network.set_connection_timeout(2);
    events.set_network_options(network);
    (client, events)
}
async fn run(
    config: Config,
    reader: SnapshotReader,
    cancel: &AtomicBool,
    connected: &AtomicBool,
) -> Result<(), String> {
    let mut pending = LatestPending::default();
    let mut heartbeat = tokio::time::interval(Duration::from_millis(25));
    heartbeat.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut next_sample = Instant::now();
    loop {
        if cancel.load(Ordering::Acquire) {
            return Ok(());
        }
        let (client, mut events) = connection(&config);
        let mut busy = false;
        let mut online = false;
        loop {
            // Preserve poll across ticks: slow connect/partial write must progress.
            let event = {
                let poll = events.poll();
                tokio::pin!(poll);
                loop {
                    tokio::select! {
                        event = &mut poll => break event,
                        _ = heartbeat.tick() => {
                            if cancel.load(Ordering::Acquire) { return Ok(()); }
                            let now = Instant::now();
                            if now >= next_sample {
                                pending.replace(reader.latest().map_err(|e| format!("MQTT reader: {e:?}"))?);
                                next_sample = now + config.interval;
                            }
                            if online && !busy && let Some(state) = pending.take() {
                                client.try_publish(&config.topic, config.qos, config.retain, payload::encode(&state, now)?).map_err(|e| format!("MQTT bounded submission: {e}"))?;
                                busy = true;
                            }
                        }
                    }
                }
            };
            match event {
                Ok(Event::Incoming(Incoming::ConnAck(_))) => {
                    online = true;
                    connected.store(true, Ordering::Release);
                }
                Ok(Event::Outgoing(Outgoing::Publish(_))) if config.qos == QoS::AtMostOnce => {
                    busy = false
                }
                Ok(Event::Incoming(Incoming::PubAck(_) | Incoming::PubComp(_))) => busy = false,
                Ok(_) => {}
                Err(_) => break,
            }
        }
        connected.store(false, Ordering::Release);
        // New clean session discards pending retransmissions; latest-state is not a journal.
        drop(client);
        drop(events);
        let retry = Instant::now() + config.reconnect;
        while Instant::now() < retry {
            heartbeat.tick().await;
            if cancel.load(Ordering::Acquire) {
                return Ok(());
            }
            pending.replace(reader.latest().map_err(|e| format!("MQTT reader: {e:?}"))?);
        }
    }
}
#[cfg(test)]
#[allow(clippy::unwrap_used)]
mod tests;
