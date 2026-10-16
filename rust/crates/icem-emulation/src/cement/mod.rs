//! Fixed CementUnitNetworkFactory composition. No I/O, scheduler, dynamic graph or globals.
//! Inputs/outputs retain their original paths; factory wiring is read exactly one hop.
use crate::dfb::{FirstOrderLag, KickoutLatch, Totalizer, dotnet_max};

#[derive(Debug, Clone, PartialEq)]
pub enum SignalValue {
    Float(f64),
    Unsigned(u32),
    Bool(bool),
    Text(Option<String>),
}
impl From<&f64> for SignalValue {
    fn from(v: &f64) -> Self {
        Self::Float(*v)
    }
}
impl From<&u32> for SignalValue {
    fn from(v: &u32) -> Self {
        Self::Unsigned(*v)
    }
}
impl From<&bool> for SignalValue {
    fn from(v: &bool) -> Self {
        Self::Bool(*v)
    }
}
impl From<&Option<String>> for SignalValue {
    fn from(v: &Option<String>) -> Self {
        Self::Text(v.clone())
    }
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Direction {
    Input,
    Output,
    Parameter,
}
#[derive(Debug, Clone, PartialEq)]
pub struct SignalSample {
    pub path: String,
    pub direction: Direction,
    /// The local port storage; a connected input's Read() need not equal this value.
    pub stored: SignalValue,
    pub value: SignalValue,
    pub source: Option<&'static str>,
}
impl SignalSample {
    fn local(path: String, direction: Direction, value: SignalValue) -> Self {
        Self {
            path,
            direction,
            stored: value.clone(),
            value,
            source: None,
        }
    }
}
macro_rules! collect_port {
    (scalar, $s:expr, $block:expr, $dir:expr, $out:expr, $name:expr) => {
        $out.push(SignalSample::local(
            format!("{}.{}", $block, $name),
            $dir,
            SignalValue::from(&$s),
        ));
    };
    (array, $s:expr, $block:expr, $dir:expr, $out:expr, $name:expr) => {
        for (i, value) in $s.iter().enumerate() {
            $out.push(SignalSample::local(
                format!("{}.{}{}", $block, $name, i + 1),
                $dir,
                SignalValue::from(value),
            ));
        }
    };
}
macro_rules! ports {
    (pub struct $name:ident { $($field:ident : $ty:ty => $kind:ident $path:literal,)* }) => {
        #[derive(Debug, Default, Clone)]
        pub struct $name { $(pub $field: $ty,)* }
        impl $name {
            pub(super) fn samples(&self, _block: &str, _direction: Direction, _out: &mut Vec<SignalSample>) {
                $(collect_port!($kind, self.$field, _block, _direction, _out, $path);)*
            }
        }
    };
}
mod random;
pub use random::{Context, SeededRandom};
mod pump_side_ports;
pub use pump_side_ports::*;
mod mixer_ports;
pub use mixer_ports::*;
mod f300_hpvt_ports;
pub use f300_hpvt_ports::*;
mod raw_io_ports;
pub use raw_io_ports::*;
mod temperature_ports;
pub use temperature_ports::*;
mod job_sequencer_ports;
pub use job_sequencer_ports::*;
mod blocks;
pub use blocks::*;
mod f300_hpvt;
pub use f300_hpvt::*;
mod wire;
pub use wire::{WIRE_SIGNALS, WireSignal};

pub const BLOCK_ORDER: &[&str] = &[
    "PumpA",
    "PumpB",
    "Mixer",
    "F300Hpvt",
    "RawIo",
    "Temp",
    "Sequencer",
];
#[derive(Debug, Clone)]
pub struct CementNetwork {
    pub pump_a: PumpSide,
    pub pump_b: PumpSide,
    pub mixer: Mixer,
    pub f300_hpvt: F300Hpvt,
    pub raw_io: RawIo,
    pub temperature: Temperature,
    pub sequencer: JobSequencer,
    /// Factory-owned shared signal storage; these paths are not registered network ports.
    pub job_active: bool,
    pub reset: bool,
}
impl Default for CementNetwork {
    fn default() -> Self {
        let mut pump_a = PumpSide::default();
        pump_a.inputs.rate = 3.0;
        pump_a.inputs.opko_setting = 3000.0;
        let pump_b = pump_a.clone();
        let mut mixer = Mixer::default();
        mixer.inputs.blend_target = 15.8;
        mixer.inputs.water_valve_cmd = 60.0;
        mixer.inputs.cement_valve_cmd = 55.0;
        Self {
            pump_a,
            pump_b,
            mixer,
            f300_hpvt: F300Hpvt::default(),
            raw_io: RawIo::default(),
            temperature: Temperature::default(),
            sequencer: JobSequencer::default(),
            job_active: true,
            reset: false,
        }
    }
}
impl CementNetwork {
    /// Original factory Add order (D20), with a single RNG shared by all seven blocks.
    pub fn execute(&mut self, ctx: &mut Context) {
        self.pump_a.execute(self.reset, ctx);
        self.pump_b.execute(self.reset, ctx);
        self.mixer.execute(self.job_active, self.reset, ctx);
        self.f300_hpvt
            .execute(self.mixer.outputs.mix_water_rate, ctx);
        self.raw_io.execute(ctx);
        self.temperature.execute(ctx);
        self.sequencer.execute(self.job_active, self.reset);
    }
    /// Complete owned diagnostic view, including connected inputs' local and effective values.
    pub fn signals(&self) -> Vec<SignalSample> {
        let mut out = Vec::new();
        macro_rules! block {
            ($field:ident, $name:literal) => {
                self.$field
                    .inputs
                    .samples($name, Direction::Input, &mut out);
                self.$field
                    .outputs
                    .samples($name, Direction::Output, &mut out);
            };
        }
        block!(pump_a, "PumpA");
        block!(pump_b, "PumpB");
        block!(mixer, "Mixer");
        block!(f300_hpvt, "F300Hpvt");
        block!(raw_io, "RawIo");
        block!(temperature, "Temp");
        block!(sequencer, "Sequencer");
        for sample in &mut out {
            let (source, value) = match sample.path.as_str() {
                "PumpA.Reset" | "PumpB.Reset" | "Mixer.ResetStage" | "Sequencer.Reset" => {
                    ("Network.Reset", SignalValue::Bool(self.reset))
                }
                "Mixer.JobActive" | "Sequencer.JobActive" => {
                    ("Network.JobActive", SignalValue::Bool(self.job_active))
                }
                "F300Hpvt.FlowRef" => (
                    "Mixer.MixWaterRate",
                    SignalValue::Float(self.mixer.outputs.mix_water_rate),
                ),
                _ => continue,
            };
            sample.source = Some(source);
            sample.value = value;
        }
        out
    }
    pub fn parameters(&self) -> Vec<SignalSample> {
        let mut out = Vec::new();
        self.pump_a
            .parameters
            .samples("PumpA", Direction::Parameter, &mut out);
        self.pump_b
            .parameters
            .samples("PumpB", Direction::Parameter, &mut out);
        self.mixer
            .parameters
            .samples("Mixer", Direction::Parameter, &mut out);
        self.temperature
            .parameters
            .samples("Temp", Direction::Parameter, &mut out);
        self.sequencer
            .parameters
            .samples("Sequencer", Direction::Parameter, &mut out);
        out
    }
    /// Known keys are ASCII. Arbitrary Unicode OrdinalIgnoreCase remains a separate parity gate.
    pub fn try_wire_value(&self, name: &str) -> Option<f64> {
        WIRE_SIGNALS
            .iter()
            .find(|s| s.name.eq_ignore_ascii_case(name))
            .map(|s| (s.read)(self))
    }
    pub fn wire_value(&self, name: &str) -> f64 {
        self.try_wire_value(name).unwrap_or(0.0)
    }
}
