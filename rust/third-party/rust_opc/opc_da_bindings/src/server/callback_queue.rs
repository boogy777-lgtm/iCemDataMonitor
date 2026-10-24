//! iCem SDK extension: finite owned queue, independent of COM.
#![forbid(unsafe_code)]
use std::{
    collections::{BTreeMap, BTreeSet, VecDeque},
    sync::{Arc, Condvar, Mutex, atomic::AtomicBool},
};
pub(crate) const MAX_REQUESTS: usize = 32;
pub(crate) const MAX_SINKS: usize = 4;
pub(crate) const MAX_ITEMS: usize = 4096;
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum QueueError {
    Full,
    Closed,
    Unknown,
    Poisoned,
}
pub(crate) struct DeliveryQueue<T> {
    latest: Option<T>,
    completions: VecDeque<(u32, T)>,
    reserved: BTreeSet<u32>,
    cancelled: BTreeSet<u32>,
    next: u32,
    closed: bool,
}
impl<T> DeliveryQueue<T> {
    pub(crate) fn new() -> Self {
        Self {
            latest: None,
            completions: VecDeque::new(),
            reserved: BTreeSet::new(),
            cancelled: BTreeSet::new(),
            next: 1,
            closed: false,
        }
    }
    pub(crate) fn reserve(&mut self) -> Result<u32, QueueError> {
        if self.closed {
            return Err(QueueError::Closed);
        }
        if self.reserved.len() >= MAX_REQUESTS || self.next == u32::MAX {
            return Err(QueueError::Full);
        }
        let id = self.next;
        self.next += 1;
        self.reserved.insert(id);
        Ok(id)
    }
    pub(crate) fn pending(&self, id: u32) -> bool {
        self.reserved.contains(&id)
    }
    pub(crate) fn cancellable(&self, id: u32) -> bool {
        !self.closed
            && !self.cancelled.contains(&id)
            && self.completions.iter().any(|(key, _)| *key == id)
    }
    pub(crate) fn complete(&mut self, id: u32, value: T) -> Result<(), QueueError> {
        if self.closed {
            return Err(QueueError::Closed);
        }
        if !self.reserved.contains(&id) || self.completions.iter().any(|(key, _)| *key == id) {
            return Err(QueueError::Unknown);
        }
        self.completions.push_back((id, value));
        Ok(())
    }
    pub(crate) fn cancel(&mut self, id: u32, value: T) -> Result<(), QueueError> {
        if !self.cancellable(id) {
            return Err(QueueError::Unknown);
        }
        if let Some((_, pending)) = self.completions.iter_mut().find(|(key, _)| *key == id) {
            *pending = value;
            self.cancelled.insert(id);
            Ok(())
        } else {
            Err(QueueError::Unknown)
        }
    }
    pub(crate) fn latest(&mut self, value: T) -> Result<(), QueueError> {
        if self.closed {
            return Err(QueueError::Closed);
        }
        self.latest = Some(value);
        Ok(())
    }
    pub(crate) fn pop(&mut self) -> Option<T> {
        if let Some((id, value)) = self.completions.pop_front() {
            self.reserved.remove(&id);
            self.cancelled.remove(&id);
            Some(value)
        } else {
            self.latest.take()
        }
    }
    pub(crate) fn release(&mut self, id: u32) {
        self.reserved.remove(&id);
        self.cancelled.remove(&id);
        self.completions.retain(|(key, _)| *key != id);
    }
    pub(crate) fn clear_latest(&mut self) {
        self.latest = None;
    }
    pub(crate) fn close(&mut self) {
        self.closed = true;
        self.latest = None;
        self.completions.clear();
        self.reserved.clear();
        self.cancelled.clear();
    }
}
// The same group membership mutex covers preparation and accepted requests.
// These are DA callback queues only, not service/model/COM admission.
pub(crate) struct Sink<T> {
    pub(crate) queue: Mutex<DeliveryQueue<T>>,
    pub(crate) wake: Condvar,
    pub(crate) closed: AtomicBool,
}
pub(crate) struct Request<T> {
    pub(crate) value: Option<T>, // None while the SDK prepares the owned result.
    pub(crate) ids: Vec<(u32, u32)>,
}
pub(crate) struct Members<T> {
    pub(crate) sinks: BTreeMap<u32, Arc<Sink<T>>>,
    pub(crate) requests: BTreeMap<u32, Request<T>>,
    pub(crate) next_cookie: u32,
    pub(crate) next_cancel: u32,
    pub(crate) closed: bool,
}
pub(crate) struct Preparation<'a, T> {
    members: &'a Mutex<Members<T>>,
    id: u32,
    armed: bool,
}
impl<T> Members<T> {
    pub(crate) fn prepare(members: &Mutex<Self>) -> Result<Preparation<'_, T>, QueueError> {
        let mut m = members.lock().map_err(|_| QueueError::Poisoned)?;
        if m.closed || m.sinks.is_empty() {
            return Err(QueueError::Closed);
        }
        let live = m.sinks.clone();
        // Canceled events still hold their credits until dequeue. Preparations
        // remain charged even if every originally selected sink disconnects.
        m.requests.retain(|_, request| {
            request.value.is_none()
                || request.ids.iter().any(|(cookie, id)| {
                    live.get(cookie).is_some_and(|sink| {
                        sink.queue.lock().map(|q| q.pending(*id)).unwrap_or(true)
                    })
                })
        });
        if m.requests.len() >= MAX_REQUESTS || m.next_cancel == u32::MAX {
            return Err(QueueError::Full);
        }
        let sinks = m
            .sinks
            .iter()
            .map(|(c, s)| (*c, s.clone()))
            .collect::<Vec<_>>();
        // Acquire every queue before reserving anything: poison cannot leave a
        // partial reservation. Lock order remains members -> queues everywhere.
        let mut queues = Vec::with_capacity(sinks.len());
        for (_, sink) in &sinks {
            queues.push(sink.queue.lock().map_err(|_| QueueError::Poisoned)?);
        }
        let mut ids = Vec::with_capacity(sinks.len());
        for i in 0..queues.len() {
            match queues[i].reserve() {
                Ok(id) => ids.push((sinks[i].0, id)),
                Err(error) => {
                    for (q, (_, id)) in queues.iter_mut().zip(&ids) {
                        q.release(*id);
                    }
                    return Err(error);
                }
            }
        }
        let id = m.next_cancel;
        m.next_cancel += 1;
        m.requests.insert(id, Request { value: None, ids });
        Ok(Preparation {
            members,
            id,
            armed: true,
        })
    }
}
impl<T: Clone> Preparation<'_, T> {
    // refresh_epoch performs only the SDK's atomic refresh epoch update;
    // never invoke a service/model/foreign callback with these locks held.
    pub(crate) fn commit(
        mut self,
        value: T,
        refresh_epoch: Option<&std::sync::atomic::AtomicU64>,
    ) -> Result<u32, QueueError> {
        let mut m = self.members.lock().map_err(|_| QueueError::Poisoned)?;
        if m.closed {
            return Err(QueueError::Closed);
        }
        let request = m.requests.get(&self.id).ok_or(QueueError::Unknown)?;
        // Advise linearizes at prepare: new sinks cannot gain an unreserved
        // completion. Unadvised/retired sinks simply cease delivery.
        let sinks = request
            .ids
            .iter()
            .filter_map(|(cookie, id)| m.sinks.get(cookie).map(|sink| (sink.clone(), *id)))
            .collect::<Vec<_>>();
        if sinks.is_empty() {
            return Err(QueueError::Closed);
        }
        let mut queues = Vec::with_capacity(sinks.len());
        for (sink, _) in &sinks {
            queues.push(sink.queue.lock().map_err(|_| QueueError::Poisoned)?);
        }
        if queues
            .iter()
            .zip(&sinks)
            .any(|(q, (_, id))| !q.pending(*id))
        {
            return Err(QueueError::Closed);
        }
        if let Some(epoch) = refresh_epoch {
            epoch.fetch_add(1, std::sync::atomic::Ordering::AcqRel);
            for q in &mut queues {
                q.clear_latest();
            }
        }
        for (q, (_, id)) in queues.iter_mut().zip(&sinks) {
            q.complete(*id, value.clone())?;
        }
        m.requests
            .get_mut(&self.id)
            .ok_or(QueueError::Unknown)?
            .value = Some(value);
        self.armed = false; // existing credits transfer; never reserve a second time.
        drop(queues);
        drop(m);
        for (sink, _) in sinks {
            sink.wake.notify_one();
        }
        Ok(self.id)
    }
}
impl<T> Drop for Preparation<'_, T> {
    fn drop(&mut self) {
        if !self.armed {
            return;
        }
        // Poison rejects future work, but must not prevent rollback. No foreign
        // code executes here and every credit belongs exclusively to this token.
        let mut m = self.members.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(request) = m.requests.remove(&self.id) {
            for (cookie, id) in request.ids {
                if let Some(sink) = m.sinks.get(&cookie) {
                    sink.queue
                        .lock()
                        .unwrap_or_else(|e| e.into_inner())
                        .release(id);
                }
            }
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn members(sinks: usize) -> Mutex<Members<&'static str>> {
        Mutex::new(Members {
            sinks: (1..=sinks as u32)
                .map(|cookie| {
                    (
                        cookie,
                        Arc::new(Sink {
                            queue: Mutex::new(DeliveryQueue::new()),
                            wake: Condvar::new(),
                            closed: AtomicBool::new(false),
                        }),
                    )
                })
                .collect(),
            requests: BTreeMap::new(),
            next_cookie: sinks as u32 + 1,
            next_cancel: 1,
            closed: false,
        })
    }
    #[test]
    fn concurrent_preparation_rejects_before_owned_service_work() {
        use std::sync::{
            Barrier,
            atomic::{AtomicUsize, Ordering},
        };
        let m = members(2);
        let entered = AtomicUsize::new(0);
        let allocated = AtomicUsize::new(0);
        let ready = Barrier::new(MAX_REQUESTS + 1);
        let release = Barrier::new(MAX_REQUESTS + 1);
        std::thread::scope(|scope| {
            for _ in 0..MAX_REQUESTS {
                scope.spawn(|| {
                    let _permit = Members::prepare(&m).unwrap();
                    // Stand-in service/owned decode begins only after admission.
                    entered.fetch_add(1, Ordering::SeqCst);
                    let owned = vec![7_u8; MAX_ITEMS];
                    allocated.fetch_add(owned.len(), Ordering::SeqCst);
                    ready.wait();
                    release.wait();
                });
            }
            ready.wait();
            let rejects = (0..64)
                .map(|_| {
                    scope.spawn(|| {
                        assert!(matches!(Members::prepare(&m), Err(QueueError::Full)));
                    })
                })
                .collect::<Vec<_>>();
            for reject in rejects {
                reject.join().unwrap();
            }
            assert_eq!(entered.load(Ordering::SeqCst), MAX_REQUESTS);
            assert_eq!(allocated.load(Ordering::SeqCst), MAX_REQUESTS * MAX_ITEMS);
            release.wait();
        });
        assert!(Members::prepare(&m).is_ok());
    }
    #[test]
    fn retained_and_preparing_share_budget_until_all_sinks_dequeue() {
        let m = members(2);
        let accepted = Members::prepare(&m).unwrap().commit("read", None).unwrap();
        let preparations = (1..MAX_REQUESTS)
            .map(|_| Members::prepare(&m).unwrap())
            .collect::<Vec<_>>();
        assert!(matches!(Members::prepare(&m), Err(QueueError::Full)));
        {
            let state = m.lock().unwrap();
            let request = &state.requests[&accepted];
            for (cookie, id) in &request.ids {
                state.sinks[cookie]
                    .queue
                    .lock()
                    .unwrap()
                    .cancel(*id, "cancel")
                    .unwrap();
            }
            state.sinks[&1].queue.lock().unwrap().pop();
        }
        // Cancellation and a single dequeue cannot bypass the group cap.
        assert!(matches!(Members::prepare(&m), Err(QueueError::Full)));
        m.lock().unwrap().sinks[&2].queue.lock().unwrap().pop();
        assert!(Members::prepare(&m).is_ok());
        drop(preparations);
        assert!(m.lock().unwrap().requests.is_empty());
    }
    #[test]
    fn full_sink_rejects_atomically_before_preparation() {
        let m = members(2);
        {
            let state = m.lock().unwrap();
            let mut q = state.sinks[&2].queue.lock().unwrap();
            for _ in 0..MAX_REQUESTS {
                q.reserve().unwrap();
            }
        }
        for _ in 0..64 {
            assert!(matches!(Members::prepare(&m), Err(QueueError::Full)));
        }
        let state = m.lock().unwrap();
        assert!(state.requests.is_empty());
        assert!(state.sinks[&1].queue.lock().unwrap().reserved.is_empty());
    }
    #[test]
    fn rollback_covers_error_all_failed_panic_and_poison() {
        let m = members(2);
        let fail = || -> Result<(), QueueError> {
            let _permit = Members::prepare(&m)?;
            Err(QueueError::Unknown) // service/output error; no completion
        };
        for _ in 0..64 {
            assert_eq!(fail(), Err(QueueError::Unknown));
        }
        for _ in 0..64 {
            drop(Members::prepare(&m).unwrap());
        } // all-failed/no callback
        assert!(
            std::panic::catch_unwind(|| {
                let _permit = Members::prepare(&m).unwrap();
                panic!("service panic");
            })
            .is_err()
        );
        assert!(m.lock().unwrap().requests.is_empty());
        let permit = Members::prepare(&m).unwrap();
        assert!(
            std::panic::catch_unwind(|| {
                let state = m.lock().unwrap();
                let _q = state.sinks[&1].queue.lock().unwrap();
                panic!("poison membership and queue");
            })
            .is_err()
        );
        drop(permit);
        let state = m.lock().err().unwrap().into_inner();
        assert!(state.requests.is_empty());
        assert!(
            state.sinks[&1]
                .queue
                .lock()
                .err()
                .unwrap()
                .into_inner()
                .reserved
                .is_empty()
        );
        drop(state);
        assert!(matches!(Members::prepare(&m), Err(QueueError::Poisoned)));
    }
    #[test]
    fn sink_set_is_fixed_before_service_and_disconnect_does_not_fabricate_delivery() {
        let m = members(2);
        let permit = Members::prepare(&m).unwrap();
        let late = members(1).into_inner().unwrap().sinks.remove(&1).unwrap();
        {
            let mut state = m.lock().unwrap();
            state
                .sinks
                .remove(&1)
                .unwrap()
                .queue
                .lock()
                .unwrap()
                .close();
            state.sinks.insert(3, late.clone());
        }
        permit.commit("read", None).unwrap();
        assert_eq!(
            m.lock().unwrap().sinks[&2].queue.lock().unwrap().pop(),
            Some("read")
        );
        assert_eq!(late.queue.lock().unwrap().pop(), None);
        let permit = Members::prepare(&m).unwrap();
        {
            let mut state = m.lock().unwrap();
            for sink in state.sinks.values() {
                sink.queue.lock().unwrap().close();
            }
            state.sinks.clear();
        }
        assert_eq!(permit.commit("lost", None), Err(QueueError::Closed));
        assert!(m.lock().unwrap().requests.is_empty());
    }
    #[test]
    fn remove_during_preparation_releases_without_completion() {
        let m = members(1);
        let permit = Members::prepare(&m).unwrap();
        {
            let mut state = m.lock().unwrap();
            state.closed = true;
            for sink in state.sinks.values() {
                sink.queue.lock().unwrap().close();
            }
            state.sinks.clear();
            state.requests.clear();
        }
        assert_eq!(permit.commit("removed", None), Err(QueueError::Closed));
        assert!(m.lock().unwrap().requests.is_empty());
    }
    #[test]
    fn coalescing_preserves_completion_credits() {
        let mut q = DeliveryQueue::new();
        for n in 0..100_000 {
            q.latest(n).unwrap();
        }
        for n in 0..MAX_REQUESTS {
            let id = q.reserve().unwrap();
            q.complete(id, n).unwrap();
        }
        assert_eq!(q.reserve(), Err(QueueError::Full));
        for n in 0..MAX_REQUESTS {
            assert_eq!(q.pop(), Some(n));
        }
        assert_eq!(q.pop(), Some(99_999));
        assert!(q.reserve().is_ok());
    }
    #[test]
    fn cancel_linearizes_before_dequeue() {
        let mut q = DeliveryQueue::new();
        let id = q.reserve().unwrap();
        assert!(q.pending(id));
        q.complete(id, "read").unwrap();
        assert_eq!(q.complete(id, "duplicate"), Err(QueueError::Unknown));
        q.cancel(id, "cancel").unwrap();
        assert_eq!(q.cancel(id, "duplicate"), Err(QueueError::Unknown));
        assert_eq!(q.pop(), Some("cancel"));
        assert_eq!(q.cancel(id, "late"), Err(QueueError::Unknown));
    }
    #[test]
    fn close_releases_and_rejects() {
        let mut q = DeliveryQueue::new();
        let id = q.reserve().unwrap();
        q.complete(id, vec![1; MAX_ITEMS]).unwrap();
        q.latest(vec![2; MAX_ITEMS]).unwrap();
        q.clear_latest();
        q.release(id);
        q.close();
        assert_eq!(q.reserve(), Err(QueueError::Closed));
        assert_eq!(q.latest(Vec::new()), Err(QueueError::Closed));
        assert_eq!(q.complete(id, Vec::new()), Err(QueueError::Closed));
        assert_eq!(q.pop(), None);
    }
    #[test]
    fn finite_profile() {
        assert_eq!(MAX_SINKS * 32, 128);
    }
    #[test]
    fn competing_cancel_and_dequeue_have_one_terminal_result() {
        for _ in 0..200 {
            let mut q = DeliveryQueue::new();
            let id = q.reserve().unwrap();
            q.complete(id, "read").unwrap();
            let q = std::sync::Arc::new(std::sync::Mutex::new(q));
            let barrier = std::sync::Arc::new(std::sync::Barrier::new(2));
            let worker_q = q.clone();
            let worker_b = barrier.clone();
            let worker = std::thread::spawn(move || {
                worker_b.wait();
                worker_q.lock().unwrap().pop().unwrap()
            });
            barrier.wait();
            let cancelled = q.lock().unwrap().cancel(id, "cancel").is_ok();
            assert_eq!(
                worker.join().unwrap(),
                if cancelled { "cancel" } else { "read" }
            );
            assert!(q.lock().unwrap().pop().is_none());
        }
    }
}
