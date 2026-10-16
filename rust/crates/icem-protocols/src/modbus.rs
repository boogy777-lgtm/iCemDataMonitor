//! Only the range contract is implemented in M0; no TCP server yet.
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Exception {
    IllegalAddress,
    IllegalValue,
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

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn validates_dynamic_edge_without_u16_overflow() {
        assert!(RegisterRange::validate(649, 1, 650, Function::ReadHolding).is_ok());
        assert_eq!(
            RegisterRange::validate(649, 2, 650, Function::ReadHolding),
            Err(Exception::IllegalAddress)
        );
        assert_eq!(
            RegisterRange::validate(65534, 125, 65535, Function::ReadHolding),
            Err(Exception::IllegalAddress)
        );
        assert_eq!(
            RegisterRange::validate(0, 0, 650, Function::WriteMultiple),
            Err(Exception::IllegalValue)
        );
        assert_eq!(
            RegisterRange::validate(0, 124, 650, Function::WriteMultiple),
            Err(Exception::IllegalValue)
        );
        assert!(RegisterRange::validate(0, 125, 650, Function::ReadInput).is_ok());
    }
}
