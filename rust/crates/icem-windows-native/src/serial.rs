//! ADR 0004: exclusive synchronous serial I/O with finite writes.
use std::{io, ptr};
use windows_sys::Win32::{
    Devices::Communication::{
        COMMTIMEOUTS, DCB, NOPARITY, ONESTOPBIT, SetCommState, SetCommTimeouts,
    },
    Foundation::{CloseHandle, GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE},
    Storage::FileSystem::{CreateFileW, OPEN_EXISTING, QueryDosDeviceW, WriteFile},
};
/// Unique handle stored as integer, movable without unsafe Send implementation.
/// Synchronous operations require exclusive borrow. No raw handles escape.
pub struct SerialPort {
    handle: usize,
}
impl SerialPort {
    pub fn open(name: &str) -> io::Result<Self> {
        let suffix = name.strip_prefix(r"\\.\").unwrap_or(name);
        if !suffix
            .get(..3)
            .is_some_and(|s| s.eq_ignore_ascii_case("COM"))
            || suffix[3..].is_empty()
            || !suffix[3..].bytes().all(|b| b.is_ascii_digit())
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Invalid COM port name",
            ));
        }
        let path: Vec<u16> = format!(r"\\.\{suffix}").encode_utf16().chain([0]).collect();
        // SAFETY: terminated path, exclusive synchronous device, no pointer retained.
        let raw = unsafe {
            CreateFileW(
                path.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                ptr::null(),
                OPEN_EXISTING,
                0,
                ptr::null_mut(),
            )
        };
        if raw == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }
        let port = Self {
            handle: raw as usize,
        };
        let dcb = DCB {
            DCBlength: size_of::<DCB>() as u32,
            BaudRate: 9600,
            _bitfield: 1,
            ByteSize: 8,
            Parity: NOPARITY,
            StopBits: ONESTOPBIT,
            XonChar: 17,
            XoffChar: 19,
            ..DCB::default()
        };
        // SAFETY: live exclusive handle and initialized DCB; binary 8N1, handshake off.
        if unsafe { SetCommState(raw, &dcb) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let timeouts = COMMTIMEOUTS {
            ReadIntervalTimeout: u32::MAX,
            ReadTotalTimeoutMultiplier: 0,
            ReadTotalTimeoutConstant: 500,
            WriteTotalTimeoutMultiplier: 0,
            WriteTotalTimeoutConstant: 500,
        };
        // SAFETY: live handle, initialized timeouts retained only for call.
        if unsafe { SetCommTimeouts(raw, &timeouts) } == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(port)
    }
    pub fn write_all(&mut self, bytes: &[u8]) -> io::Result<()> {
        if bytes.len() > 65_536 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Serial line exceeds 65536 bytes",
            ));
        }
        let mut offset = 0;
        while offset < bytes.len() {
            let mut written = 0;
            // SAFETY: exclusively borrowed synchronous handle, input lives for whole
            // blocking call, output initialized local u32, no overlapped operation.
            if unsafe {
                WriteFile(
                    self.handle as HANDLE,
                    bytes[offset..].as_ptr(),
                    (bytes.len() - offset) as u32,
                    &mut written,
                    ptr::null_mut(),
                )
            } == 0
            {
                return Err(io::Error::last_os_error());
            }
            if written == 0 || written as usize > bytes.len() - offset {
                return Err(io::Error::new(
                    io::ErrorKind::WriteZero,
                    "Serial write incomplete",
                ));
            }
            offset += written as usize;
        }
        Ok(())
    }
}
impl Drop for SerialPort {
    fn drop(&mut self) {
        // SAFETY: uniquely owned successful CreateFileW result, closed once.
        unsafe {
            CloseHandle(self.handle as HANDLE);
        }
    }
}
pub fn serial_ports() -> io::Result<Vec<String>> {
    let mut capacity = 4096;
    loop {
        let mut buffer = vec![0u16; capacity];
        // SAFETY: null query enumerates names into owned buffer of stated capacity.
        let size = unsafe { QueryDosDeviceW(ptr::null(), buffer.as_mut_ptr(), capacity as u32) };
        if size > 0 {
            buffer.truncate(size as usize);
            let mut ports: Vec<_> = buffer
                .split(|x| *x == 0)
                .filter_map(|s| String::from_utf16(s).ok())
                .filter(|s| {
                    s.starts_with("COM")
                        && !s[3..].is_empty()
                        && s[3..].bytes().all(|b| b.is_ascii_digit())
                })
                .collect();
            ports.sort();
            return Ok(ports);
        }
        let error = io::Error::last_os_error();
        if error.raw_os_error() != Some(122) || capacity >= 1_048_576 {
            return Err(error);
        }
        capacity *= 2;
    }
}
