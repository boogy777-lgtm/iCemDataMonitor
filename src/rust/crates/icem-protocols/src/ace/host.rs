//! Active Host LantableRegistry.DecodeSignals semantics, distinct from emulator native units.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Header {
    pub source: [u8; 6],
    pub equipment: u32,
    pub vector: u32,
}
/// The passive witness predicate is intentionally weaker than AceFrameBuilder.parse.
pub fn header(frame: &[u8]) -> Option<Header> {
    if frame.len() != 152 || frame[14..16] != [3, 3] || !matches!(frame[16], 8 | 9) {
        return None;
    }
    Some(Header {
        source: frame[6..12].try_into().ok()?,
        equipment: u32::from_be_bytes(frame[20..24].try_into().ok()?),
        vector: u32::from_be_bytes(frame[24..28].try_into().ok()?),
    })
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    Float32,
    Float64,
    Unsigned32,
    Signed32,
    Unsigned16,
    Signed16,
    Signed8,
    Hex,
    Ascii,
    Unknown,
}
impl Kind {
    pub fn parse(value: &str) -> Self {
        match value {
            "FF" => Self::Float32,
            "FD" => Self::Float64,
            "UL" | "UI" => Self::Unsigned32,
            "SL" | "SI" => Self::Signed32,
            "US" => Self::Unsigned16,
            "SS" => Self::Signed16,
            "SC" => Self::Signed8,
            "HX" => Self::Hex,
            "UC" => Self::Ascii,
            _ => Self::Unknown,
        }
    }
}
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Decoded {
    pub value: Option<f64>,
    pub text: Option<String>,
    pub sentinel: bool,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DecodeError {
    InvalidSpan,
    TruncatedScalar,
}
fn bytes<const N: usize>(frame: &[u8], start: usize) -> Result<[u8; N], DecodeError> {
    frame
        .get(start..start.checked_add(N).ok_or(DecodeError::InvalidSpan)?)
        .and_then(|bytes| bytes.try_into().ok())
        .ok_or(DecodeError::TruncatedScalar)
}
/// Fixed numeric widths follow the Host source, even if declared Size is smaller.
/// A declared span outside the frame yields an unavailable value; a scalar read
/// outside the frame fails the whole vector as the original BinaryPrimitives call.
/// Negative/overflow spans are checked rather than panicking (explicit safety delta).
pub fn decode(
    frame: &[u8],
    offset: i32,
    size: i32,
    kind: Kind,
    factor: f64,
) -> Result<Decoded, DecodeError> {
    let start = 24_i32.checked_add(offset).ok_or(DecodeError::InvalidSpan)?;
    if start < 0 {
        return Ok(Decoded::default());
    }
    let start = usize::try_from(start).map_err(|_| DecodeError::InvalidSpan)?;
    let size = usize::try_from(size).map_err(|_| DecodeError::InvalidSpan)?;
    let Some(declared) = start
        .checked_add(size)
        .and_then(|end| frame.get(start..end))
    else {
        return Ok(Decoded::default());
    };
    let value = match kind {
        Kind::Float32 => f64::from(f32::from_bits(u32::from_be_bytes(bytes(frame, start)?))),
        Kind::Float64 => f64::from_bits(u64::from_be_bytes(bytes(frame, start)?)),
        Kind::Unsigned32 => f64::from(u32::from_be_bytes(bytes(frame, start)?)),
        Kind::Signed32 => f64::from(i32::from_be_bytes(bytes(frame, start)?)),
        Kind::Unsigned16 => f64::from(u16::from_be_bytes(bytes(frame, start)?)),
        Kind::Signed16 => f64::from(i16::from_be_bytes(bytes(frame, start)?)),
        Kind::Signed8 => f64::from(bytes::<1>(frame, start)?[0].cast_signed()),
        Kind::Hex => {
            return Ok(Decoded {
                text: Some(format!(
                    "0x{:08X}",
                    u32::from_be_bytes(bytes(frame, start)?)
                )),
                ..Decoded::default()
            });
        }
        Kind::Ascii => {
            return Ok(Decoded {
                text: Some(
                    declared
                        .iter()
                        .take_while(|b| **b != 0)
                        .map(|b| char::from(if b.is_ascii() { *b } else { b'?' }))
                        .collect(),
                ),
                ..Decoded::default()
            });
        }
        Kind::Unknown => return Ok(Decoded::default()),
    } * factor;
    let sentinel = (value + 9999.).abs() < 0.001;
    Ok(Decoded {
        value: (!sentinel).then_some(value),
        text: None,
        sentinel,
    })
}
