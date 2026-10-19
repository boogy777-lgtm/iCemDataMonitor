//! Emulator protocol v1, bounded JSON-lines framing and Windows named pipe.
//! The application supplies one serialized engine; transport owns no domain state.
use icem_emulation::control::{
    Command, ControlReply,
    application::{ErrorCategory, Outcome},
    parse_line,
};
use std::ops::ControlFlow;
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
    bounded_line(reader, MAX_LINE_BYTES, false).await
}
async fn bounded_line(
    reader: &mut (impl AsyncBufRead + Unpin),
    limit: usize,
    require_newline: bool,
) -> io::Result<Option<String>> {
    let mut result = Vec::new();
    loop {
        let buffer = reader.fill_buf().await?;
        if buffer.is_empty() {
            return if result.is_empty() {
                Ok(None)
            } else if require_newline {
                Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "incomplete control reply",
                ))
            } else {
                decode_line(result).map(Some)
            };
        }
        let count = buffer
            .iter()
            .position(|b| *b == b'\n')
            .map_or(buffer.len(), |i| i + 1);
        if result.len() + count > limit {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "control line exceeds byte limit",
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ClientEnd {
    Reply { ok: bool },
    Cancelled,
}
#[derive(Debug)]
pub struct ClientError {
    pub category: ErrorCategory,
    pub outcome: Outcome,
    pub message: String,
}
impl std::fmt::Display for ClientError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{}: {} ({:?})",
            self.category.code(),
            self.message,
            self.outcome
        )
    }
}
impl std::error::Error for ClientError {}
impl ClientError {
    fn io(error: io::Error, unknown: bool) -> Self {
        let category = match error.kind() {
            io::ErrorKind::TimedOut => ErrorCategory::Deadline,
            io::ErrorKind::InvalidInput => ErrorCategory::InvalidInput,
            io::ErrorKind::InvalidData => ErrorCategory::Protocol,
            io::ErrorKind::UnexpectedEof
            | io::ErrorKind::BrokenPipe
            | io::ErrorKind::ConnectionReset => ErrorCategory::Disconnected,
            _ => ErrorCategory::Unavailable,
        };
        Self {
            category,
            outcome: if unknown {
                Outcome::Unknown
            } else {
                Outcome::Rejected
            },
            message: error.to_string(),
        }
    }
}
fn validate_command(command: &str) -> io::Result<()> {
    if command.len() + 2 > MAX_LINE_BYTES || command.contains(['\r', '\n']) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid command framing",
        ));
    }
    Ok(())
}
async fn reply_line(reader: &mut (impl AsyncBufRead + Unpin)) -> io::Result<String> {
    bounded_line(reader, MAX_REPLY_BYTES, true)
        .await?
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "connection closed before reply",
            )
        })
}
fn reply_ok(line: &str, expected: Option<&str>) -> io::Result<bool> {
    let value: serde_json::Value =
        serde_json::from_str(line).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
    let ok = value["ok"]
        .as_bool()
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "reply has no boolean ok"))?;
    let cmd = value["cmd"]
        .as_str()
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "reply has no command"))?;
    if expected.is_some_and(|expected| expected != cmd) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "unexpected stream reply",
        ));
    }
    Ok(ok)
}
/// Shared one-shot/subscription consumer. The first deadline includes write + ACK;
/// Windows supplies a deadline established BEFORE connect. No mutation is retried.
pub async fn client_session<S: AsyncRead + AsyncWrite + Unpin>(
    stream: S,
    command: &str,
    deadline: tokio::time::Instant,
    idle: Duration,
    mut cancel: watch::Receiver<bool>,
    mut receive: impl FnMut(&str) -> io::Result<ControlFlow<()>>,
) -> Result<ClientEnd, ClientError> {
    validate_command(command).map_err(|e| ClientError::io(e, false))?;
    if *cancel.borrow() {
        return Ok(ClientEnd::Cancelled);
    }
    let parsed = parse_line(command).ok();
    let mutation = parsed.as_ref().is_none_or(Command::is_mutation);
    let watching = matches!(parsed, Some(Command::Watch));
    let expected = parsed.as_ref().map(Command::name);
    let (input, mut output) = tokio::io::split(stream);
    let mut input = BufReader::new(input);
    let mut attempted = false;
    let first = tokio::select! {
        biased;
        _ = cancel.changed() => return if attempted && mutation { Err(ClientError { category: ErrorCategory::Cancelled, outcome: Outcome::Unknown, message: "request cancelled after write attempt".into() }) } else { Ok(ClientEnd::Cancelled) },
        first = tokio::time::timeout_at(deadline, async {
            attempted = true; // even a failed/partial write cannot prove absence of effect
            write_line(&mut output, command).await?;
            reply_line(&mut input).await
        }) => first.map_err(|_| ClientError::io(io::Error::new(io::ErrorKind::TimedOut, "control request deadline"), attempted && mutation))?
            .map_err(|e| ClientError::io(e, attempted && mutation))?,
    };
    let ok = reply_ok(&first, expected).map_err(|e| ClientError::io(e, mutation))?;
    if receive(&first)
        .map_err(|e| ClientError::io(e, mutation))?
        .is_break()
    {
        return Ok(ClientEnd::Cancelled);
    }
    if !ok || !watching {
        return Ok(ClientEnd::Reply { ok });
    }
    loop {
        if *cancel.borrow() {
            return Ok(ClientEnd::Cancelled);
        }
        let next = tokio::select! {
            biased;
            _ = cancel.changed() => return Ok(ClientEnd::Cancelled),
            next = tokio::time::timeout(idle, reply_line(&mut input)) => next
                .map_err(|_| ClientError::io(io::Error::new(io::ErrorKind::TimedOut, "watch data deadline"), false))?
                .map_err(|e| ClientError::io(e, false))?,
        };
        let ok = reply_ok(&next, Some("status")).map_err(|e| ClientError::io(e, false))?;
        if receive(&next)
            .map_err(|e| ClientError::io(e, false))?
            .is_break()
        {
            return Ok(ClientEnd::Cancelled);
        }
        if !ok {
            return Ok(ClientEnd::Reply { ok: false });
        }
    }
}
#[cfg(windows)]
pub use windows::{ControlServer, cli_request, request, stream_request};
#[cfg(windows)]
mod windows {
    use super::*;
    use std::thread::{self, JoinHandle};
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

