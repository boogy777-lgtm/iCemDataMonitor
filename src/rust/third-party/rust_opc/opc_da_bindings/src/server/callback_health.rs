//! iCem SDK extension: process-scoped health contains no COM references.
#![forbid(unsafe_code)]
use std::{
    collections::BTreeMap,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};
const MAX_WORKERS: usize = 128;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CallbackHealth {
    pub live_workers: usize,
    pub in_flight: usize,
    pub oldest_in_flight: Duration,
    pub failed: bool,
}
impl CallbackHealth {
    pub fn healthy(&self, deadline: Duration) -> bool {
        !self.failed && (self.in_flight == 0 || self.oldest_in_flight < deadline)
    }
}
#[derive(Default)]
struct State {
    workers: BTreeMap<u64, Option<Instant>>,
    next: u64,
    failed: bool,
}
#[derive(Clone, Default)]
pub struct CallbackMonitor(Arc<Mutex<State>>);
impl CallbackMonitor {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn health(&self, now: Instant) -> CallbackHealth {
        let Ok(s) = self.0.lock() else {
            return CallbackHealth {
                failed: true,
                ..CallbackHealth::default()
            };
        };
        let mut h = CallbackHealth {
            live_workers: s.workers.len(),
            failed: s.failed,
            ..CallbackHealth::default()
        };
        for start in s.workers.values().flatten() {
            h.in_flight += 1;
            h.oldest_in_flight = h
                .oldest_in_flight
                .max(now.saturating_duration_since(*start));
        }
        h
    }
    pub(crate) fn worker(&self) -> Option<WorkerLease> {
        let mut s = self.0.lock().ok()?;
        if s.failed || s.workers.len() >= MAX_WORKERS || s.next == u64::MAX {
            return None;
        }
        let id = s.next;
        s.next += 1;
        s.workers.insert(id, None);
        Some(WorkerLease(Arc::new(WorkerRegistration {
            monitor: self.clone(),
            id,
        })))
    }
}
#[derive(Clone)]
pub(crate) struct WorkerLease(Arc<WorkerRegistration>);
struct WorkerRegistration {
    monitor: CallbackMonitor,
    id: u64,
}
impl WorkerLease {
    pub(crate) fn begin(&self, now: Instant) {
        if let Ok(mut s) = self.0.monitor.0.lock() {
            if let Some(start) = s.workers.get_mut(&self.0.id) {
                *start = Some(now);
            }
        }
    }
    pub(crate) fn finish(&self) {
        if let Ok(mut s) = self.0.monitor.0.lock() {
            if let Some(start) = s.workers.get_mut(&self.0.id) {
                *start = None;
            }
        }
    }
    pub(crate) fn fail(&self) {
        if let Ok(mut s) = self.0.monitor.0.lock() {
            s.failed = true;
        }
    }
}
impl Drop for WorkerRegistration {
    fn drop(&mut self) {
        if let Ok(mut s) = self.monitor.0.lock() {
            s.workers.remove(&self.id);
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn retired_stays_charged_until_exit() {
        let m = CallbackMonitor::new();
        let w = m.worker().unwrap();
        let now = Instant::now();
        w.begin(now);
        let h = m.health(now + Duration::from_secs(3));
        assert_eq!(h.in_flight, 1);
        assert!(!h.healthy(Duration::from_secs(2)));
        w.finish();
        assert!(m.health(now).healthy(Duration::from_secs(2)));
        let retained = w.clone();
        drop(w);
        assert_eq!(m.health(now).live_workers, 1);
        drop(retained);
        assert_eq!(m.health(now).live_workers, 0);
    }
    #[test]
    fn repeated_disconnect_cannot_bypass_cap() {
        let m = CallbackMonitor::new();
        let workers: Vec<_> = (0..MAX_WORKERS).map(|_| m.worker().unwrap()).collect();
        assert!(m.worker().is_none());
        drop(workers);
        assert!(m.worker().is_some());
    }
    #[test]
    fn failure_sticky_after_worker_exit() {
        let m = CallbackMonitor::new();
        let w = m.worker().unwrap();
        w.fail();
        drop(w);
        assert!(!m.health(Instant::now()).healthy(Duration::from_secs(2)));
        assert!(m.worker().is_none());
    }
}
