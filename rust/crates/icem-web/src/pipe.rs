//! Local HTTP/2 gRPC on the original Host pipe; no alternate JSON control protocol.
use crate::WebState;
use std::{
    io,
    sync::mpsc,
    thread::{self, JoinHandle},
};
use tokio::{
    net::windows::named_pipe::{NamedPipeServer, ServerOptions},
    sync::watch,
};

pub const HOST_PIPE: &str = "iCemDataMonitor.Host";
struct Listener {
    pending: NamedPipeServer,
    path: String,
}
impl axum::serve::Listener for Listener {
    type Io = NamedPipeServer;
    type Addr = ();
    async fn accept(&mut self) -> (Self::Io, ()) {
        loop {
            if let Err(error) = self.pending.connect().await {
                eprintln!("host pipe connect: {error}");
                let _ = self.pending.disconnect();
                tokio::time::sleep(std::time::Duration::from_millis(50)).await;
                continue;
            }
            loop {
                match ServerOptions::new()
                    .reject_remote_clients(true)
                    .max_instances(32)
                    .create(&self.path)
                {
                    Ok(next) => return (std::mem::replace(&mut self.pending, next), ()),
                    Err(error) => {
                        eprintln!("host pipe next instance: {error}");
                        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
                    }
                }
            }
        }
    }
    fn local_addr(&self) -> io::Result<()> {
        Ok(())
    }
}
pub struct LocalRpcServer {
    stop: watch::Sender<bool>,
    thread: Option<JoinHandle<io::Result<()>>>,
}
impl LocalRpcServer {
    pub fn bind(name: &str, state: WebState) -> io::Result<Self> {
        if name.is_empty() || name.contains(['\\', '/', '\0']) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid local pipe name",
            ));
        }
        let path = format!(r"\\.\pipe\{name}");
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()?;
        let pending = {
            let _entered = runtime.enter();
            ServerOptions::new()
                .first_pipe_instance(true)
                .reject_remote_clients(true)
                .max_instances(32)
                .create(&path)?
        };
        let (stop, mut stopping) = watch::channel(false);
        let (ready, wait) = mpsc::sync_channel(1);
        let thread = thread::Builder::new()
            .name("host-grpc-pipe".into())
            .spawn(move || {
                runtime.block_on(async move {
                    let router = axum::Router::new().route_service(
                        "/icemdatamonitor.v1.TelemetryService/{method}",
                        crate::rpc::service(state),
                    );
                    let serving = axum::serve(Listener { pending, path }, router).into_future();
                    tokio::pin!(serving);
                    let _ = ready.send(());
                    tokio::select! {result=&mut serving=>result,_=stopping.changed()=>Ok(())}
                })
            })?;
        wait.recv()
            .map_err(|_| io::Error::other("host pipe stopped before ready"))?;
        Ok(Self {
            stop,
            thread: Some(thread),
        })
    }
    pub fn stop(mut self) -> io::Result<()> {
        self.join()
    }
    fn join(&mut self) -> io::Result<()> {
        self.stop.send_replace(true);
        if let Some(thread) = self.thread.take() {
            thread
                .join()
                .map_err(|_| io::Error::other("host pipe panicked"))??;
        }
        Ok(())
    }
}
impl Drop for LocalRpcServer {
    fn drop(&mut self) {
        if let Err(e) = self.join() {
            eprintln!("host pipe shutdown: {e}");
        }
    }
}
