use icem_emulation::control::{
    application::{ErrorCategory, Outcome},
    parse_line,
};
use icem_transport::emulator_control::{
    ClientEnd, Executor, MAX_REPLY_BYTES, client_session, serve_session,
};
use std::{
    io,
    ops::ControlFlow,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    time::Duration,
};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    sync::watch,
};
type Result = std::result::Result<(), Box<dyn std::error::Error>>;
struct Status;
impl Executor for Status {
    fn execute(&self, line: &str) -> icem_emulation::control::ControlReply {
        let command = parse_line(line).map(|c| c.name()).unwrap_or("");
        icem_emulation::control::ControlReply {
            cmd: command.into(),
            json: format!("{{\"ok\":true,\"cmd\":\"{command}\"}}"),
            start_watch: command == "watch",
            quit: command == "quit",
        }
    }
    fn watch_tick(&self) -> String {
        r#"{"ok":true,"cmd":"status"}"#.into()
    }
}
#[tokio::test]
async fn text_and_json_share_ack_sequential_status_and_callback_cancellation() -> Result {
    for command in [" WaTcH ", r#"{"cmd":"watch"}"#] {
        let (client, server) = tokio::io::duplex(256);
        let (stop, cancel) = watch::channel(false);
        let server = tokio::spawn(serve_session(server, Arc::new(Status), cancel));
        let (_stop_client, cancel) = watch::channel(false);
        let mut commands = Vec::new();
        let end = client_session(
            client,
            command,
            tokio::time::Instant::now() + Duration::from_secs(2),
            Duration::from_secs(2),
            cancel,
            |line| {
                let value: serde_json::Value = serde_json::from_str(line)?;
                commands.push(value["cmd"].as_str().unwrap_or("").to_owned());
                Ok(if commands.len() == 3 {
                    ControlFlow::Break(())
                } else {
                    ControlFlow::Continue(())
                })
            },
        )
        .await?;
        assert_eq!(end, ClientEnd::Cancelled);
        assert_eq!(commands, ["watch", "status", "status"]);
        stop.send_replace(true);
        tokio::time::timeout(Duration::from_secs(1), server).await???;
    }
    Ok(())
}
#[tokio::test]
async fn cancellation_during_ack_and_stream_wait_is_prompt() -> Result {
    for ack in [false, true] {
        let (client, mut server) = tokio::io::duplex(256);
        let (stop, cancel) = watch::channel(false);
        let (received, seen) = tokio::sync::oneshot::channel::<()>();
        let task = tokio::spawn(async move {
            let mut received = Some(received);
            client_session(
                client,
                "watch",
                tokio::time::Instant::now() + Duration::from_secs(10),
                Duration::from_secs(10),
                cancel,
                |_| {
                    if let Some(received) = received.take() {
                        let _ = received.send(());
                    }
                    Ok(ControlFlow::Continue(()))
                },
            )
            .await
        });
        if ack {
            server
                .write_all(b"{\"ok\":true,\"cmd\":\"watch\"}\n")
                .await?;
            tokio::time::timeout(Duration::from_secs(1), seen).await??;
        }
        // Cancellation also works without an ACK. No arbitrary sleep/driver dependency.
        stop.send_replace(true);
        assert_eq!(
            tokio::time::timeout(Duration::from_secs(1), task).await???,
            ClientEnd::Cancelled
        );
    }
    Ok(())
}
#[tokio::test]
async fn lost_mutation_reply_is_unknown_and_never_replayed() -> Result {
    let count = Arc::new(AtomicUsize::new(0));
    let (client, server) = tokio::io::duplex(256);
    let called = count.clone();
    let server = tokio::spawn(async move {
        let mut reader = BufReader::new(server);
        let mut line = String::new();
        reader.read_line(&mut line).await?;
        called.fetch_add(1, Ordering::SeqCst);
        // Effect occurred, response disappeared.
        io::Result::Ok(())
    });
    let (_stop, cancel) = watch::channel(false);
    let result = client_session(
        client,
        "setequipment 7",
        tokio::time::Instant::now() + Duration::from_secs(1),
        Duration::from_secs(1),
        cancel,
        |_| Ok(ControlFlow::Continue(())),
    )
    .await;
    let error = result.err().ok_or("expected reply loss")?;
    assert_eq!(error.category, ErrorCategory::Disconnected);
    assert_eq!(error.outcome, Outcome::Unknown);
    server.await??;
    assert_eq!(count.load(Ordering::SeqCst), 1);
    Ok(())
}
#[tokio::test]
async fn malformed_oversized_truncated_and_wrong_stream_frames_are_bounded() -> Result {
    let frames = [
        vec![b'x'; MAX_REPLY_BYTES + 1],
        vec![255, b'\n'],
        b"{}\n".to_vec(),
        b"{\"ok\":true,\"cmd\":\"watch\"}".to_vec(),
        b"{\"ok\":true,\"cmd\":\"ports\"}\n".to_vec(),
    ];
    for frame in frames {
        let (client, mut server) = tokio::io::duplex(512);
        let task = tokio::spawn(async move {
            let _ = server.write_all(&frame).await;
        });
        let (_stop, cancel) = watch::channel(false);
        assert!(
            client_session(
                client,
                "watch",
                tokio::time::Instant::now() + Duration::from_secs(3),
                Duration::from_secs(1),
                cancel,
                |_| Ok(ControlFlow::Continue(()))
            )
            .await
            .is_err()
        );
        task.await?;
    }
    Ok(())
}
#[tokio::test]
async fn deadlines_cover_blocked_write_ack_and_idle_stream() -> Result {
    for (capacity, ack) in [(1, false), (256, false), (256, true)] {
        let (client, mut server) = tokio::io::duplex(capacity);
        if ack {
            server
                .write_all(b"{\"ok\":true,\"cmd\":\"watch\"}\n")
                .await?;
        }
        let (_stop, cancel) = watch::channel(false);
        let result = client_session(
            client,
            "watch",
            tokio::time::Instant::now() + Duration::from_millis(20),
            Duration::from_millis(20),
            cancel,
            |_| Ok(ControlFlow::Continue(())),
        )
        .await;
        assert_eq!(
            result.err().ok_or("expected deadline")?.category,
            ErrorCategory::Deadline
        );
    }
    Ok(())
}
