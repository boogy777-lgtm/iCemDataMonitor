//! iCem SDK extension: finite owned queue, independent of COM.
#![forbid(unsafe_code)]
use std::collections::{BTreeSet, VecDeque};
pub(crate) const MAX_REQUESTS: usize = 32;
pub(crate) const MAX_SINKS: usize = 4;
pub(crate) const MAX_ITEMS: usize = 4096;
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum QueueError {
    Full,
    Closed,
    Unknown,
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
#[cfg(test)]
mod tests {
    use super::*;
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
