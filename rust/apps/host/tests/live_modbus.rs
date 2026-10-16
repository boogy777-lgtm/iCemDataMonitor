#![cfg(windows)]
#[path = "../../../tests/support/windows.rs"]
mod windows;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpStream;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};
use windows::{Producer, Station};
struct Host(Option<Child>);
impl Drop for Host {
    fn drop(&mut self) {
        if let Some(child) = &mut self.0 {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}
fn request(stream: &mut TcpStream, body: &[u8]) -> std::io::Result<Vec<u8>> {
    let length = u16::try_from(body.len()).map_err(std::io::Error::other)?;
    stream.write_all(&[0, 1, 0, 0])?;
    stream.write_all(&length.to_be_bytes())?;
    stream.write_all(body)?;
    let mut header = [0; 6];
    stream.read_exact(&mut header)?;
    assert_eq!(&header[..4], &[0, 1, 0, 0]);
    let length = usize::from(u16::from_be_bytes([header[4], header[5]]));
    assert!(length <= 254);
    let mut result = vec![0; length];
    stream.read_exact(&mut result)?;
    Ok(result)
}
#[test]
fn dotnet_mapping_to_rust_host_to_modbus_with_live_refresh()
-> Result<(), Box<dyn std::error::Error>> {
    let station = Station::new()?;
    station.seed(650)?;
    let mut producer = Producer::start(&station, false)?;
    producer.command("write 6 4660", "WRITTEN")?;
    let child = Command::new(env!("CARGO_BIN_EXE_icem-host-dev"))
        .arg("--bus-live")
        .arg(&station.path)
        .arg("--bus-map")
        .arg(&station.map)
        .arg("--bus-mutex")
        .arg(&station.mutex)
        .args(["--bind", "127.0.0.1:0", "--duration-ms", "3000"])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()?;
    let mut host = Host(Some(child));
    let child = host.0.as_mut().ok_or("missing host")?;
    let mut stderr = BufReader::new(child.stderr.take().ok_or("missing stderr")?);
    let address = loop {
        let mut line = String::new();
        if stderr.read_line(&mut line)? == 0 {
            return Err("host exited before ready".into());
        }
        if let Some((_, address)) = line.trim().split_once(". Modbus ") {
            break address.to_owned();
        }
    };
    let mut stream = TcpStream::connect(address)?;
    stream.set_read_timeout(Some(Duration::from_secs(2)))?;
    stream.set_write_timeout(Some(Duration::from_secs(2)))?;
    assert_eq!(
        request(&mut stream, &[1, 3, 0, 6, 0, 1])?,
        [1, 3, 2, 0x12, 0x34]
    );
    assert_eq!(
        request(&mut stream, &[1, 6, 0, 200, 0xbe, 0xef])?,
        [1, 6, 0, 200, 0xbe, 0xef]
    );
    producer.command("write 6 43981", "WRITTEN")?;
    let deadline = Instant::now() + Duration::from_secs(2);
    loop {
        if request(&mut stream, &[1, 4, 0, 6, 0, 1])? == [1, 4, 2, 0xab, 0xcd] {
            break;
        }
        if Instant::now() >= deadline {
            return Err("live MMF update not mirrored".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    // A new producer tick happened; an inactive-zone external write must survive it.
    assert_eq!(
        request(&mut stream, &[1, 3, 0, 200, 0, 1])?,
        [1, 3, 2, 0xbe, 0xef]
    );
    drop(stream);
    let output = host.0.take().ok_or("missing host")?.wait_with_output()?;
    assert!(output.status.success());
    assert!(output.stdout.is_empty());
    Ok(())
}