    async fn connect_and_exchange(
        path: &str,
        command: &str,
        timeout: Duration,
        idle: Duration,
        mut cancel: watch::Receiver<bool>,
        receive: impl FnMut(&str) -> io::Result<ControlFlow<()>>,
    ) -> Result<ClientEnd, ClientError> {
        validate_command(command).map_err(|e| ClientError::io(e, false))?;
        let deadline = tokio::time::Instant::now() + timeout;
        let connect = async {
            loop {
                match ClientOptions::new().open(path) {
                    Ok(pipe) => return Ok(pipe),
                    Err(error) if error.raw_os_error() == Some(231) => {
                        tokio::time::sleep(Duration::from_millis(10)).await
                    }
                    Err(error) => return Err(error),
                }
            }
        };
        if *cancel.borrow() {
            return Ok(ClientEnd::Cancelled);
        }
        let pipe = tokio::select! {
            biased;
            _ = cancel.changed() => return Ok(ClientEnd::Cancelled),
            pipe = tokio::time::timeout_at(deadline, connect) => pipe
                .map_err(|_| ClientError::io(io::Error::new(io::ErrorKind::TimedOut, "control connect deadline"), false))?
                .map_err(|e| ClientError::io(e, false))?,
        };
        client_session(pipe, command, deadline, idle, cancel, receive).await
    }
    /// Common client: callback sees validated ACK followed by bounded status lines.
    /// Cancellation drops the existing pipe; never launches/reconnects/replays.
    pub fn stream_request(
        name: &str,
        command: &str,
        timeout: Duration,
        idle: Duration,
        cancel: watch::Receiver<bool>,
        receive: impl FnMut(&str) -> io::Result<ControlFlow<()>>,
    ) -> Result<ClientEnd, ClientError> {
        let path = pipe_path(name).map_err(|e| ClientError::io(e, false))?;
        Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|e| ClientError::io(e, false))?
            .block_on(connect_and_exchange(
                &path, command, timeout, idle, cancel, receive,
            ))
    }
    /// CLI watch additionally observes Ctrl+C. One-shot calls preserve the same client path.
    pub fn cli_request(
        name: &str,
        command: &str,
        receive: impl FnMut(&str) -> io::Result<ControlFlow<()>>,
    ) -> Result<ClientEnd, ClientError> {
        let path = pipe_path(name).map_err(|e| ClientError::io(e, false))?;
        let (_cancel, cancelled) = watch::channel(false);
        Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|e| ClientError::io(e, false))?
            .block_on(async {
                let exchange = connect_and_exchange(
                    &path,
                    command,
                    Duration::from_secs(3),
                    Duration::from_secs(3),
                    cancelled,
                    receive,
                );
                if matches!(parse_line(command), Ok(Command::Watch)) {
                    tokio::select! {
                        result = exchange => result,
                        signal = tokio::signal::ctrl_c() => {
                            signal.map_err(|e| ClientError::io(e, false))?;
                            Ok(ClientEnd::Cancelled)
                        }
                    }
                } else {
                    exchange.await
                }
            })
    }
    /// Compatibility one-response helper, including watch ACK only. Whole-operation deadline preserved.
    pub fn request(name: &str, command: &str, timeout: Duration) -> io::Result<String> {
        let (_cancel, cancelled) = watch::channel(false);
        let mut response = None;
        stream_request(name, command, timeout, timeout, cancelled, |line| {
            response = Some(line.to_owned());
            Ok(ControlFlow::Break(()))
        })
        .map_err(|e| {
            io::Error::new(
                match e.category {
                    ErrorCategory::Deadline => io::ErrorKind::TimedOut,
                    ErrorCategory::InvalidInput => io::ErrorKind::InvalidInput,
                    ErrorCategory::Protocol => io::ErrorKind::InvalidData,
                    ErrorCategory::Disconnected => io::ErrorKind::UnexpectedEof,
                    _ => io::ErrorKind::Other,
                },
                e,
            )
        })?;
        response.ok_or_else(|| io::Error::other("request cancelled"))
    }
}
