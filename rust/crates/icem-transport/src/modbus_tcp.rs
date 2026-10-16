//! Bounded Modbus TCP I/O. Polling is a portable development transport, pending
//! Windows load/latency acceptance; the pure register protocol has no I/O dependency.
use icem_protocols::modbus::RegisterBank;
use std::io::{self, Read, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;

const MAX_BODY: usize = 254;
const MAX_ADU: usize = 6 + MAX_BODY;

#[derive(Debug, Clone, Copy)]
pub struct ServerOptions {
    pub max_connections: usize,
    pub poll_interval: Duration,
}
impl Default for ServerOptions {
    fn default() -> Self {
        Self {
            max_connections: 64,
            poll_interval: Duration::from_millis(2),
        }
    }
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct ServerStats {
    pub accepted: u64,
    pub rejected: u64,
    pub failed_sessions: u64,
    pub requests: u64,
}
#[derive(Default)]
struct Counters {
    accepted: AtomicU64,
    rejected: AtomicU64,
    failed_sessions: AtomicU64,
    requests: AtomicU64,
}
impl Counters {
    fn snapshot(&self) -> ServerStats {
        ServerStats {
            accepted: self.accepted.load(Ordering::Relaxed),
            rejected: self.rejected.load(Ordering::Relaxed),
            failed_sessions: self.failed_sessions.load(Ordering::Relaxed),
            requests: self.requests.load(Ordering::Relaxed),
        }
    }
}

/// Owns the listener thread. Bind failure is synchronous; drop stops and joins it.
pub struct ModbusServer {
    address: SocketAddr,
    shutdown: Arc<AtomicBool>,
    counters: Arc<Counters>,
    thread: Option<JoinHandle<io::Result<()>>>,
}
impl ModbusServer {
    pub fn bind(
        address: SocketAddr,
        bank: Arc<Mutex<RegisterBank>>,
        options: ServerOptions,
    ) -> io::Result<Self> {
        if !(1..=1024).contains(&options.max_connections)
            || options.poll_interval < Duration::from_millis(1)
            || options.poll_interval > Duration::from_millis(100)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid Modbus resource limits",
            ));
        }
        let listener = TcpListener::bind(address)?;
        listener.set_nonblocking(true)?;
        let address = listener.local_addr()?;
        let shutdown = Arc::new(AtomicBool::new(false));
        let counters = Arc::new(Counters::default());
        let stop = Arc::clone(&shutdown);
        let counts = Arc::clone(&counters);
        let thread = thread::Builder::new()
            .name("icem-modbus".into())
            .spawn(move || serve(listener, bank, options, &stop, &counts))?;
        Ok(Self {
            address,
            shutdown,
            counters,
            thread: Some(thread),
        })
    }
    pub fn local_addr(&self) -> SocketAddr {
        self.address
    }
    pub fn stats(&self) -> ServerStats {
        self.counters.snapshot()
    }
    pub fn stop(mut self) -> io::Result<ServerStats> {
        self.join()?;
        Ok(self.stats())
    }
    fn join(&mut self) -> io::Result<()> {
        self.shutdown.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            thread
                .join()
                .map_err(|_| io::Error::other("Modbus listener thread panicked"))??;
        }
        Ok(())
    }
}
impl Drop for ModbusServer {
    fn drop(&mut self) {
        if let Err(error) = self.join() {
            eprintln!("Modbus shutdown: {error}");
        }
    }
}

fn serve(
    listener: TcpListener,
    bank: Arc<Mutex<RegisterBank>>,
    options: ServerOptions,
    shutdown: &AtomicBool,
    counters: &Counters,
) -> io::Result<()> {
    let mut sessions: Vec<Session> = Vec::new();
    while !shutdown.load(Ordering::Acquire) {
        // Bound accept work as well as sessions, so connection floods cannot starve requests/stop.
        for _ in 0..options.max_connections {
            match listener.accept() {
                Ok((stream, _)) => {
                    if sessions.len() >= options.max_connections {
                        counters.rejected.fetch_add(1, Ordering::Relaxed);
                        continue; // drops the excess socket
                    }
                    stream.set_nonblocking(true)?;
                    stream.set_nodelay(true)?;
                    sessions.push(Session::new(stream));
                    counters.accepted.fetch_add(1, Ordering::Relaxed);
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => break,
                Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
                Err(error) => return Err(error),
            }
        }
        let mut index = 0;
        while index < sessions.len() {
            match sessions[index].pump(&bank, counters) {
                Ok(true) => index += 1,
                Ok(false) => {
                    sessions.swap_remove(index);
                }
                Err(_) => {
                    counters.failed_sessions.fetch_add(1, Ordering::Relaxed);
                    sessions.swap_remove(index);
                }
            }
        }
        thread::sleep(options.poll_interval);
    }
    Ok(()) // listener and all session sockets close here, before join returns
}

struct Session {
    stream: TcpStream,
    input: Vec<u8>,
    output: Vec<u8>,
    written: usize,
}
impl Session {
    fn new(stream: TcpStream) -> Self {
        Self {
            stream,
            input: Vec::with_capacity(MAX_ADU * 2),
            output: Vec::new(),
            written: 0,
        }
    }
    fn frame_length(&self) -> io::Result<Option<usize>> {
        if self.input.len() < 6 {
            return Ok(None);
        }
        let length = usize::from(u16::from_be_bytes([self.input[4], self.input[5]]));
        if !(2..=MAX_BODY).contains(&length) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "MBAP length outside 2..254",
            ));
        }
        Ok(Some(6 + length))
    }
    fn pump(&mut self, bank: &Mutex<RegisterBank>, counters: &Counters) -> io::Result<bool> {
        // One pending response provides backpressure to a client that does not read.
        if self.output.is_empty() {
            let complete = self
                .frame_length()?
                .is_some_and(|length| self.input.len() >= length);
            if !complete {
                let mut chunk = [0; MAX_ADU];
                match self.stream.read(&mut chunk) {
                    Ok(0) => return Ok(false),
                    Ok(count) => self.input.extend_from_slice(&chunk[..count]),
                    Err(error)
                        if matches!(
                            error.kind(),
                            io::ErrorKind::WouldBlock | io::ErrorKind::Interrupted
                        ) => {}
                    Err(error) => return Err(error),
                }
            }
            if let Some(length) = self.frame_length()? {
                if self.input.len() >= length {
                    let response = bank
                        .lock()
                        .map_err(|_| io::Error::other("register mirror lock poisoned"))?
                        .process(&self.input[6..length]);
                    self.output.extend_from_slice(&self.input[..4]); // preserve TID and legacy PID echo
                    let response_len = u16::try_from(response.len())
                        .map_err(|_| io::Error::other("oversized Modbus response"))?;
                    self.output.extend_from_slice(&response_len.to_be_bytes());
                    self.output.extend_from_slice(&response);
                    self.input.drain(..length);
                    counters.requests.fetch_add(1, Ordering::Relaxed);
                }
            }
        }
        if !self.output.is_empty() {
            match self.stream.write(&self.output[self.written..]) {
                Ok(0) => return Ok(false),
                Ok(count) => self.written += count,
                Err(error)
                    if matches!(
                        error.kind(),
                        io::ErrorKind::WouldBlock | io::ErrorKind::Interrupted
                    ) => {}
                Err(error) => return Err(error),
            }
            if self.written == self.output.len() {
                self.output.clear();
                self.written = 0;
            }
        }
        Ok(true)
    }
}
