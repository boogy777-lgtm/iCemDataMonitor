#![forbid(unsafe_code)]
//! Pure Bus v5 codec over an OWNED, already synchronized byte copy.
//! Never create Rust references into a mapping concurrently changed by .NET.
use std::fmt;

pub const HEADER_BYTES: usize = 64;
pub const DEFAULT_REG_COUNT: u16 = 10_001;
pub const MIN_REG_COUNT: u16 = 650;
pub const LIVE_END: u16 = 600;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Zone {
    Com,
    Lan,
    Raw,
}

impl Zone {
    pub const ALL: [Self; 3] = [Self::Com, Self::Lan, Self::Raw];
    pub const fn range(self) -> (usize, usize) {
        match self {
            Self::Com => (4, 200),
            Self::Lan => (200, 400),
            Self::Raw => (400, 600),
        }
    }
    const fn index(self) -> usize {
        match self {
            Self::Com => 0,
            Self::Lan => 1,
            Self::Raw => 2,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ZoneMetadata {
    pub last_write_utc_ticks: i64,
    pub producer_active: bool,
    pub write_count: u32,
}

impl ZoneMetadata {
    /// .NET DateTime UTC ticks: 100 ns since 0001-01-01, NOT Unix or monotonic.
    pub fn age_ms(self, now_utc_ticks: i64) -> i64 {
        now_utc_ticks
            .saturating_sub(self.last_write_utc_ticks)
            .max(0)
            / 10_000
    }
    pub fn is_live(self, now_utc_ticks: i64) -> bool {
        self.producer_active && self.age_ms(now_utc_ticks) < 5_000
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DecodeError {
    Truncated,
    Magic,
    ProtocolVersion,
    LayoutVersion,
    RegisterCount,
    FileLength,
    Address,
}

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "bus decode error: {self:?}")
    }
}
impl std::error::Error for DecodeError {}

#[derive(Debug, Clone)]
pub struct Snapshot {
    words: Box<[u16]>,
    count: u16,
    zones: [ZoneMetadata; 3],
}

impl Snapshot {
    /// Decode layout 5 only. Legacy migration belongs to the unchanged AddIn.
    pub fn decode(bytes: &[u8]) -> Result<Self, DecodeError> {
        let header = bytes.get(..HEADER_BYTES).ok_or(DecodeError::Truncated)?;
        if header.get(..4) != Some(b"STNB".as_slice()) {
            return Err(DecodeError::Magic);
        }
        if u16::from_le_bytes(read(header, 4)?) != 1 {
            return Err(DecodeError::ProtocolVersion);
        }
        if u16::from_le_bytes(read(header, 6)?) != 5 {
            return Err(DecodeError::LayoutVersion);
        }
        let count = u16::from_le_bytes(read(header, 8)?);
        if count < MIN_REG_COUNT {
            return Err(DecodeError::RegisterCount);
        }
        if bytes.len() != HEADER_BYTES + usize::from(count) * 2 {
            return Err(DecodeError::FileLength);
        }
        let zone = |index: usize| -> Result<ZoneMetadata, DecodeError> {
            Ok(ZoneMetadata {
                last_write_utc_ticks: i64::from_le_bytes(read(header, 10 + index * 9)?),
                producer_active: header.get(18 + index * 9) == Some(&1),
                write_count: u32::from_le_bytes(read(header, 37 + index * 4)?),
            })
        };
        let body = bytes.get(HEADER_BYTES..).ok_or(DecodeError::Truncated)?;
        let (pairs, []) = body.as_chunks::<2>() else {
            return Err(DecodeError::FileLength);
        };
        let words = pairs.iter().copied().map(u16::from_be_bytes).collect();
        Ok(Self {
            words,
            count,
            zones: [zone(0)?, zone(1)?, zone(2)?],
        })
    }
    pub fn register_count(&self) -> u16 {
        self.count
    }
    pub fn words(&self) -> &[u16] {
        &self.words
    }
    pub fn zone(&self, zone: Zone) -> ZoneMetadata {
        self.zones[zone.index()]
    }
    pub fn pair_bits(&self, address: u16) -> Result<u32, DecodeError> {
        if address < 4 || !address.is_multiple_of(2) {
            return Err(DecodeError::Address);
        }
        let pair = self
            .words
            .get(usize::from(address)..usize::from(address) + 2)
            .ok_or(DecodeError::Address)?;
        Ok(u32::from(pair[0]) | (u32::from(pair[1]) << 16))
    }
    /// Bit-preserving CDAB; the timestamp at 4 has separate projection rules.
    pub fn float_pair(&self, address: u16) -> Result<f32, DecodeError> {
        self.pair_bits(address).map(f32::from_bits)
    }
    pub fn unix_seconds(&self) -> Result<i32, DecodeError> {
        self.pair_bits(4).map(u32::cast_signed)
    }
}

fn read<const N: usize>(bytes: &[u8], offset: usize) -> Result<[u8; N], DecodeError> {
    let end = offset.checked_add(N).ok_or(DecodeError::Truncated)?;
    bytes
        .get(offset..end)
        .ok_or(DecodeError::Truncated)?
        .try_into()
        .map_err(|_| DecodeError::Truncated)
}

#[cfg(test)]
mod tests {
    use super::*;
    const FIXTURE: &[u8] = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin");

    #[test]
    fn independent_wire_fixture_preserves_endianness_and_timestamp() -> Result<(), DecodeError> {
        let snap = Snapshot::decode(FIXTURE)?;
        assert_eq!(snap.words().len(), 650);
        assert_eq!(snap.words()[0], 7);
        assert_eq!(snap.unix_seconds()?, 1_700_000_000);
        assert_eq!(snap.float_pair(6)?.to_bits(), 1.0_f32.to_bits());
        assert_eq!(snap.float_pair(8)?.to_bits(), 0x8000_0000); // negative zero
        assert_eq!(snap.pair_bits(10)?, 0x7fc0_1234); // preserve NaN payload
        assert_eq!(snap.zone(Zone::Com).write_count, 42);
        assert!(!snap.zone(Zone::Lan).producer_active);
        Ok(())
    }
    #[test]
    fn signed_unix_timestamp_preserves_high_bit_and_boundaries() -> Result<(), DecodeError> {
        for (bits, expected) in [
            (0_u32, 0_i32),
            (0x7fff_ffff, i32::MAX),
            (0x8000_0000, i32::MIN),
            (0xffff_ffff, -1),
        ] {
            let mut bytes = FIXTURE.to_vec();
            // Independent wire encoding: low CDAB word followed by high word, each BE.
            bytes[72..74].copy_from_slice(&(bits as u16).to_be_bytes());
            bytes[74..76].copy_from_slice(&((bits >> 16) as u16).to_be_bytes());
            assert_eq!(Snapshot::decode(&bytes)?.unix_seconds()?, expected);
        }
        Ok(())
    }
    #[test]
    fn every_truncated_prefix_is_rejected() {
        for end in 0..FIXTURE.len() {
            assert!(Snapshot::decode(&FIXTURE[..end]).is_err());
        }
    }
    #[test]
    fn shape_and_pair_boundaries_are_checked() -> Result<(), DecodeError> {
        let snap = Snapshot::decode(FIXTURE)?;
        assert!(snap.pair_bits(3).is_err());
        assert!(snap.pair_bits(649).is_err());
        assert!(snap.pair_bits(u16::MAX - 1).is_err());
        for (offset, value) in [(0, b'X'), (4, 2), (6, 4)] {
            let mut bytes = FIXTURE.to_vec();
            bytes[offset] = value;
            assert!(Snapshot::decode(&bytes).is_err());
        }
        let mut longer = FIXTURE.to_vec();
        longer.push(0);
        assert!(Snapshot::decode(&longer).is_err());
        Ok(())
    }
    #[test]
    fn liveness_is_age_gated_even_for_a_retained_snapshot() {
        let zone = ZoneMetadata {
            last_write_utc_ticks: 100_000_000,
            producer_active: true,
            write_count: 1,
        };
        assert!(zone.is_live(149_990_000));
        assert!(!zone.is_live(150_000_000));
        assert_eq!(zone.age_ms(0), 0);
        assert!(!zone.is_live(i64::MAX));
    }
}
