//! Pure Modbus register mirror and unit-id/PDU codec. No sockets or bus writes.
use icem_bus::{Snapshot, Zone};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Function {
    ReadHolding,
    ReadInput,
    WriteSingle,
    WriteMultiple,
}

impl Function {
    pub const fn wire_code(self) -> u8 {
        match self {
            Self::ReadHolding => 3,
            Self::ReadInput => 4,
            Self::WriteSingle => 6,
            Self::WriteMultiple => 16,
        }
    }
}

impl TryFrom<u8> for Function {
    type Error = Exception;
    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            3 => Ok(Self::ReadHolding),
            4 => Ok(Self::ReadInput),
            6 => Ok(Self::WriteSingle),
            16 => Ok(Self::WriteMultiple),
            _ => Err(Exception::IllegalFunction),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Exception {
    IllegalFunction,
    IllegalAddress,
    IllegalValue,
}

impl Exception {
    pub const fn wire_code(self) -> u8 {
        match self {
            Self::IllegalFunction => 1,
            Self::IllegalAddress => 2,
            Self::IllegalValue => 3,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RegisterRange {
    start: u16,
    quantity: u16,
}

impl RegisterRange {
    pub fn validate(
        start: u16,
        quantity: u16,
        total: u16,
        function: Function,
    ) -> Result<Self, Exception> {
        let maximum = match function {
            Function::ReadHolding | Function::ReadInput => 125,
            Function::WriteSingle => 1,
            Function::WriteMultiple => 123,
        };
        if quantity == 0 || quantity > maximum {
            return Err(Exception::IllegalValue);
        }
        if u32::from(start) + u32::from(quantity) > u32::from(total) {
            return Err(Exception::IllegalAddress);
        }
        Ok(Self { start, quantity })
    }
    pub fn as_range(self) -> std::ops::Range<usize> {
        usize::from(self.start)..usize::from(self.start) + usize::from(self.quantity)
    }
}

/// Freshness is supplied by the source adapter, never inferred from wall-clock age.
pub enum MirrorInput<'a> {
    Unavailable,
    Fresh(&'a Snapshot),
    LastGood(&'a Snapshot),
}

/// Separate, freely writable Modbus words. Clients never mutate the producer bus.
#[derive(Debug)]
pub struct RegisterBank {
    words: Vec<u16>,
    count: u16,
}

impl RegisterBank {
    pub fn new(count: u16) -> Result<Self, Exception> {
        if count < 4 {
            return Err(Exception::IllegalValue);
        }
        Ok(Self {
            words: vec![0; usize::from(count)],
            count,
        })
    }
    pub fn words(&self) -> &[u16] {
        &self.words
    }
    pub fn register_count(&self) -> u16 {
        self.count
    }

    /// Source: ModbusTcpPublisher.TickOnce. Preserve actual resize-before-stale
    /// ordering (D10), raw flags (D07), overwrite every tick, inactive/reserve hold.
    pub fn tick(&mut self, input: MirrorInput<'_>) {
        let (snapshot, copy_zones) = match input {
            MirrorInput::Unavailable => {
                self.words[..4].fill(0);
                return;
            }
            MirrorInput::Fresh(snapshot) => (snapshot, true),
            MirrorInput::LastGood(snapshot) => (snapshot, false),
        };
        self.count = snapshot.register_count();
        self.words.resize(usize::from(self.count), 0);
        self.words[..4].copy_from_slice(&snapshot.words()[..4]);
        if copy_zones {
            for zone in Zone::ALL {
                if snapshot.zone(zone).producer_active {
                    let (start, end) = zone.range();
                    let end = end.min(self.words.len());
                    if start < end {
                        self.words[start..end].copy_from_slice(&snapshot.words()[start..end]);
                    }
                }
            }
        }
    }

    /// Input and result include unit id, then function and data; MBAP lives in transport.
    /// Trailing PDU bytes are tolerated as in the legacy codec. Validation precedes writes.
    pub fn process(&mut self, body: &[u8]) -> Vec<u8> {
        let Some((&unit, rest)) = body.split_first() else {
            return vec![0, 0x80, 1];
        };
        let Some(&code) = rest.first() else {
            return vec![0, 0x80, 1];
        };
        let result =
            Function::try_from(code).and_then(|function| self.execute(unit, function, body));
        match result {
            Ok(response) => response,
            Err(error) => vec![unit, code | 0x80, error.wire_code()],
        }
    }

    fn execute(&mut self, unit: u8, function: Function, body: &[u8]) -> Result<Vec<u8>, Exception> {
        let address = word(body, 2)?;
        match function {
            Function::ReadHolding | Function::ReadInput => {
                let quantity = word(body, 4)?;
                let range =
                    RegisterRange::validate(address, quantity, self.count, function)?.as_range();
                let mut response = vec![unit, function.wire_code(), (quantity * 2) as u8];
                for value in &self.words[range] {
                    response.extend_from_slice(&value.to_be_bytes());
                }
                Ok(response)
            }
            Function::WriteSingle => {
                let value = word(body, 4)?; // D05: five-byte body is rejected, never indexed past end.
                let range = RegisterRange::validate(address, 1, self.count, function)?.as_range();
                self.words[range.start] = value;
                Ok(vec![unit, 6, body[2], body[3], body[4], body[5]])
            }
            Function::WriteMultiple => {
                let quantity = word(body, 4)?;
                let byte_count = usize::from(*body.get(6).ok_or(Exception::IllegalValue)?);
                // Match legacy precedence: malformed values before address errors.
                if quantity == 0 || quantity > 123 || byte_count != usize::from(quantity) * 2 {
                    return Err(Exception::IllegalValue);
                }
                let data = body.get(7..7 + byte_count).ok_or(Exception::IllegalValue)?;
                let range =
                    RegisterRange::validate(address, quantity, self.count, function)?.as_range();
                for (slot, pair) in self.words[range].iter_mut().zip(data.chunks_exact(2)) {
                    *slot = u16::from_be_bytes([pair[0], pair[1]]);
                }
                Ok(vec![unit, 16, body[2], body[3], body[4], body[5]])
            }
        }
    }
}

fn word(bytes: &[u8], offset: usize) -> Result<u16, Exception> {
    let pair = bytes
        .get(offset..offset + 2)
        .ok_or(Exception::IllegalValue)?;
    Ok(u16::from_be_bytes([pair[0], pair[1]]))
}

#[cfg(test)]
mod tests;
