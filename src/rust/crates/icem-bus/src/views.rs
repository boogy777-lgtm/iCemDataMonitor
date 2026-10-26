//! Read-only projections of Bus v5. No catalog, clock, allocation or producer state.
use crate::{LIVE_END, Snapshot};
use std::fmt;

/// Logical body regions, distinct from producer zones (COM still starts at 4).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Region {
    Service,
    ComMeasurements,
    Lan,
    Raw,
    Reserve,
}

impl Region {
    pub const ALL: [Self; 5] = [
        Self::Service,
        Self::ComMeasurements,
        Self::Lan,
        Self::Raw,
        Self::Reserve,
    ];

    /// Half-open body-register bounds. Count has already passed the Bus shape gate.
    pub const fn range(self, register_count: u16) -> (usize, usize) {
        match self {
            Self::Service => (0, 8),
            Self::ComMeasurements => (8, 200),
            Self::Lan => (200, 400),
            Self::Raw => (400, 600),
            Self::Reserve => (600, register_count as usize),
        }
    }
}

#[derive(Debug, Clone, Copy)]
pub struct RegionView<'a> {
    pub region: Region,
    pub start_address: u16,
    pub words: &'a [u16],
}

/// Stable new-adapter identity: the first 16-bit register, never the array index.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct VariableId(u16);

impl VariableId {
    pub const fn register_address(self) -> u16 {
        self.0
    }
}

impl fmt::Display for VariableId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "var{}", self.0)
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Value {
    Word(u16),
    ComUnixSeconds(i32),
    Float32(f32),
}

impl Value {
    /// Use raw bits, rather than float equality, for source change detection.
    pub fn raw_bits(self) -> u32 {
        match self {
            Self::Word(value) => u32::from(value),
            Self::ComUnixSeconds(value) => value.cast_unsigned(),
            Self::Float32(value) => value.to_bits(),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Variable {
    pub index: usize,
    pub id: VariableId,
    pub value: Value,
}

/// Logical service area 0..8, borrowed from the same owned snapshot.
#[derive(Debug, Clone, Copy)]
pub struct ServiceView<'a> {
    words: &'a [u16],
}

impl ServiceView<'_> {
    pub fn status(&self) -> &[u16] {
        &self.words[..4]
    }

    /// Source COM field, not acquisition time or a LAN/RAW measurement timestamp.
    /// Zero means Unix epoch; availability/liveness is a separate source fact.
    pub fn com_source_time(&self) -> i32 {
        bits(self.words[4], self.words[5]).cast_signed()
    }

    pub fn unit_number(&self) -> f32 {
        f32::from_bits(bits(self.words[6], self.words[7]))
    }
}

impl Snapshot {
    pub fn region(&self, region: Region) -> RegionView<'_> {
        let (start, end) = region.range(self.register_count());
        RegionView {
            region,
            start_address: start as u16,
            words: &self.words()[start..end],
        }
    }

    pub fn service(&self) -> ServiceView<'_> {
        ServiceView {
            words: self.region(Region::Service).words,
        }
    }

    /// 4 status words, 298 live pairs, then every reserved word unchanged.
    /// Reserve has no declared numerical type beyond its physical u16 words.
    pub fn variable_count(&self) -> usize {
        usize::from(self.register_count()) - 298
    }

    pub fn variable(&self, index: usize) -> Option<Variable> {
        if index >= self.variable_count() {
            return None;
        }
        let address = match index {
            0..4 => index,
            4..302 => 4 + (index - 4) * 2,
            _ => usize::from(LIVE_END) + index - 302,
        };
        let words = self.words();
        let value = match address {
            4 => Value::ComUnixSeconds(bits(words[4], words[5]).cast_signed()),
            6..600 => Value::Float32(f32::from_bits(bits(words[address], words[address + 1]))),
            _ => Value::Word(words[address]),
        };
        Some(Variable {
            index,
            id: VariableId(address as u16),
            value,
        })
    }

    /// Allocation-free stable-order projection, including inactive/reserved positions.
    pub fn variables(&self) -> impl Iterator<Item = Variable> + '_ {
        (0..self.variable_count()).filter_map(|index| self.variable(index))
    }
}

fn bits(low: u16, high: u16) -> u32 {
    u32::from(low) | (u32::from(high) << 16)
}
