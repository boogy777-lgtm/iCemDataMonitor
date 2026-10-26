use icem_emulation::control::{ControlReply, to_json};
use icem_transport::emulator_control::{Executor, MAX_LINE_BYTES, serve_session};
use std::{
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    sync::watch,
};
#[derive(Default)]
struct Commands {
    lines: Mutex<Vec<String>>,
    quit_ack: AtomicBool,
}
impl Executor for Commands {
    fn execute(&self, line: &str) -> ControlReply {
        self.lines
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .push(line.into());
        ControlReply {
            cmd: line.into(),
            json: to_json(true, line, None, None),
            start_watch: line == "watch",
            quit: line == "quit",
        }
    }
    fn watch_tick(&self) -> String {
        to_json(true, "status", None, None)
    }
    fn reply_flushed(&self, r: &ControlReply) {
        if r.quit {
            self.quit_ack.store(true, Ordering::SeqCst);
        }
    }
}
type Result = std::result::Result<(), Box<dyn std::error::Error>>;
#[tokio::test]
async fn fragmented_and_coalesced_commands_quit_ack_before_eof() -> Result {
    let (client, server) = tokio::io::duplex(128);
    let (_stop, cancel) = watch::channel(false);
    let executor = Arc::new(Commands::default());
    let task = tokio::spawn(serve_session(server, executor.clone(), cancel));
    let (read, mut write) = tokio::io::split(client);
    let mut read = BufReader::new(read);
    let mut response = String::new();
    write.write_all(b"sta").await?;
    write.write_all(b"tus\r\nports\nquit\nignored\n").await?;
    for expected in ["status", "ports", "quit"] {
        response.clear();
        assert!(read.read_line(&mut response).await? > 0);
        assert!(
            response.ends_with("\r\n"),
            "preserve Windows StreamWriter framing"
        );
        assert!(response.contains(&format!("\"cmd\":\"{expected}\"")));
    }
    response.clear();
    assert_eq!(read.read_line(&mut response).await?, 0);
    task.await??;
    assert!(executor.quit_ack.load(Ordering::SeqCst));
    assert_eq!(
        *executor.lines.lock().map_err(|_| "poisoned")?,
        ["status", "ports", "quit"]
    );
    Ok(())
}
#[tokio::test]
async fn watch_ack_then_one_hz_status_and_cancel_idle_reader() -> Result {
    let (mut client, server) = tokio::io::duplex(256);
    let (stop, cancel) = watch::channel(false);
    let executor = Arc::new(Commands::default());
    let task = tokio::spawn(serve_session(server, executor, cancel));
    client.write_all(b"watch\n").await?;
    let mut read = BufReader::new(client);
    let mut response = String::new();
    read.read_line(&mut response).await?;
    assert!(response.contains("watch"));
    response.clear();
    tokio::time::timeout(Duration::from_millis(1500), read.read_line(&mut response)).await??;
    assert!(response.contains("status"));
    stop.send(true)?;
    tokio::time::timeout(Duration::from_millis(200), task).await???;
    let (_client, server) = tokio::io::duplex(16);
    let (stop, cancel) = watch::channel(false);
    let task = tokio::spawn(serve_session(server, Arc::new(Commands::default()), cancel));
    stop.send(true)?;
    tokio::time::timeout(Duration::from_millis(200), task).await???;
    Ok(())
}
#[tokio::test]
async fn oversized_and_invalid_utf8_rejected_before_execution() -> Result {
    for bytes in [vec![b'x'; MAX_LINE_BYTES + 1], vec![255, b'\n']] {
        let (mut client, server) = tokio::io::duplex(MAX_LINE_BYTES + 2);
        let (_stop, cancel) = watch::channel(false);
        let executor = Arc::new(Commands::default());
        let task = tokio::spawn(serve_session(server, executor.clone(), cancel));
        client.write_all(&bytes).await?;
        assert!(task.await?.is_err());
        assert!(executor.lines.lock().map_err(|_| "poisoned")?.is_empty());
    }
    Ok(())
}
