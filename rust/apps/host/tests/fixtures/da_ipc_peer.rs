//! Test-only Host IPC peer: not an OPC server and never registered with COM.
use std::io::{self, Read, Write};

fn main() -> io::Result<()> {
    if std::env::args().skip(1).collect::<Vec<_>>() != ["--ipc-v1"] {
        return Err(io::Error::other("expected --ipc-v1"));
    }
    let marker = std::env::current_exe()?.with_file_name("da-ipc-starts.log");
    writeln!(
        std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(marker)?,
        "start"
    )?;
    let mut input = io::stdin().lock();
    let mut output = io::stdout().lock();
    output.write_all(b"ICDAv001")?;
    output.flush()?;
    loop {
        let mut size = [0; 4];
        match input.read_exact(&mut size) {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => return Ok(()),
            Err(error) => return Err(error),
        }
        let size = u32::from_le_bytes(size) as usize;
        if !(16..=256 * 1024).contains(&size) {
            return Err(io::Error::other("invalid frame size"));
        }
        let mut frame = vec![0; size];
        input.read_exact(&mut frame)?;
        if &frame[..8] != b"ICDAv001" {
            return Err(io::Error::other("invalid frame version"));
        }
        output.write_all(&frame[8..16])?;
        output.flush()?;
    }
}
