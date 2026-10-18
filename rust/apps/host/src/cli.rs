//! CLI dispatch precedes singleton/config/tray. Each mutation has exactly one attempt.
use icem_web::{
    client,
    host_client::{self, Launch, Presence, READY_TIMEOUT, StartPolicy},
    proto::*,
};
use std::time::{Duration, Instant};
enum Command {
    Snapshot,
    Header,
    Get(i32),
    Stop,
    Restart,
}
fn parse(args: &[String]) -> Result<(Command, bool), String> {
    let start = args.iter().any(|s| s == "--start");
    let words: Vec<_> = args
        .iter()
        .filter(|s| s.as_str() != "--start")
        .map(String::as_str)
        .collect();
    let command = match words.as_slice() {
        ["snapshot"] => Command::Snapshot,
        ["header"] => Command::Header,
        ["get", "main"] => Command::Get(1),
        ["get", "lan"] => Command::Get(2),
        ["get", "raw"] => Command::Get(3),
        ["stop"] => Command::Stop,
        ["restart"] => Command::Restart,
        _ => {
            return Err(
                "Usage: icem-host-dev [--start] snapshot|get main|lan|raw|header; stop|restart"
                    .into(),
            );
        }
    };
    Ok((command, start))
}
pub fn dispatch(args: &[String]) -> Option<u8> {
    if !args.iter().any(|a| {
        matches!(
            a.as_str(),
            "snapshot" | "header" | "get" | "stop" | "restart" | "--start"
        )
    }) {
        return None;
    }
    if args.iter().any(|a| matches!(a.as_str(), "--help" | "-h")) {
        eprintln!("icem-host-dev [--start] snapshot|get main|lan|raw|header; stop|restart");
        return Some(0);
    }
    let result = (|| {
        let (command, start) = parse(args).map_err(|e| (2, e))?;
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|e| (1, e.to_string()))?;
        runtime.block_on(run(command, start))
    })();
    Some(match result {
        Ok(json) => {
            println!("{json}");
            0
        }
        Err((code, error)) => {
            eprintln!("{error}");
            code
        }
    })
}
async fn run(command: Command, start: bool) -> Result<String, (u8, String)> {
    let pipe = icem_web::HOST_PIPE;
    if matches!(command, Command::Stop) {
        let signalled =
            icem_windows_native::signal_existing_event(icem_platform_windows::HOST_SHUTDOWN)
                .map_err(|e| (1, e.to_string()))?;
        return Ok(format!(
            "{{\"action\":\"stop\",\"result\":\"{}\"}}",
            if signalled { "signalled" } else { "notRunning" }
        ));
    }
    if matches!(command, Command::Restart) {
        // Keep an old-owner stream. Its disconnect proves teardown even if the
        // successor rebinds between probes; a fixed sleep cannot prove that.
        let old = tokio::time::timeout(Duration::from_secs(3), async {
            let mut rpc = client::connect(pipe).await.map_err(|e| e.to_string())?;
            rpc.subscribe_boot_stages(BootStreamRequest {})
                .await
                .map(|r| r.into_inner())
                .map_err(|e| e.to_string())
        })
        .await
        .ok()
        .and_then(Result::ok);
        let deadline = Instant::now() + READY_TIMEOUT;
        let signalled =
            icem_windows_native::signal_existing_event(icem_platform_windows::HOST_RESTART)
                .map_err(|e| (1, e.to_string()))?;
        if signalled {
            let mut old = old.ok_or_else(|| {
                (
                    2,
                    "Перезапуск отправлен один раз; не удалось наблюдать остановку прежнего Host"
                        .into(),
                )
            })?;
            tokio::time::timeout(deadline.saturating_duration_since(Instant::now()), async {
                while let Ok(Some(_)) = old.message().await {} // EOF/error on this old stream; no reconnect.
            })
            .await
            .map_err(|_| {
                (
                    2,
                    "Host не завершил прежнее соединение после restart".into(),
                )
            })?;
            drop(old);
            host_client::ensure_with(
                || host_client::probe(pipe),
                || Ok(None),
                StartPolicy::Wait,
                deadline.saturating_duration_since(Instant::now()),
            )
            .await
            .map_err(|e| (2, e))?;
            return Ok("{\"action\":\"restart\",\"result\":\"restarted\"}".into());
        }
        let presence = tokio::time::timeout(Duration::from_secs(1), host_client::probe(pipe))
            .await
            .unwrap_or(Presence::Absent);
        if presence != Presence::Absent {
            return Err((
                1,
                "Host отвечает, но restart event отсутствует. Перезапустите Host из трея.".into(),
            ));
        }
        let launch = Launch::installed().map_err(|e| (2, e))?;
        host_client::ensure(pipe, Some(&launch), READY_TIMEOUT)
            .await
            .map_err(|e| (2, e))?;
        return Ok("{\"action\":\"restart\",\"result\":\"spawned\"}".into());
    }
    let launch = if start {
        Some(Launch::installed().map_err(|e| (2, e))?)
    } else {
        None
    };
    host_client::ensure(pipe, launch.as_ref(), READY_TIMEOUT)
        .await
        .map_err(|e| (2, e))?;
    let mut rpc = client::connect(pipe)
        .await
        .map_err(|e| (1, e.to_string()))?;
    match command {
        Command::Header => {
            let s = rpc
                .get_status(HostStatusRequest {})
                .await
                .map_err(|e| (1, e.to_string()))?
                .into_inner();
            Ok(format!(
                "{{\"comPortActive\":{},\"caseState\":{},\"caseLanState\":{},\"heartbeat\":{}}}",
                i32::from(s.com_port > 0),
                s.case_state,
                s.case_lan_state,
                s.heartbeat
            ))
        }
        Command::Snapshot | Command::Get(_) => {
            let zone = if let Command::Get(zone) = command {
                zone
            } else {
                0
            };
            let reply = rpc
                .get_registers(RegistersRequest { zone })
                .await
                .map_err(|e| (1, e.to_string()))?
                .into_inner();
            project(&reply, zone == 0).map_err(|e| (1, e))
        }
        Command::Stop | Command::Restart => Err((1, "lifecycle dispatch invariant".into())),
    }
}
fn project(reply: &RegistersReply, snapshot: bool) -> Result<String, String> {
    use register_value::Value;
    if snapshot {
        let mut words = Vec::with_capacity(600);
        for value in &reply.values {
            let bits = match value.value {
                Some(Value::Int32Value(n)) => n.cast_unsigned(),
                Some(Value::FloatValue(f)) => if f.is_finite() { f } else { 0. }.to_bits(),
                None => continue,
            };
            words.push(bits as u16);
            if value.addr >= 4 {
                words.push((bits >> 16) as u16);
            }
        }
        serde_json::to_string(&words).map_err(|e| e.to_string())
    } else {
        let mut values = Vec::with_capacity(100);
        for value in &reply.values {
            let number = match value.value {
                Some(Value::Int32Value(n)) => f32::from_bits(n.cast_unsigned()),
                Some(Value::FloatValue(f)) => {
                    if f.is_finite() {
                        f
                    } else {
                        0.
                    }
                }
                None => continue,
            };
            // Baseline reinterprets timestamp before JSON without float sanitization.
            if !number.is_finite() {
                return Err("CLI timestamp reinterpretation is non-finite".into());
            }
            values.push(number);
        }
        serde_json::to_string(&values).map_err(|e| e.to_string())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cli_shapes_and_cdab_nonfinite_projection_match_legacy() -> Result<(), String> {
        for args in [
            vec!["get", "unknown"],
            vec!["snapshot", "--ini", "x"],
            vec!["--start"],
            vec!["header", "extra"],
        ] {
            assert!(parse(&args.into_iter().map(String::from).collect::<Vec<_>>()).is_err());
        }
        let reply = RegistersReply {
            values: vec![
                RegisterValue {
                    addr: 0,
                    value: Some(register_value::Value::Int32Value(7)),
                },
                RegisterValue {
                    addr: 4,
                    value: Some(register_value::Value::Int32Value(0x3f80_0000)),
                },
                RegisterValue {
                    addr: 6,
                    value: Some(register_value::Value::FloatValue(f32::NAN)),
                },
            ],
            ..Default::default()
        };
        assert_eq!(project(&reply, true)?, "[7,0,16256,0,0]");
        let reply = RegistersReply {
            values: reply.values[1..].to_vec(),
            ..Default::default()
        };
        assert_eq!(project(&reply, false)?, "[1.0,0.0]");
        Ok(())
    }
}
