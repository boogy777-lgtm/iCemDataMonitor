use icem_emulation::{
    lan::VectorData,
    runtime_ports::{ComEmitter, LanEmitter},
};
use icem_platform_windows::{
    AceLanEmitter, ComSerialEmitter, PacketDevice, SerialDevice, burst_start_ms,
};
use std::time::Duration;
#[derive(Default)]
struct Serial {
    bytes: Vec<u8>,
    fail: bool,
}
impl SerialDevice for Serial {
    fn open(&mut self, _: &str) -> Result<(), String> {
        Ok(())
    }
    fn close(&mut self) {}
    fn write_all(&mut self, b: &[u8]) -> Result<(), String> {
        if self.fail {
            Err("write failure".into())
        } else {
            self.bytes.extend_from_slice(b);
            Ok(())
        }
    }
    fn ports(&self) -> Vec<String> {
        vec!["COM12".into()]
    }
}
#[derive(Default)]
struct Packets {
    packets: Vec<Vec<u8>>,
    attempts: usize,
    mac: Option<[u8; 6]>,
    fail: Option<usize>,
}
impl PacketDevice for Packets {
    fn open(&mut self, name: &str) -> Result<(), String> {
        if name == "missing" {
            Err("missing".into())
        } else {
            Ok(())
        }
    }
    fn close(&mut self) {}
    fn mac(&self) -> Option<[u8; 6]> {
        self.mac
    }
    fn send(&mut self, b: &[u8]) -> Result<(), String> {
        self.attempts += 1;
        if self.fail == Some(self.attempts) {
            Err("injection failure".into())
        } else {
            self.packets.push(b.to_vec());
            Ok(())
        }
    }
    fn adapters(&self) -> Vec<String> {
        vec!["test".into()]
    }
    fn npcap_available(&self) -> bool {
        true
    }
}
type TestResult = std::result::Result<(), Box<dyn std::error::Error>>;
#[test]
fn serial_crlf_success_counters_and_reopen() -> TestResult {
    let mut com = ComSerialEmitter::new(Serial::default());
    assert!(com.send_line("closed").is_err());
    com.open("COM12")?;
    com.send_line("a,b")?;
    assert_eq!(com.device().bytes, b"a,b\r\n");
    assert_eq!(com.lines_sent(), 1);
    com.close();
    assert_eq!(com.port_name(), None);
    assert_eq!(com.lines_sent(), 1);
    com.open("COM13")?;
    assert_eq!(com.lines_sent(), 0);
    assert!(com.send_line(&"x".repeat(65_535)).is_err());
    assert_eq!(com.lines_sent(), 0);
    let mut bad = ComSerialEmitter::new(Serial {
        fail: true,
        ..Serial::default()
    });
    bad.open("COM12")?;
    assert!(bad.send_line("failed").is_err());
    assert_eq!(bad.lines_sent(), 0);
    Ok(())
}
#[test]
fn lan_order_wire_pacing_and_partial_failure_counter() -> TestResult {
    let source = [2, 3, 4, 5, 6, 7];
    let mut lan = AceLanEmitter::new(Packets {
        mac: Some(source),
        fail: Some(2),
        ..Packets::default()
    });
    let vectors: Vec<_> = (1..=6)
        .map(|sv| VectorData {
            sv_number: sv,
            name: sv.to_string(),
            payload: [sv as u8; 124],
        })
        .collect();
    assert!(lan.send_with_wait(&vectors, 123, |_| {}).is_err());
    lan.open("test")?;
    let mut waits = Vec::new();
    assert!(
        lan.send_with_wait(&vectors, 123, |d| waits.push(d))
            .is_err()
    );
    assert_eq!(
        waits,
        [Duration::from_millis(938)]
            .into_iter()
            .chain([Duration::from_millis(5); 5])
            .collect::<Vec<_>>()
    );
    assert_eq!(lan.frames_sent(), 5);
    assert_eq!(lan.device().attempts, 6);
    let received: Vec<_> = lan
        .device()
        .packets
        .iter()
        .filter_map(|b| icem_protocols::ace::parse(b))
        .collect();
    assert_eq!(
        received.iter().map(|f| f.sv).collect::<Vec<_>>(),
        vec![1, 3, 4, 5, 6]
    );
    assert!(received.iter().all(|f| f.equipment == 123));
    assert_eq!(&lan.device().packets[0][6..12], &source);
    lan.close();
    assert_eq!(lan.frames_sent(), 5);
    lan.open("test")?;
    assert_eq!(lan.frames_sent(), 0);
    assert!(lan.open("missing").is_err());
    assert!(!lan.is_open());
    Ok(())
}
#[test]
fn missing_mac_and_empty_superframe_never_inject() -> TestResult {
    let mut lan = AceLanEmitter::new(Packets::default());
    lan.open("test")?;
    let v = VectorData {
        sv_number: 1,
        name: "test".into(),
        payload: [0; 124],
    };
    assert!(
        lan.send_with_wait(&[v], 1, |_| panic!("must not sleep without MAC"))
            .is_err()
    );
    lan.send_with_wait(&[], 1, |_| panic!("must not sleep for empty burst"))?;
    assert_eq!(lan.frames_sent(), 0);
    assert_eq!(burst_start_ms(6), 938);
    assert_eq!(burst_start_ms(20), 885);
    Ok(())
}
