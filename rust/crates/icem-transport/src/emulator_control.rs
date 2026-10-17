//! Emulator protocol v1, bounded JSON-lines framing and Windows named pipe.
//! The application supplies one serialized engine; transport owns no domain state.
use icem_emulation::control::ControlReply;
use std::{io, sync::Arc, time::Duration};
use tokio::io::{AsyncBufRead, AsyncBufReadExt, AsyncRead, AsyncWrite, AsyncWriteExt, BufReader};
use tokio::sync::watch;

pub const MAX_LINE_BYTES: usize = 65_536;
pub const MAX_REPLY_BYTES: usize = 8 * 1024 * 1024;
pub const ACK_DRAIN: Duration = Duration::from_millis(1500);
const WRITE_TIMEOUT: Duration = Duration::from_secs(3);
/// Calls may block on engine I/O; transport executes them on Tokio's blocking pool.
pub trait Executor: Send + Sync + 'static {
    fn execute(&self, line: &str) -> ControlReply;
    fn watch_tick(&self) -> String;
    /// `quit` must not tear down the pipe until its ACK has been flushed.
    fn reply_flushed(&self, _reply: &ControlReply) {}
}
async fn line(reader: &mut (impl AsyncBufRead + Unpin)) -> io::Result<Option<String>> {
    let mut result = Vec::new();
    loop {
        let buffer = reader.fill_buf().await?;
        if buffer.is_empty() {
            return if result.is_empty() {
                Ok(None)
            } else {
                decode_line(result).map(Some)
            };
        }
        let count = buffer
            .iter()
            .position(|b| *b == b'\n')
            .map_or(buffer.len(), |i| i + 1);
        if result.len() + count > MAX_LINE_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "control line exceeds 65536 bytes",
            ));
        }
        let complete = buffer[count - 1] == b'\n';
        result.extend_from_slice(&buffer[..count]);
        reader.consume(count);
        if complete {
            return decode_line(result).map(Some);
        }
    }
}
fn decode_line(mut bytes: Vec<u8>) -> io::Result<String> {
    if bytes.last() == Some(&b'\n') {
        bytes.pop();
    }
    if bytes.last() == Some(&b'\r') {
        bytes.pop();
    }
    String::from_utf8(bytes).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))
}
async fn write_line(writer: &mut (impl AsyncWrite + Unpin), json: &str) -> io::Result<()> {
    // Only a complete JSON line may escape; responses are allowed to contain the
    // original full catalog (larger than a command), with a separate 8 MiB cap.
    if json.len() + 2 > MAX_REPLY_BYTES || json.contains(['\r', '\n']) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid control reply framing",
        ));
    }
    tokio::time::timeout(WRITE_TIMEOUT, async {
        writer.write_all(json.as_bytes()).await?;
        writer.write_all(b"\r\n").await?;
        writer.flush().await
    })
    .await
    .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "control write timeout"))?
}
/// Shared by Windows pipes and in-memory duplex contract tests.
pub async fn serve_session<S: AsyncRead + AsyncWrite + Unpin>(
    stream: S,
    executor: Arc<dyn Executor>,
    mut stop: watch::Receiver<bool>,
) -> io::Result<()> {
    let (input, mut output) = tokio::io::split(stream);
    let mut input = BufReader::new(input);
    loop {
        if *stop.borrow() {
            return Ok(());
        }
        let command = tokio::select! {
            biased;
            _ = stop.changed() => return Ok(()),
            line = line(&mut input) => match line? { Some(line) => line, None => return Ok(()) },
        };
        let worker = Arc::clone(&executor);
        let reply = tokio::task::spawn_blocking(move || worker.execute(&command))
            .await
            .map_err(io::Error::other)?;
        write_line(&mut output, &reply.json).await?;
        executor.reply_flushed(&reply);
        if reply.quit {
            return Ok(());
        }
        if reply.start_watch {
            loop {
                tokio::select! {
                    biased;
                    _ = stop.changed() => return Ok(()),
                    _ = tokio::time::sleep(Duration::from_secs(1)) => {},
                }
                let worker = Arc::clone(&executor);
                let tick = tokio::task::spawn_blocking(move || worker.watch_tick())
                    .await
                    .map_err(io::Error::other)?;
                write_line(&mut output, &tick).await?;
            }
        }
    }
}
#[cfg(windows)]
pub use windows::{ControlServer, request};
#[cfg(windows)]
mod windows {
    use super::*;
    use std::thread::{self, JoinHandle};
    use tokio::io::AsyncReadExt;
    use tokio::{
        net::windows::named_pipe::{ClientOptions, ServerOptions},
        runtime::Builder,
        sync::oneshot,
        task::JoinSet,
    };
    fn pipe_path(name: &str) -> io::Result<String> {
        if name.is_empty() || name.contains(['\\', '/', '\0']) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid local pipe name",
            ));
        }
        Ok(format!(r"\\.\pipe\{name}"))
    }
    pub struct ControlServer {
        stop: Option<oneshot::Sender<()>>,
        thread: Option<JoinHandle<io::Result<()>>>,
    }
    impl ControlServer {
        /// Reserve the first instance synchronously. A second engine fails bind
        /// instead of sharing command traffic with the running instance.
        pub fn bind(name: &str, executor: Arc<dyn Executor>) -> io::Result<Self> {
            let path = pipe_path(name)?;
            let runtime = Builder::new_current_thread().enable_all().build()?;
            let initial = {
                let _entered = runtime.enter();
                ServerOptions::new()
                    .first_pipe_instance(true)
                    .reject_remote_clients(true)
                    .create(&path)?
            };
            let (sender, receiver) = oneshot::channel();
            let thread = thread::Builder::new().name("emulator-control-pipe".into()).spawn(move || runtime.block_on(async move {
                let (stop, _) = watch::channel(false);
                let mut receiver = receiver;
                let mut pending = initial;
                let mut clients = JoinSet::new();
                let result = loop {
                    // Cap active sessions, while always retaining one listening
                    // instance; no detached tasks or unbounded accepted queue.
                    if clients.len() >= 32 {
                        tokio::select! { _ = &mut receiver => break Ok(()), _ = clients.join_next() => {} }
                        continue;
                    }
                    tokio::select! {
                        biased;
                        _ = &mut receiver => break Ok(()),
                        result = pending.connect() => {
                            if let Err(error) = result { break Err(error); }
                            let next = match ServerOptions::new().reject_remote_clients(true).create(&path) {
                                Ok(next) => next, Err(error) => break Err(error),
                            };
                            let connected = std::mem::replace(&mut pending,next);
                            let executor = Arc::clone(&executor); let cancel = stop.subscribe();
                            clients.spawn(async move { let _ = serve_session(connected,executor,cancel).await; });
                        }
                        _ = clients.join_next(), if !clients.is_empty() => {},
                    }
                };
                stop.send_replace(true);
                // Current ACK/write gets a bounded drain; idle/watch readers wake
                // immediately. Abort and join leftovers before releasing runtime.
                if tokio::time::timeout(ACK_DRAIN, async { while clients.join_next().await.is_some() {} }).await.is_err() {
                    clients.shutdown().await;
                }
                result
            }))?;
            Ok(Self {
                stop: Some(sender),
                thread: Some(thread),
            })
        }
        pub fn stop(mut self) -> io::Result<()> {
            self.stop_inner()
        }
        fn stop_inner(&mut self) -> io::Result<()> {
            if let Some(stop) = self.stop.take() {
                let _ = stop.send(());
            }
            if let Some(thread) = self.thread.take() {
                thread
                    .join()
                    .map_err(|_| io::Error::other("control pipe thread panicked"))??;
            }
            Ok(())
        }
    }
    impl Drop for ControlServer {
        fn drop(&mut self) {
            let _ = self.stop_inner();
        }
    }
    /// One round trip; timeout includes busy-pipe connect, write and response.
    /// Never retries an accepted command (toggle/start commands may have effects).
    pub fn request(name: &str, command: &str, timeout: Duration) -> io::Result<String> {
        let path = pipe_path(name)?;
        if command.len() + 2 > MAX_LINE_BYTES || command.contains(['\r', '\n']) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid command framing",
            ));
        }
        Builder::new_current_thread()
            .enable_all()
            .build()?
            .block_on(async {
                tokio::time::timeout(timeout, async {
                    let pipe = loop {
                        match ClientOptions::new().open(&path) {
                            Ok(pipe) => break pipe,
                            Err(error) if error.raw_os_error() == Some(231) => {
                                tokio::time::sleep(Duration::from_millis(10)).await
                            }
                            Err(error) => return Err(error),
                        }
                    };
                    let (read, mut write) = tokio::io::split(pipe);
                    write_line(&mut write, command).await?;
                    // Full vars/status replies need a larger limit than requests.
                    let mut reader = BufReader::new(read).take(MAX_REPLY_BYTES as u64 + 1);
                    let mut response = String::new();
                    let count = reader.read_line(&mut response).await?;
                    if count == 0 {
                        return Err(io::Error::new(
                            io::ErrorKind::UnexpectedEof,
                            "connection closed before reply",
                        ));
                    }
                    if count > MAX_REPLY_BYTES || !response.ends_with('\n') {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "invalid control reply",
                        ));
                    }
                    Ok(response.trim_end_matches(['\r', '\n']).into())
                })
                .await
                .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "control request timeout"))?
            })
    }
}
