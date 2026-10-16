use crate::WebState;
use std::{
    io,
    net::{SocketAddr, TcpListener},
    thread::{self, JoinHandle},
    time::Duration,
};
use tokio::sync::watch;

pub struct WebServer {
    address: SocketAddr,
    stop: watch::Sender<bool>,
    thread: Option<JoinHandle<io::Result<()>>>,
}
impl WebServer {
    /// Bind synchronously; a failed endpoint never looks like a started layer.
    pub fn bind(address: SocketAddr, state: WebState) -> io::Result<Self> {
        let listener = TcpListener::bind(address)?;
        let address = listener.local_addr()?;
        listener.set_nonblocking(true)?;
        let stop = state.shutdown.clone();
        let mut stopping = stop.subscribe();
        let (ready, wait) = std::sync::mpsc::sync_channel(1);
        let thread = thread::Builder::new()
            .name("icem-web".into())
            .spawn(move || {
                let runtime = tokio::runtime::Builder::new_current_thread()
                    .enable_all()
                    .build()?;
                runtime.block_on(async move {
                    let listener = tokio::net::TcpListener::from_std(listener)?;
                    let router = crate::router(state);
                    let _ = ready.send(());
                    let serving = axum::serve(listener, router).into_future();
                    tokio::pin!(serving);
                    tokio::select! {
                        result=&mut serving=>result,
                        _=stopping.changed()=>{
                            // Cancel all streams, then drop listener/connections on runtime teardown.
                            tokio::time::sleep(Duration::from_millis(20)).await;
                            Ok(())
                        }
                    }
                })
            })?;
        wait.recv()
            .map_err(|_| io::Error::other("web runtime failed before readiness"))?;
        Ok(Self {
            address,
            stop,
            thread: Some(thread),
        })
    }
    pub fn local_addr(&self) -> SocketAddr {
        self.address
    }
    pub fn stop(mut self) -> io::Result<()> {
        self.join()
    }
    fn join(&mut self) -> io::Result<()> {
        let _ = self.stop.send(true);
        if let Some(thread) = self.thread.take() {
            thread
                .join()
                .map_err(|_| io::Error::other("web worker panicked"))??;
        }
        Ok(())
    }
}
impl Drop for WebServer {
    fn drop(&mut self) {
        if let Err(error) = self.join() {
            eprintln!("web shutdown: {error}");
        }
    }
}
