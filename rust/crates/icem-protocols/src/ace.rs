//! Original AceFrameBuilder wire contract; IEEE 802.3, not UDP/EtherType.
pub const FRAME_SIZE: usize = 152;
pub const PAYLOAD_SIZE: usize = 124;
pub const DESTINATION: [u8; 6] = [3, 0, 1, 0, 0, 32];
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Frame {
    pub equipment: u32,
    pub sv: u32,
    pub payload: [u8; PAYLOAD_SIZE],
}
pub fn build(source: [u8; 6], equipment: u32, sv: u32, payload: &[u8]) -> [u8; FRAME_SIZE] {
    let mut frame = [0; FRAME_SIZE];
    frame[..6].copy_from_slice(&DESTINATION);
    frame[6..12].copy_from_slice(&source);
    frame[12..20].copy_from_slice(&[0, 0x8a, 3, 3, 8, 0, 0xd0, 0x0d]);
    frame[20..24].copy_from_slice(&equipment.to_be_bytes());
    frame[24..28].copy_from_slice(&sv.to_be_bytes());
    let len = payload.len().min(PAYLOAD_SIZE);
    frame[28..28 + len].copy_from_slice(&payload[..len]);
    frame
}
pub fn parse(frame: &[u8]) -> Option<Frame> {
    // Actual C# accepts trailing bytes and does not validate the length field.
    // Preserve this predicate despite its stricter XML comment.
    let frame = frame.get(..FRAME_SIZE)?;
    if frame[..6] != DESTINATION
        || frame[14..16] != [3, 3]
        || !matches!(frame[16], 8 | 9)
        || frame[17..20] != [0, 0xd0, 0x0d]
    {
        return None;
    }
    Some(Frame {
        equipment: u32::from_be_bytes(frame[20..24].try_into().ok()?),
        sv: u32::from_be_bytes(frame[24..28].try_into().ok()?),
        payload: frame[28..].try_into().ok()?,
    })
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SignalType {
    Float,
    Unsigned,
    Signed,
}
impl SignalType {
    pub fn parse(name: &str) -> Option<Self> {
        match name.to_ascii_uppercase().as_str() {
            "FF" => Some(Self::Float),
            "UL" | "UI" | "HX" => Some(Self::Unsigned),
            "SI" | "SL" => Some(Self::Signed),
            _ => None,
        }
    }
}
/// SignalWireCodec.TryDecodeNative. ByteOffset includes the 4-byte SV prefix.
pub fn decode_native(
    payload: &[u8],
    byte_offset: i32,
    size: i32,
    kind: &str,
    factor: f64,
) -> Option<f64> {
    let start = usize::try_from(byte_offset.checked_sub(4)?).ok()?;
    let size = usize::try_from(size).ok()?;
    let bytes = payload.get(start..start.checked_add(size)?)?;
    match SignalType::parse(kind)? {
        SignalType::Float => Some(f64::from(f32::from_bits(u32::from_be_bytes(
            bytes.try_into().ok()?,
        )))),
        SignalType::Unsigned => Some(
            match size {
                1 => f64::from(bytes[0]),
                2 => f64::from(u16::from_be_bytes(bytes.try_into().ok()?)),
                4 => f64::from(u32::from_be_bytes(bytes.try_into().ok()?)),
                8 => u64::from_be_bytes(bytes.try_into().ok()?) as f64,
                _ => return None,
            } * factor,
        ),
        SignalType::Signed => Some(
            match size {
                1 => f64::from(bytes[0].cast_signed()),
                2 => f64::from(i16::from_be_bytes(bytes.try_into().ok()?)),
                4 => f64::from(i32::from_be_bytes(bytes.try_into().ok()?)),
                8 => i64::from_be_bytes(bytes.try_into().ok()?) as f64,
                _ => return None,
            } * factor,
        ),
    }
}
