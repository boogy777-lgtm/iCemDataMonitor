//! One bounded worker independent of request contracts.
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
    mpsc,
};
pub struct Worker<Q, R> {
    sender: mpsc::SyncSender<Q>,
    receiver: mpsc::Receiver<R>,
    stopping: Arc<AtomicBool>,
}
/// Kept outside the view. Finish only AFTER the UI event loop has returned.
#[must_use = "finish the worker after the UI loop to preserve in-flight writes"]
pub struct WorkerCompletion {
    worker: std::thread::JoinHandle<()>,
    stopping: Arc<AtomicBool>,
}
impl WorkerCompletion {
    pub fn finish(self) -> std::io::Result<()> {
        self.stopping.store(true, Ordering::Release);
        self.worker.thread().unpark();
        self.worker
            .join()
            .map_err(|_| std::io::Error::other("desktop worker panicked"))
    }
}
impl<Q: Send + 'static, R: Send + 'static> Worker<Q, R> {
    pub fn start(
        execute: impl Fn(Q) -> R + Send + 'static,
    ) -> std::io::Result<(Self, WorkerCompletion)> {
        let (sender, requests) = mpsc::sync_channel::<Q>(16);
        let (responses, receiver) = mpsc::sync_channel(16);
        let stopping = Arc::new(AtomicBool::new(false));
        let stop = stopping.clone();
        let worker = std::thread::Builder::new()
            .name("desktop-control".into())
            .spawn(move || {
                'requests: while !stop.load(Ordering::Acquire) {
                    let request = match requests.recv_timeout(std::time::Duration::from_millis(50))
                    {
                        Ok(request) => request,
                        Err(mpsc::RecvTimeoutError::Timeout) => continue,
                        Err(mpsc::RecvTimeoutError::Disconnected) => break,
                    };
                    if stop.load(Ordering::Acquire) {
                        break;
                    }
                    let mut reply = execute(request);
                    loop {
                        if stop.load(Ordering::Acquire) {
                            break 'requests;
                        }
                        match responses.try_send(reply) {
                            Ok(()) => break,
                            Err(mpsc::TrySendError::Disconnected(_)) => break 'requests,
                            Err(mpsc::TrySendError::Full(returned)) => {
                                reply = returned;
                                std::thread::park_timeout(std::time::Duration::from_millis(50));
                            }
                        }
                    }
                }
            })?;
        Ok((
            Self {
                sender,
                receiver,
                stopping: stopping.clone(),
            },
            WorkerCompletion { worker, stopping },
        ))
    }
    pub fn send(&self, request: Q) -> Result<(), String> {
        self.sender
            .try_send(request)
            .map_err(|e| format!("очередь команд: {e}"))
    }
    pub fn try_recv(&self) -> Result<R, mpsc::TryRecvError> {
        self.receiver.try_recv()
    }
}
impl<Q, R> Drop for Worker<Q, R> {
    fn drop(&mut self) {
        self.stopping.store(true, Ordering::Release);
        let (_, empty) = mpsc::channel();
        drop(std::mem::replace(&mut self.receiver, empty));
        // Never join backend I/O from a window-close callback. The worker owns
        // only the port/queues/stop flag; backpressure observes cancellation within 50 ms.
        // A request already in progress may finish; the queue is never replayed.
    }
}
