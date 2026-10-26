use icem_protocols::modbus::RegisterBank;
use icem_transport::modbus_tcp::{ModbusServer, ServerOptions};
use std::io::{self, Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

type TestResult = Result<(), Box<dyn std::error::Error>>;
fn start(max_connections: usize) -> Result<ModbusServer, Box<dyn std::error::Error>> {
    let bank = RegisterBank::new(650).map_err(|error| format!("{error:?}"))?;
    Ok(ModbusServer::bind(
        "127.0.0.1:0".parse()?,
        Arc::new(Mutex::new(bank)),
        ServerOptions {
            max_connections,
            ..ServerOptions::default()
        },
    )?)
}
fn connect(address: SocketAddr) -> io::Result<TcpStream> {
    let stream = TcpStream::connect_timeout(&address, Duration::from_secs(3))?;
    stream.set_read_timeout(Some(Duration::from_secs(3)))?;
    stream.set_write_timeout(Some(Duration::from_secs(3)))?;
    stream.set_nodelay(true)?;
    Ok(stream)
}
fn adu(tid: u16, pid: u16, body: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(&tid.to_be_bytes());
    bytes.extend_from_slice(&pid.to_be_bytes());
    bytes.extend_from_slice(&(body.len() as u16).to_be_bytes());
    bytes.extend_from_slice(body);
    bytes
}
fn response(stream: &mut TcpStream) -> io::Result<Vec<u8>> {
    let mut header = [0; 6];
    stream.read_exact(&mut header)?;
    let length = usize::from(u16::from_be_bytes([header[4], header[5]]));
    if !(2..=254).contains(&length) {
        return Err(io::Error::other("invalid response length"));
    }
    let mut bytes = header.to_vec();
    bytes.resize(6 + length, 0);
    stream.read_exact(&mut bytes[6..])?;
    Ok(bytes)
}
fn await_counter(test: impl Fn() -> bool) -> io::Result<()> {
    let deadline = Instant::now() + Duration::from_secs(3);
    while !test() {
        if Instant::now() >= deadline {
            return Err(io::Error::new(io::ErrorKind::TimedOut, "counter deadline"));
        }
        std::thread::sleep(Duration::from_millis(2));
    }
    Ok(())
}

#[test]
fn fragmentation_coalescing_persistent_session_and_legacy_pid_echo() -> TestResult {
    let server = start(8)?;
    let mut client = connect(server.local_addr())?;
    let write = adu(0x1234, 9, &[17, 6, 0, 4, 0xab, 0xcd]);
    // Split every part of MBAP and PDU across polls, including a one-byte tail.
    for byte in &write {
        client.write_all(&[*byte])?;
        std::thread::sleep(Duration::from_millis(3));
    }
    assert_eq!(response(&mut client)?, write);
    let mut requests = adu(2, 0, &[17, 3, 0, 4, 0, 1]);
    requests.extend_from_slice(&adu(3, 0, &[17, 4, 0, 4, 0, 1]));
    requests.extend_from_slice(&adu(4, 0, &[17, 16, 0, 5, 0, 2, 4, 0x12, 0x34, 0x56, 0x78]));
    client.write_all(&requests)?;
    assert_eq!(response(&mut client)?, adu(2, 0, &[17, 3, 2, 0xab, 0xcd]));
    assert_eq!(response(&mut client)?, adu(3, 0, &[17, 4, 2, 0xab, 0xcd]));
    assert_eq!(response(&mut client)?, adu(4, 0, &[17, 16, 0, 5, 0, 2]));
    let stats = server.stop()?;
    assert_eq!(stats.requests, 4);
    assert_eq!(stats.failed_sessions, 0);
    Ok(())
}

#[test]
fn malformed_pdu_gets_exception_then_valid_request_on_same_connection() -> TestResult {
    let server = start(8)?;
    let mut client = connect(server.local_addr())?;
    client.write_all(&adu(1, 0, &[1, 6, 0, 4, 0xff]))?; // original five-byte FC06 bug
    assert_eq!(response(&mut client)?, adu(1, 0, &[1, 0x86, 3]));
    client.write_all(&adu(2, 0, &[1, 3, 0, 4, 0, 1]))?;
    assert_eq!(response(&mut client)?, adu(2, 0, &[1, 3, 2, 0, 0]));
    assert_eq!(server.stop()?.requests, 2);
    Ok(())
}

#[test]
fn invalid_mbap_isolated_and_other_clients_still_work() -> TestResult {
    let server = start(8)?;
    for length in [0_u16, 1, 255, u16::MAX] {
        let mut bad = connect(server.local_addr())?;
        let [hi, lo] = length.to_be_bytes();
        bad.write_all(&[0, 1, 0, 0, hi, lo])?;
        let mut byte = [0];
        match bad.read(&mut byte) {
            Ok(count) => assert_eq!(count, 0),
            Err(error) => assert!(matches!(
                error.kind(),
                io::ErrorKind::ConnectionReset | io::ErrorKind::ConnectionAborted
            )),
        }
    }
    let mut good = connect(server.local_addr())?;
    good.write_all(&adu(9, 0, &[1, 3, 0, 0, 0, 1]))?;
    assert_eq!(response(&mut good)?, adu(9, 0, &[1, 3, 2, 0, 0]));
    assert_eq!(server.stop()?.failed_sessions, 4);
    Ok(())
}

#[test]
fn resource_cap_idle_partial_stop_and_rebind() -> TestResult {
    let server = start(1)?;
    let address = server.local_addr();
    let mut idle = connect(address)?;
    idle.write_all(&[0, 1, 0])?; // incomplete MBAP must not block cancellation
    await_counter(|| server.stats().accepted == 1)?;
    let mut rejected = connect(address)?;
    await_counter(|| server.stats().rejected == 1)?;
    let mut byte = [0];
    match rejected.read(&mut byte) {
        Ok(count) => assert_eq!(count, 0),
        Err(error) => assert!(matches!(
            error.kind(),
            io::ErrorKind::ConnectionReset | io::ErrorKind::ConnectionAborted
        )),
    }
    let before = Instant::now();
    server.stop()?;
    assert!(before.elapsed() < Duration::from_secs(2));
    // A socket with unread request bytes may be reset on Windows; the listener must still rebind.
    drop(idle);
    drop(rejected);
    let bank = RegisterBank::new(650).map_err(|error| format!("{error:?}"))?;
    let rebound = ModbusServer::bind(
        address,
        Arc::new(Mutex::new(bank)),
        ServerOptions::default(),
    )?;
    rebound.stop()?;
    Ok(())
}

#[test]
fn failed_bind_does_not_affect_existing_listener_and_drop_releases_port() -> TestResult {
    let server = start(8)?;
    let address = server.local_addr();
    let bank = Arc::new(Mutex::new(
        RegisterBank::new(650).map_err(|error| format!("{error:?}"))?,
    ));
    assert!(ModbusServer::bind(address, Arc::clone(&bank), ServerOptions::default()).is_err());
    let mut client = connect(address)?;
    client.write_all(&adu(1, 0, &[1, 3, 0, 0, 0, 1]))?;
    assert_eq!(response(&mut client)?, adu(1, 0, &[1, 3, 2, 0, 0]));
    drop(server);
    drop(client);
    let rebound = ModbusServer::bind(address, bank, ServerOptions::default())?;
    rebound.stop()?;
    Ok(())
}

#[test]
fn many_coalesced_requests_are_ordered_and_shared_writes_are_visible() -> TestResult {
    let server = start(8)?;
    let mut client = connect(server.local_addr())?;
    let mut requests = Vec::new();
    for tid in 0..200 {
        requests.extend_from_slice(&adu(tid, 0, &[3, 6, 0, 4, (tid >> 8) as u8, tid as u8]));
    }
    client.write_all(&requests)?;
    for tid in 0..200 {
        assert_eq!(
            response(&mut client)?,
            adu(tid, 0, &[3, 6, 0, 4, (tid >> 8) as u8, tid as u8])
        );
    }
    let mut other = connect(server.local_addr())?;
    other.write_all(&adu(300, 0, &[3, 4, 0, 4, 0, 1]))?;
    assert_eq!(response(&mut other)?, adu(300, 0, &[3, 4, 2, 0, 199]));
    assert_eq!(server.stop()?.requests, 201);
    Ok(())
}
