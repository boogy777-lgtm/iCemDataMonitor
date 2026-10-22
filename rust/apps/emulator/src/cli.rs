//! CLI presentation and bounded stdin framing; transport owns shared watch semantics.
use icem_transport::emulator_control::{ClientEnd, MAX_LINE_BYTES};
use std::{
    io::{self, BufRead, IsTerminal, Write},
    ops::ControlFlow,
};
type Receive<'a> = dyn FnMut(&str) -> io::Result<ControlFlow<()>> + 'a;
fn emit(out: &mut impl Write, line: &str) -> io::Result<()> {
    writeln!(out, "{line}")?;
    out.flush()
}
fn error(out: &mut impl Write, cmd: &str, message: &str) -> io::Result<()> {
    emit(
        out,
        &icem_emulation::control::to_json(false, cmd, None, Some(message)),
    )
}
/// CR, LF, CRLF and final unterminated lines. At most MAX_LINE_BYTES raw bytes.
fn read_line(input: &mut impl BufRead, skip_lf: &mut bool) -> io::Result<Option<Vec<u8>>> {
    let mut line = Vec::new();
    loop {
        let bytes = input.fill_buf()?;
        if bytes.is_empty() {
            return Ok(if line.is_empty() { None } else { Some(line) });
        }
        if *skip_lf {
            *skip_lf = false;
            if bytes[0] == b'\n' {
                input.consume(1);
                continue;
            }
        }
        if let Some(index) = bytes.iter().position(|b| matches!(b, b'\r' | b'\n')) {
            if line.len() + index > MAX_LINE_BYTES {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "stdin line exceeds 65536 bytes",
                ));
            }
            line.extend_from_slice(&bytes[..index]);
            *skip_lf = bytes[index] == b'\r';
            input.consume(index + 1);
            return Ok(Some(line));
        }
        if line.len() + bytes.len() > MAX_LINE_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "stdin line exceeds 65536 bytes",
            ));
        }
        line.extend_from_slice(bytes);
        let len = bytes.len();
        input.consume(len);
    }
}
fn roundtrip(
    out: &mut impl Write,
    command: &str,
    request: &mut impl FnMut(&str, &mut Receive<'_>) -> Result<ClientEnd, String>,
) -> io::Result<u8> {
    let command = command.trim();
    if command.len() > MAX_LINE_BYTES - 2 || command.contains(['\r', '\n']) {
        error(out, "", "command exceeds 65534 bytes or contains newline")?;
        return Ok(1);
    }
    let mut output_failed = None;
    let result = request(command, &mut |reply| {
        if let Err(e) = emit(out, reply) {
            output_failed = Some(io::Error::new(e.kind(), e.to_string()));
            return Err(e);
        }
        Ok(ControlFlow::Continue(()))
    });
    if let Some(e) = output_failed {
        return Err(e);
    }
    match result {
        Ok(ClientEnd::Reply { ok }) => Ok(u8::from(!ok)),
        Ok(ClientEnd::Cancelled) => Ok(0),
        Err(e) => {
            error(out, "", &e)?;
            Ok(1)
        }
    }
}
fn run(
    input: &mut impl BufRead,
    terminal: bool,
    args: &[String],
    out: &mut impl Write,
    mut request: impl FnMut(&str, &mut Receive<'_>) -> Result<ClientEnd, String>,
) -> io::Result<u8> {
    if !args.is_empty() {
        return roundtrip(out, &args.join(" "), &mut request);
    }
    if terminal {
        error(
            out,
            "help",
            &format!(
                "usage: iCemEmulator.exe --cli <command> ({}), or pipe commands via stdin",
                icem_emulation::control::COMMAND_NAMES.join("|")
            ),
        )?;
        return Ok(2);
    }
    let mut worst = 0;
    let mut skip_lf = false;
    loop {
        let bytes = match read_line(input, &mut skip_lf) {
            Ok(Some(line)) => line,
            Ok(None) => break,
            Err(e) => {
                error(out, "", &e.to_string())?;
                return Ok(1);
            }
        };
        let command = match std::str::from_utf8(&bytes) {
            Ok(s) => s,
            Err(e) => {
                error(out, "", &format!("invalid UTF-8: {e}"))?;
                return Ok(1);
            }
        };
        if command.trim().len() > MAX_LINE_BYTES - 2 {
            error(out, "", "command exceeds 65534 bytes")?;
            return Ok(1);
        }
        worst = worst.max(roundtrip(out, command, &mut request)?);
        if command.trim().eq_ignore_ascii_case("quit") {
            break;
        }
    }
    Ok(worst)
}
pub fn main(pipe: &str, args: &[String]) -> u8 {
    let terminal = io::stdin().is_terminal();
    let result = run(
        &mut io::stdin().lock(),
        terminal,
        args,
        &mut io::stdout().lock(),
        |command, receive| {
            #[cfg(windows)]
            {
                icem_transport::emulator_control::cli_request(pipe, command, receive)
                    .map_err(|e| e.to_string())
            }
            #[cfg(not(windows))]
            {
                let _ = (pipe, command, receive);
                Err("emulator control pipe requires Windows".into())
            }
        },
    );
    match result {
        Ok(code) => code,
        Err(e) => {
            eprintln!("CLI output/input failed: {e}");
            1
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn reply(command: &str, receive: &mut Receive<'_>) -> Result<ClientEnd, String> {
        let ok = command == "status" || command == "watch" || command.starts_with('{');
        let _ = receive(&format!("{{\"ok\":{ok}}}")).map_err(|e| e.to_string())?;
        if command == "watch" {
            let _ = receive("{\"tick\":1}").map_err(|e| e.to_string())?;
        }
        Ok(ClientEnd::Reply { ok })
    }
    #[test]
    fn aggregate_empty_lines_eof_and_newlines() -> io::Result<()> {
        let mut calls = Vec::new();
        let mut output = Vec::new();
        assert_eq!(
            run(
                &mut &b"status\r\nbad\r\n\rstatus\nstatus"[..],
                false,
                &[],
                &mut output,
                |s, r| {
                    calls.push(s.to_owned());
                    reply(s, r)
                }
            )?,
            1
        );
        assert_eq!(calls, ["status", "bad", "", "status", "status"]);
        assert_eq!(output.iter().filter(|b| **b == b'\n').count(), 5);
        let mut called = false;
        assert_eq!(
            run(&mut &b""[..], false, &[], &mut Vec::new(), |_, _| {
                called = true;
                Err("unexpected".into())
            })?,
            0
        );
        assert!(!called);
        Ok(())
    }
    #[test]
    fn terminal_and_explicit_command_watch() -> io::Result<()> {
        let mut output = Vec::new();
        assert_eq!(
            run(&mut &b""[..], true, &[], &mut output, |_, _| panic!(
                "must not connect"
            ))?,
            2
        );
        let json: serde_json::Value = serde_json::from_slice(&output)?;
        assert_eq!(json["cmd"], "help");
        for command in icem_emulation::control::COMMAND_NAMES {
            assert!(json["error"].as_str().unwrap_or_default().contains(command));
        }
        let mut calls = 0;
        output.clear();
        assert_eq!(
            run(
                &mut &b"bad\n"[..],
                false,
                &["watch".into()],
                &mut output,
                |s, r| {
                    calls += 1;
                    reply(s, r)
                }
            )?,
            0
        );
        assert_eq!(calls, 1);
        assert_eq!(output.iter().filter(|b| **b == b'\n').count(), 2);
        Ok(())
    }
    #[test]
    fn textual_quit_stops_after_error_json_quit_continues() -> io::Result<()> {
        let mut input = &b" QuIt \r\nset mutation\n"[..];
        let mut calls = Vec::new();
        assert_eq!(
            run(&mut input, false, &[], &mut Vec::new(), |s, r| {
                calls.push(s.to_owned());
                reply(s, r)
            })?,
            1
        );
        assert_eq!(calls, ["QuIt"]);
        assert!(
            std::str::from_utf8(input)
                .unwrap_or_default()
                .contains("set mutation")
        );
        calls.clear();
        assert_eq!(
            run(
                &mut &b"{\"cmd\":\"quit\"}\nstatus"[..],
                false,
                &[],
                &mut Vec::new(),
                |s, r| {
                    calls.push(s.to_owned());
                    reply(s, r)
                }
            )?,
            0
        );
        assert_eq!(calls.len(), 2);
        Ok(())
    }
    #[test]
    fn byte_limits_and_invalid_utf8_prevent_mutation() -> io::Result<()> {
        for bytes in [
            vec![b'x'; MAX_LINE_BYTES + 1],
            vec![b'x'; MAX_LINE_BYTES - 1],
            vec![0xff],
        ] {
            let mut output = Vec::new();
            let mut calls = 0;
            assert_eq!(
                run(&mut bytes.as_slice(), false, &[], &mut output, |_, _| {
                    calls += 1;
                    panic!("invalid line sent")
                })?,
                1
            );
            assert_eq!(calls, 0);
            assert_eq!(output.iter().filter(|b| **b == b'\n').count(), 1);
        }
        let valid = "é".repeat((MAX_LINE_BYTES - 2) / 2);
        let mut calls = 0;
        assert_eq!(
            run(
                &mut valid.as_bytes(),
                false,
                &[],
                &mut Vec::new(),
                |s, r| {
                    calls += 1;
                    assert_eq!(s.len(), MAX_LINE_BYTES - 2);
                    reply("status", r)
                }
            )?,
            0
        );
        assert_eq!(calls, 1);
        Ok(())
    }
    #[test]
    fn stdout_failure_stops_without_second_error_or_next_request() {
        struct Broken {
            writes: u32,
        }
        impl Write for Broken {
            fn write(&mut self, _: &[u8]) -> io::Result<usize> {
                self.writes += 1;
                Err(io::ErrorKind::BrokenPipe.into())
            }
            fn flush(&mut self) -> io::Result<()> {
                Ok(())
            }
        }
        let mut output = Broken { writes: 0 };
        let mut calls = 0;
        assert!(
            run(
                &mut &b"status\nset mutation"[..],
                false,
                &[],
                &mut output,
                |s, r| {
                    calls += 1;
                    reply(s, r)
                }
            )
            .is_err()
        );
        assert_eq!(calls, 1);
        assert_eq!(output.writes, 1);
    }
    #[test]
    fn client_failure_is_not_retried_and_batch_continues() -> io::Result<()> {
        let mut calls = Vec::new();
        let mut out = Vec::new();
        assert_eq!(
            run(
                &mut &b"setequipment 42\nstatus"[..],
                false,
                &[],
                &mut out,
                |s, r| {
                    calls.push(s.to_owned());
                    if s.starts_with("setequipment") {
                        Err("unknown effect".into())
                    } else {
                        reply(s, r)
                    }
                }
            )?,
            1
        );
        assert_eq!(calls, ["setequipment 42", "status"]);
        assert_eq!(out.iter().filter(|b| **b == b'\n').count(), 2);
        // A protocol-oversized line must stop before a following mutation too.
        let mut bytes = vec![b'x'; MAX_LINE_BYTES - 1];
        bytes.extend_from_slice(b"\nsetequipment 99");
        assert_eq!(
            run(
                &mut bytes.as_slice(),
                false,
                &[],
                &mut Vec::new(),
                |_, _| panic!("oversized line reached client")
            )?,
            1
        );
        Ok(())
    }
}
