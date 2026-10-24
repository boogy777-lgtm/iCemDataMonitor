#![forbid(unsafe_code)]
//! Private Host/sidecar v1 stream protocol. No operating-system resource crosses it.
use icem_bus::{Snapshot, Value, Zone};
use icem_core::publication::{CaptureStatus, PublishedSnapshot};
use std::{
    io::{self, Read, Write},
    sync::Arc,
    time::{Duration, Instant},
};

pub const MAX_FRAME: usize = 256 * 1024;
pub const READY: &[u8; 8] = b"ICDAv001";
const UNKNOWN: u64 = u64::MAX;
const HEADER: usize = 40;
pub const CLSID: &str = "{C69B6B01-878C-4927-8CC0-C6BA26C4B71A}";
pub const PROG_ID: &str = "iCem.Rust.ReadOnlyDA.1";
#[derive(Clone, Debug)]
pub struct Frame {
    pub sequence: u64,
    pub generation: u64,
    pub capture_age: Option<Duration>,
    pub completion_age: Option<Duration>,
    /// 0 initial, 1 captured, 2 retained, 3 failed.
    pub status: u8,
    pub snapshot: Option<Arc<Snapshot>>,
}
fn invalid() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "invalid OPC DA IPC frame")
}
fn age(time: Option<Instant>, now: Instant) -> Option<Duration> {
    time.and_then(|t| now.checked_duration_since(t))
}
impl Frame {
    pub fn from_publication(state: PublishedSnapshot, sequence: u64, now: Instant) -> Self {
        Self {
            sequence,
            generation: state.capture_generation,
            capture_age: age(state.captured_at_monotonic, now),
            completion_age: age(state.completed_at_monotonic, now),
            status: match state.status {
                CaptureStatus::Initial => 0,
                CaptureStatus::Captured => 1,
                CaptureStatus::Retained { .. } => 2,
                CaptureStatus::Failed(_) => 3,
            },
            snapshot: state.snapshot,
        }
    }
    pub fn encode(&self) -> io::Result<Vec<u8>> {
        if self.status > 3 {
            return Err(invalid());
        }
        let mut body = Vec::with_capacity(
            HEADER
                + self
                    .snapshot
                    .as_ref()
                    .map_or(0, |s| 64 + s.words().len() * 2),
        );
        body.extend_from_slice(READY);
        body.extend_from_slice(&self.sequence.to_le_bytes());
        body.extend_from_slice(&self.generation.to_le_bytes());
        for age in [self.capture_age, self.completion_age] {
            let nanos = age.map_or(UNKNOWN, |d| {
                d.as_nanos().min(u128::from(UNKNOWN - 1)) as u64
            });
            body.extend_from_slice(&nanos.to_le_bytes());
        }
        body.push(self.status);
        if let Some(snapshot) = &self.snapshot {
            let mut header = [0u8; 64];
            header[..4].copy_from_slice(b"STNB");
            header[4..6].copy_from_slice(&1u16.to_le_bytes());
            header[6..8].copy_from_slice(&5u16.to_le_bytes());
            header[8..10].copy_from_slice(&snapshot.register_count().to_le_bytes());
            for (index, zone) in Zone::ALL.iter().enumerate() {
                let metadata = snapshot.zone(*zone);
                header[10 + index * 9..18 + index * 9]
                    .copy_from_slice(&metadata.last_write_utc_ticks.to_le_bytes());
                header[18 + index * 9] = u8::from(metadata.producer_active);
                header[37 + index * 4..41 + index * 4]
                    .copy_from_slice(&metadata.write_count.to_le_bytes());
            }
            body.extend_from_slice(&header);
            for word in snapshot.words() {
                body.extend_from_slice(&word.to_be_bytes());
            }
        }
        if body.len() > MAX_FRAME {
            return Err(invalid());
        }
        Ok(body)
    }
    pub fn decode(body: &[u8]) -> io::Result<Self> {
        if !(HEADER + 1..=MAX_FRAME).contains(&body.len()) || body.get(..8) != Some(READY) {
            return Err(invalid());
        }
        let number = |at| -> io::Result<u64> {
            Ok(u64::from_le_bytes(
                body.get(at..at + 8)
                    .ok_or_else(invalid)?
                    .try_into()
                    .map_err(|_| invalid())?,
            ))
        };
        let duration = |at| -> io::Result<Option<Duration>> {
            Ok(match number(at)? {
                UNKNOWN => None,
                n => Some(Duration::from_nanos(n)),
            })
        };
        let status = body[40];
        if status > 3 {
            return Err(invalid());
        }
        if body.len() > HEADER + 1 {
            for index in 0..3 {
                if body
                    .get(HEADER + 1 + 18 + index * 9)
                    .is_none_or(|flag| *flag > 1)
                {
                    return Err(invalid());
                }
            }
        }
        let snapshot = if body.len() == HEADER + 1 {
            None
        } else {
            Some(Arc::new(
                Snapshot::decode(&body[HEADER + 1..]).map_err(|_| invalid())?,
            ))
        };
        let frame = Self {
            sequence: number(8)?,
            generation: number(16)?,
            capture_age: duration(24)?,
            completion_age: duration(32)?,
            status,
            snapshot,
        };
        if frame.snapshot.is_none()
            && (frame.generation != 0 || frame.capture_age.is_some() || frame.status == 1)
        {
            return Err(invalid());
        }
        if frame.snapshot.is_some()
            && (frame.generation == 0 || frame.capture_age.is_none() || frame.status == 0)
        {
            return Err(invalid());
        }
        Ok(frame)
    }
}
pub fn write_frame(writer: &mut impl Write, frame: &Frame) -> io::Result<()> {
    let bytes = frame.encode()?;
    writer.write_all(&(bytes.len() as u32).to_le_bytes())?;
    writer.write_all(&bytes)?;
    writer.flush()
}
/// Clean EOF is distinct from a truncated frame.
pub fn read_next_frame(reader: &mut impl Read) -> io::Result<Option<Frame>> {
    let mut first = [0u8; 1];
    match reader.read(&mut first)? {
        0 => Ok(None),
        _ => read_frame(&mut first.as_slice().chain(reader)).map(Some),
    }
}
pub fn read_frame(reader: &mut impl Read) -> io::Result<Frame> {
    let mut length = [0; 4];
    reader.read_exact(&mut length)?;
    let length = u32::from_le_bytes(length) as usize;
    if !(HEADER + 1..=MAX_FRAME).contains(&length) {
        return Err(invalid());
    }
    let mut body = vec![0; length];
    reader.read_exact(&mut body)?;
    Frame::decode(&body)
}
/// Timestamp is mandatory in DA; zero explicitly means unknown for service/reserve.
#[derive(Debug)]
pub struct Sample {
    pub value: Value,
    pub quality: u16,
    pub filetime: u64,
}
pub struct Received {
    frame: Frame,
    credit_issued: Instant,
}
impl Received {
    /// `credit_issued` must precede sending READY/the previous ACK. Host samples
    /// this frame only after reading that credit. Adding receiver elapsed to Host
    /// ages conservatively includes idle, transport and decode without comparing
    /// process-local clocks; it can overestimate age but cannot rejuvenate it.
    pub fn new(frame: Frame, credit_issued: Instant) -> Self {
        Self {
            frame,
            credit_issued,
        }
    }
    pub fn frame(&self) -> &Frame {
        &self.frame
    }
    pub fn current(&self, now: Instant) -> bool {
        let elapsed = now.checked_duration_since(self.credit_issued);
        let fresh = |base: Option<Duration>| {
            base.zip(elapsed)
                .and_then(|(b, e)| b.checked_add(e))
                .is_some_and(|age| age < Duration::from_secs(5))
        };
        self.frame.snapshot.is_some()
            && self.frame.status == 1
            && fresh(self.frame.capture_age)
            && fresh(self.frame.completion_age)
    }
    pub fn sample(&self, address: u16, now: Instant) -> Option<Sample> {
        let snapshot = self.frame.snapshot.as_ref()?;
        let index = match address {
            0..4 => usize::from(address),
            4..600 if address.is_multiple_of(2) => 4 + usize::from(address - 4) / 2,
            600.. => 302 + usize::from(address - 600),
            _ => return None,
        };
        let variable = snapshot.variable(index)?;
        if variable.id.register_address() != address {
            return None;
        }
        let zone = match address {
            4..200 => Some(Zone::Com),
            200..400 => Some(Zone::Lan),
            400..600 => Some(Zone::Raw),
            _ => None,
        };
        let metadata = zone.map(|z| snapshot.zone(z));
        // .NET ticks since 0001 -> FILETIME ticks since 1601. Never derive from COM var4.
        let filetime = metadata
            .filter(|m| m.last_write_utc_ticks <= 3_155_378_975_999_999_999)
            .and_then(|m| m.last_write_utc_ticks.checked_sub(504_911_232_000_000_000))
            .and_then(|v| u64::try_from(v).ok())
            .unwrap_or(0);
        let quality = if !metadata.is_some_and(|m| m.producer_active) {
            if zone.is_some() { 0x1c } else { 0x40 }
        } else if !self.current(now) || filetime == 0 {
            0x44
        } else {
            0xc0
        };
        Some(Sample {
            value: variable.value,
            quality,
            filetime,
        })
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn frame() -> io::Result<Frame> {
        Ok(Frame {
            sequence: 7,
            generation: 1,
            capture_age: Some(Duration::ZERO),
            completion_age: Some(Duration::ZERO),
            status: 1,
            snapshot: Some(Arc::new(
                Snapshot::decode(include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin"))
                    .map_err(|_| invalid())?,
            )),
        })
    }
    #[test]
    fn bounded_versioned_roundtrip_bits_and_malformed() -> io::Result<()> {
        let frame = frame()?;
        let bytes = frame.encode()?;
        let decoded = Frame::decode(&bytes)?;
        assert_eq!(
            frame.snapshot.as_ref().map(|s| s.words()),
            decoded.snapshot.as_ref().map(|s| s.words())
        );
        for length in 0..bytes.len() {
            assert!(Frame::decode(&bytes[..length]).is_err());
        }
        for offset in [0, 7, 40, 41] {
            let mut changed = bytes.clone();
            changed[offset] = 255;
            assert!(Frame::decode(&changed).is_err());
        }
        assert!(read_frame(&mut (MAX_FRAME as u32 + 1).to_le_bytes().as_slice()).is_err());
        let mut changed = bytes;
        changed.push(0);
        assert!(Frame::decode(&changed).is_err());
        Ok(())
    }
    #[test]
    fn address_types_writer_time_and_monotonic_expiry() -> io::Result<()> {
        let now = Instant::now();
        let received = Received::new(frame()?, now);
        assert!(matches!(
            received.sample(4, now).map(|s| s.value),
            Some(Value::ComUnixSeconds(1_700_000_000))
        ));
        assert!(matches!(
            received.sample(0, now).map(|s| s.value),
            Some(Value::Word(7))
        ));
        assert_eq!(
            received.sample(10, now).map(|s| s.value.raw_bits()),
            Some(0x7fc01234)
        );
        assert!(received.sample(5, now).is_none());
        assert_eq!(
            received.sample(8, now).map(|s| s.value.raw_bits()),
            Some(0x80000000)
        );
        assert_eq!(
            received.sample(600, now).map(|s| (s.filetime, s.quality)),
            Some((0, 0x40))
        );
        assert_eq!(received.sample(200, now).map(|s| s.quality), Some(0x1c));
        let expected = received
            .frame
            .snapshot
            .as_ref()
            .map(|s| s.zone(Zone::Com).last_write_utc_ticks - 504_911_232_000_000_000);
        assert_eq!(received.sample(4, now).map(|s| s.filetime as i64), expected);
        assert_eq!(
            received
                .sample(10, now + Duration::from_secs(5))
                .map(|s| s.quality),
            Some(0x44)
        );
        assert_eq!(
            received
                .sample(10, now - Duration::from_millis(1))
                .map(|s| s.quality),
            Some(0x44)
        );
        Ok(())
    }
    #[test]
    fn credit_anchor_includes_blocked_transit_decode_and_later_reads() -> io::Result<()> {
        let credit = Instant::now();
        let mut source = frame()?;
        source.capture_age = Some(Duration::from_secs(2));
        source.completion_age = Some(Duration::from_secs(2));
        // Host sampled after credit; delivery/decode took four more seconds.
        // A receipt-after-decode anchor would incorrectly produce GOOD here.
        let body = source.encode()?;
        let received = Received::new(Frame::decode(&body)?, credit);
        assert_eq!(
            received
                .sample(10, credit + Duration::from_secs(4))
                .map(|s| s.quality),
            Some(0x44)
        );
        assert!(!received.current(credit + Duration::from_secs(4)));
        assert!(received.current(credit + Duration::from_secs(2)));
        assert!(!received.current(credit + Duration::from_secs(3)));
        assert!(!received.current(credit + Duration::from_secs(60)));
        let mut source = frame()?;
        source.capture_age = Some(Duration::MAX);
        let overflow = Received::new(source, credit);
        assert!(!overflow.current(credit + Duration::from_secs(1)));
        Ok(())
    }
    #[test]
    fn writer_clock_requires_representable_dotnet_datetime() -> io::Result<()> {
        let now = Instant::now();
        for (ticks, expected) in [
            (504_911_231_999_999_999i64, 0u64),
            (504_911_232_000_000_001, 1),
            (3_155_378_975_999_999_999, 2_650_467_743_999_999_999),
            (3_155_378_976_000_000_000, 0),
            (i64::MAX, 0),
        ] {
            let mut bytes = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin").to_vec();
            bytes[10..18].copy_from_slice(&ticks.to_le_bytes());
            let mut source = frame()?;
            source.snapshot = Some(Arc::new(Snapshot::decode(&bytes).map_err(|_| invalid())?));
            let received = Received::new(source, now);
            let sample = received.sample(10, now).ok_or_else(invalid)?;
            assert_eq!(sample.filetime, expected);
            assert_eq!(sample.quality, if expected == 0 { 0x44 } else { 0xc0 });
        }
        Ok(())
    }
    #[test]
    fn maximum_geometry_retention_and_clean_eof() -> io::Result<()> {
        let mut source = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin").to_vec();
        source[8..10].copy_from_slice(&u16::MAX.to_le_bytes());
        source.resize(64 + usize::from(u16::MAX) * 2, 0);
        let mut frame = frame()?;
        frame.snapshot = Some(Arc::new(Snapshot::decode(&source).map_err(|_| invalid())?));
        let bytes = frame.encode()?;
        assert!(bytes.len() < MAX_FRAME);
        let now = Instant::now();
        let retained = frame.snapshot.clone();
        frame.status = 2;
        let received = Received::new(Frame::decode(&frame.encode()?)?, now);
        assert_eq!(received.sample(10, now).map(|s| s.quality), Some(0x44));
        assert_eq!(received.sample(65534, now).map(|s| s.filetime), Some(0));
        assert_eq!(
            retained.as_ref().map(|s| s.words()),
            received.frame.snapshot.as_ref().map(|s| s.words())
        );
        assert!(read_next_frame(&mut [].as_slice())?.is_none());
        for bytes in [&[1u8][..], &[41, 0, 0, 0, 1][..]] {
            assert!(read_next_frame(&mut &*bytes).is_err());
        }
        Ok(())
    }
}
