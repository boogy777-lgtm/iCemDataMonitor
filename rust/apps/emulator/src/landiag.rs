//! Legacy standalone diagnostic; no engine or Host startup.
use icem_platform_windows::landiag::{Adapter, Port, SystemPort};
use std::io::{self, Write};
const SVS: [u32; 6] = [
    0x13120203, 0x13120206, 0x13120200, 0x13120209, 0x1312020C, 0x1312020F,
];
const FAKE: [u8; 6] = [0, 0x80, 0x2F, 0x23, 0xF4, 0x44];
#[derive(Debug)]
enum Error {
    Device(String),
    Output(io::Error),
}
impl From<io::Error> for Error {
    fn from(e: io::Error) -> Self {
        Self::Output(e)
    }
}
fn line(out: &mut impl Write, text: impl std::fmt::Display) -> Result<(), Error> {
    writeln!(out, "{text}")?;
    out.flush()?;
    Ok(())
}
fn mac(bytes: [u8; 6]) -> String {
    bytes.map(|b| format!("{b:02X}")).join(":")
}
fn find(all: &[Adapter], key: &str) -> Option<Adapter> {
    let key = key.to_ascii_lowercase();
    all.iter()
        .find(|a| {
            a.description.to_ascii_lowercase().contains(&key)
                || a.name.to_ascii_lowercase().contains(&key)
        })
        .cloned()
}
fn frames(source: [u8; 6]) -> [[u8; 152]; 6] {
    SVS.map(|sv| {
        let mut payload = std::array::from_fn::<_, 124, _>(|i| i as u8);
        payload[..4].copy_from_slice(&sv.to_be_bytes());
        icem_protocols::ace::build(source, 0x00A86F46, sv, &payload)
    })
}
fn wait(port: &mut impl Port, ms: u64, captured: &mut u64) -> Result<(), Error> {
    match port.wait(ms) {
        Ok(n) => {
            *captured += n;
            Ok(())
        }
        Err(e) => {
            *captured += e.captured;
            Err(Error::Device(e.message))
        }
    }
}
fn cycle(
    port: &mut impl Port,
    capture: &Adapter,
    sender: &Adapter,
    source: [u8; 6],
    out: &mut impl Write,
) -> Result<u64, Error> {
    let mut captured = 0;
    let result = (|| {
        port.open_capture(capture).map_err(Error::Device)?;
        line(
            out,
            format!("  Capture opened: {} (promisc)", capture.description),
        )?;
        wait(port, 200, &mut captured)?;
        if let Err(e) = port.open_sender(sender) {
            line(out, format!("  Emitter Open FAILED: {e}"))?;
            return Ok(());
        }
        line(out, "  Emitter opened OK. Sending superframes...")?;
        let start = port.elapsed_ms();
        let mut sent = 0;
        for burst in 0..2 {
            if burst == 1 {
                wait(port, 100, &mut captured)?;
            }
            for (index, frame) in frames(source).iter().enumerate() {
                wait(port, if index == 0 { 938 } else { 5 }, &mut captured)?;
                if port.send(frame).is_ok() {
                    sent += 1;
                }
            }
        }
        let elapsed = port.elapsed_ms().saturating_sub(start);
        line(
            out,
            format!("  Sent 2 superframes in {elapsed}ms. FramesSent={sent}"),
        )?;
        port.close_sender();
        wait(
            port,
            500_u64.max(5000_u64.saturating_sub(elapsed)),
            &mut captured,
        )
    })();
    port.close_sender();
    port.close_capture();
    match result {
        Err(Error::Device(e)) => {
            line(out, format!("  ERROR: {e}"))?;
            let lower = e.to_ascii_lowercase();
            if lower.contains("access") || lower.contains("permission") {
                line(out, "  HINT: Try running as Administrator.")?;
            }
        }
        Err(e) => return Err(e),
        Ok(()) => {}
    }
    Ok(captured)
}
fn run(port: &mut impl Port, key: &str, out: &mut impl Write) -> Result<u8, Error> {
    line(out, "=== LAN Loopback Diagnostic ===")?;
    line(out, format!("Target adapter substring: '{key}'\n"))?;
    let selection = (|| {
        let all = port.adapters().map_err(Error::Device)?;
        line(out, format!("Available adapters ({}):", all.len()))?;
        for a in &all {
            line(
                out,
                format!(
                    "  {}  [{}]  MAC={}",
                    a.description,
                    a.name,
                    a.mac.map(mac).unwrap_or_default()
                ),
            )?;
        }
        let Some(sender) = find(&all, key) else {
            line(out, format!("FAIL: Adapter not found for '{key}'"))?;
            return Ok(None);
        };
        let separate = port.adapters().map_err(Error::Device)?;
        let Some(capture) = find(&separate, key) else {
            line(out, "FAIL: Could not get separate capture device instance")?;
            return Ok(None);
        };
        line(
            out,
            format!(
                "Send device:    {} [{}] MAC={}",
                sender.description,
                sender.name,
                sender.mac.map(mac).unwrap_or_else(|| "UNKNOWN".into())
            ),
        )?;
        line(
            out,
            format!(
                "Capture device: {} [{}]\n",
                capture.description, capture.name
            ),
        )?;
        Ok(Some((sender, capture)))
    })();
    let (sender, capture) = match selection {
        Ok(Some(pair)) => pair,
        Ok(None) => return Ok(1),
        Err(Error::Device(e)) => {
            line(out, format!("FAIL enumerating adapters: {e}"))?;
            line(
                out,
                "Npcap may not be installed or accessible. Install from https://npcap.com/",
            )?;
            return Ok(1);
        }
        Err(e) => return Err(e),
    };
    line(out, "─── Variant A: fake NI src MAC ───")?;
    let a = cycle(port, &capture, &sender, FAKE, out)?;
    line(out, format!("  sent=12  captured={a}"))?;
    wait(port, 1500, &mut 0)?;
    line(out, "\n─── Variant B: real adapter src MAC ───")?;
    let b = cycle(port, &capture, &sender, sender.mac.unwrap_or([0; 6]), out)?;
    line(out, format!("  sent=12  captured={b}"))?;
    line(out, "\n=== DIAGNOSIS ===")?;
    match (a > 0, b > 0) {
        (false, false) => line(
            out,
            "RESULT: ZERO frames captured on wire.\nROOT CAUSE: Send path failure — frames are not leaving the NIC.\nPOSSIBLE FIXES:\n  1. Npcap 'Restrict to Administrators'? Run as Administrator.\n  2. DeviceConfiguration may need specific flags.\n  3. Check Npcap service: 'sc query npcap' or 'sc query npf'.",
        )?,
        (false, true) => line(
            out,
            "RESULT: Only real MAC frames pass. Spoofed src MAC rejected by driver/NIC.\nFIX: Use real adapter MAC as default src MAC for LAN emission.",
        )?,
        (true, true) => line(
            out,
            "RESULT: Both src MAC variants seen on wire. Frames ARE going out.\nNEXT: If iCem doesn't see them, the issue is in iCem configuration.\n  Check iCem serial/lan tab: adapter selected? equipment number? unit number?",
        )?,
        (true, false) => {} // Original prints only the heading for the fourth outcome.
    }
    Ok(0)
}
pub fn main(adapter: Option<&str>) -> u8 {
    let mut out = io::stdout().lock();
    let Some(adapter) = adapter.filter(|s| !s.trim().is_empty()) else {
        return if line(&mut out, "Usage: icem-emulator-engine-dev --landiag <adapter substring>\n--landiag requires an adapter substring").is_ok() { 2 } else { 1 };
    };
    match run(&mut SystemPort::default(), adapter, &mut out) {
        Ok(code) => code,
        Err(Error::Output(e)) => {
            eprintln!("landiag output failed: {e}");
            1
        }
        Err(Error::Device(e)) => {
            eprintln!("landiag failed: {e}");
            1
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[derive(Default)]
    struct Fake {
        time: u64,
        waits: Vec<u64>,
        sent: Vec<Vec<u8>>,
        events: Vec<&'static str>,
        counts: [u64; 2],
        variant: usize,
        fault: bool,
        send_fault: bool,
        open_fault: bool,
    }
    fn adapter() -> Adapter {
        Adapter {
            name: "nic".into(),
            description: "Ethernet".into(),
            mac: Some([1, 2, 3, 4, 5, 6]),
        }
    }
    impl Port for Fake {
        fn adapters(&mut self) -> Result<Vec<Adapter>, String> {
            Ok(vec![adapter()])
        }
        fn open_capture(&mut self, _: &Adapter) -> Result<(), String> {
            self.events.push("capture");
            Ok(())
        }
        fn open_sender(&mut self, _: &Adapter) -> Result<(), String> {
            self.events.push("sender");
            if self.open_fault {
                Err("open failure".into())
            } else {
                Ok(())
            }
        }
        fn close_capture(&mut self) {
            self.events.push("close capture");
            self.variant += 1;
        }
        fn close_sender(&mut self) {
            self.events.push("close sender");
        }
        fn send(&mut self, f: &[u8]) -> Result<(), String> {
            self.sent.push(f.to_vec());
            if self.send_fault {
                Err("send failure".into())
            } else {
                Ok(())
            }
        }
        fn wait(&mut self, ms: u64) -> Result<u64, icem_platform_windows::landiag::WaitError> {
            self.waits.push(ms);
            self.time += ms;
            if ms == 200 {
                let captured = self.counts[self.variant];
                if self.fault {
                    Err(icem_platform_windows::landiag::WaitError {
                        captured,
                        message: "read failure".into(),
                    })
                } else {
                    Ok(captured)
                }
            } else {
                Ok(0)
            }
        }
        fn elapsed_ms(&self) -> u64 {
            self.time
        }
    }
    #[test]
    fn fixed_frames_timing_and_success_count() {
        let mut fake = Fake {
            send_fault: true,
            ..Fake::default()
        };
        let mut out = Vec::new();
        assert_eq!(
            run(&mut fake, "ETH", &mut out).unwrap_or_else(|e| panic!("{e:?}")),
            0
        );
        assert_eq!(fake.sent.len(), 24);
        for (i, f) in fake.sent.iter().enumerate() {
            assert_eq!(&f[6..12], if i < 12 { &FAKE } else { &[1, 2, 3, 4, 5, 6] });
            assert_eq!(&f[20..24], &0x00A86F46_u32.to_be_bytes());
            assert_eq!(&f[24..28], &SVS[i % 6].to_be_bytes());
            assert_eq!(&f[28..32], &SVS[i % 6].to_be_bytes());
            assert_eq!(&f[32..], &(4_u8..124).collect::<Vec<_>>());
        }
        let burst = [938, 5, 5, 5, 5, 5];
        let expected = [
            vec![200],
            burst.to_vec(),
            vec![100],
            burst.to_vec(),
            vec![2974],
            vec![1500],
            vec![200],
            burst.to_vec(),
            vec![100],
            burst.to_vec(),
            vec![2974],
        ]
        .concat();
        assert_eq!(fake.waits, expected);
        let text = String::from_utf8(out).unwrap_or_else(|e| panic!("{e:?}"));
        assert!(text.contains("2026ms. FramesSent=0"));
        assert_eq!(
            fake.events
                .iter()
                .filter(|e| **e == "close capture")
                .count(),
            2
        );
    }
    #[test]
    fn diagnoses_and_partial_read_failure() {
        for (counts, expected) in [
            ([0, 0], "ZERO frames"),
            ([0, 2], "Only real MAC"),
            ([2, 2], "Both src MAC"),
            ([2, 0], "=== DIAGNOSIS ===\n"),
        ] {
            let mut fake = Fake {
                counts,
                fault: true,
                ..Fake::default()
            };
            let mut out = Vec::new();
            assert_eq!(
                run(&mut fake, "nic", &mut out).unwrap_or_else(|e| panic!("{e:?}")),
                0
            );
            let text = String::from_utf8(out).unwrap_or_else(|e| panic!("{e:?}"));
            assert!(text.contains(expected));
            assert!(text.contains(&format!("sent=12  captured={}", counts[0])));
            assert_eq!(fake.variant, 2);
        }
    }
    #[test]
    fn setup_failure_and_output_failure_cleanup() {
        let mut fake = Fake {
            open_fault: true,
            ..Fake::default()
        };
        let mut out = Vec::new();
        assert_eq!(
            run(&mut fake, "nic", &mut out).unwrap_or_else(|e| panic!("{e:?}")),
            0
        );
        assert!(
            String::from_utf8(out)
                .unwrap_or_else(|e| panic!("{e:?}"))
                .contains("Emitter Open FAILED")
        );
        struct Broken;
        impl Write for Broken {
            fn write(&mut self, _: &[u8]) -> io::Result<usize> {
                Err(io::ErrorKind::BrokenPipe.into())
            }
            fn flush(&mut self) -> io::Result<()> {
                Ok(())
            }
        }
        let mut fake = Fake::default();
        assert!(matches!(
            cycle(&mut fake, &adapter(), &adapter(), FAKE, &mut Broken),
            Err(Error::Output(_))
        ));
        assert_eq!(fake.events, ["capture", "close sender", "close capture"]);
    }
}
