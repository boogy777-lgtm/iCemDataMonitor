//! Standalone legacy T1–T26 diagnostics. No settings, logger or production endpoints.
use icem_config::emulator::EmuSettings;
use icem_emulation::{
    control::{self, ControlReply},
    lan::{BaseProvider, Registry, VectorData},
    lan_pipeline::{LanPipeline, convert_payload},
    runtime::Engine,
    runtime_ports::{ComEmitter, LanEmitter},
    simulation::{self, Template, Variable},
    units::{self, Group, Preset, Settings},
};
use icem_protocols::ace;
use serde_json::Value;
use std::{
    io::{self, Write},
    path::Path,
    sync::{Arc, Mutex},
};
type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
const EQUIPMENT: u32 = 0x00a86f46;
// Independent expected SV/order pins copied from FrameRoundTrip, not provider constants.
const LEGACY_SVS: [u32; 6] = [
    0x13120203, 0x13120206, 0x13120200, 0x13120209, 0x1312020c, 0x1312020f,
];
const FIRMWARE_SVS: [u32; 14] = [
    0x13120212, 0x13120215, 0x13120218, 0x1312021b, 0x1312021e, 0x13120221, 0x13120224, 0x13120227,
    0x1312022a, 0x1312022d, 0x13120230, 0x13120233, 0x13120236, 0x13120239,
];
const PUMP: u32 = 0x13120206;
const MIX: u32 = 0x13120200;
const OPS: u32 = 0x1312020f;
const PRESETS: [Preset; 4] = [
    Preset::FirmwareDefault,
    Preset::LanNative,
    Preset::Metric,
    Preset::Imperial,
];
#[derive(Default)]
struct Case {
    failures: i32,
    details: Vec<String>,
    evidence: Vec<String>,
}
impl Case {
    fn check(&mut self, yes: bool, label: impl Into<String>) {
        if !yes {
            self.failures += 1;
            self.details.push(label.into());
        }
    }
    fn near(&mut self, actual: f64, expected: f64, tolerance: f64, label: &str) {
        self.check(
            actual.is_finite() && (actual - expected).abs() <= tolerance,
            format!("{label}: expected {expected}, got {actual}"),
        );
    }
}
/// Legacy dispatch looks only at argv[0], trims and ignores every later argument.
pub fn requested(first: Option<&str>) -> bool {
    first.is_some_and(|x| x.trim().eq_ignore_ascii_case("--selftest"))
}
pub fn main() -> i32 {
    let directory = std::env::current_exe().and_then(|p| {
        p.parent()
            .map(Path::to_path_buf)
            .ok_or_else(|| io::Error::other("executable has no parent"))
    });
    let mut output = io::stdout().lock();
    match directory.and_then(|p| run(&mut output, &p.join("emulator/templates"))) {
        Ok(failures) => failures,
        Err(error) => {
            eprintln!("selftest output/setup failed: {error}");
            1
        }
    }
}
fn run(output: &mut impl Write, templates: &Path) -> io::Result<i32> {
    writeln!(output, "=== iCemEmule Transport Self-Test ===\n")?;
    let mut failures = 0;
    for id in 1..=26 {
        let mut case = Case::default();
        if let Err(error) = execute(id, &mut case, templates) {
            case.check(false, error.to_string());
        }
        for evidence in case.evidence {
            writeln!(output, "T{id} EVIDENCE: {evidence}")?;
        }
        writeln!(
            output,
            "T{id} {}",
            if case.failures == 0 { "PASS" } else { "FAIL" }
        )?;
        for detail in case.details {
            writeln!(output, "  {detail}")?;
        }
        failures += case.failures;
    }
    summary(output, failures)?;
    output.flush()?;
    Ok(failures)
}
fn summary(output: &mut impl Write, failures: i32) -> io::Result<()> {
    if failures == 0 {
        writeln!(output, "\nALL TESTS PASSED")
    } else {
        writeln!(output, "\n{failures} TEST(S) FAILED")
    }
}
fn required(directory: &Path) -> Result {
    // Registry's permissive missing-file policy is unsuitable for a diagnostic.
    for file in [
        "lantable_registry.json",
        "firmware_registry.json",
        "variable_registry.json",
        "firmware_variables.json",
        "flecs_4.53.json",
        "plc_basic.json",
    ] {
        if !directory.join(file).is_file() {
            return Err(format!(
                "selftest required emulator asset missing: {}",
                directory.join(file).display()
            )
            .into());
        }
    }
    Ok(())
}
fn base(directory: &Path) -> Result<BaseProvider> {
    required(directory)?;
    let registry = Registry::load(
        &directory.join("lantable_registry.json"),
        Some(&directory.join("firmware_registry.json")),
    )?;
    BaseProvider::load(registry, &directory.join("variable_registry.json"), None)
}
fn pipeline(directory: &Path) -> Result<LanPipeline> {
    let mut p = LanPipeline::new(base(directory)?);
    p.network = None;
    p.frozen = false;
    p.settings.preset = Preset::LanNative;
    Ok(p)
}
fn payload(vectors: &[VectorData], sv: u32) -> Result<&[u8; 124]> {
    Ok(&vectors
        .iter()
        .find(|v| v.sv_number == sv)
        .ok_or_else(|| format!("missing SV {sv:08X}"))?
        .payload)
}
fn float(bytes: &[u8], index: usize) -> Result<f64> {
    Ok(f64::from(f32::from_be_bytes(
        bytes
            .get(index..index + 4)
            .ok_or("float span")?
            .try_into()?,
    )))
}
fn integer(bytes: &[u8], index: usize) -> Result<u32> {
    Ok(u32::from_be_bytes(
        bytes
            .get(index..index + 4)
            .ok_or("integer span")?
            .try_into()?,
    ))
}
fn settings(preset: Preset) -> Settings {
    Settings {
        preset,
        ..Default::default()
    }
}
fn converted(
    directory: &Path,
    sv: u32,
    mut bytes: [u8; 124],
    preset: Preset,
) -> Result<Vec<VectorData>> {
    let base = base(directory)?;
    let definition = base.registry().get(sv).ok_or("missing conversion SV")?;
    convert_payload(&mut bytes, definition, &settings(preset));
    Ok(vec![VectorData {
        sv_number: sv,
        name: definition.name.clone(),
        payload: bytes,
    }])
}
fn prepared() -> [u8; 124] {
    let mut bytes = [0; 124];
    bytes[..4].copy_from_slice(&15.8_f32.to_be_bytes());
    bytes[4..8].copy_from_slice(&1_f32.to_be_bytes());
    bytes
}
// Concrete in-memory endpoints exercise the production Engine with no hardware.
#[derive(Default)]
struct MemoryCom {
    name: Option<String>,
    lines: Vec<String>,
    starts: u32,
    stops: u32,
}
impl ComEmitter for MemoryCom {
    fn open(&mut self, name: &str) -> std::result::Result<(), String> {
        if self.name.is_none() {
            self.starts += 1;
        }
        self.name = Some(name.into());
        Ok(())
    }
    fn close(&mut self) {
        if self.name.take().is_some() {
            self.stops += 1;
        }
    }
    fn is_open(&self) -> bool {
        self.name.is_some()
    }
    fn port_name(&self) -> Option<&str> {
        self.name.as_deref()
    }
    fn lines_sent(&self) -> u64 {
        self.lines.len() as u64
    }
    fn send_line(&mut self, line: &str) -> std::result::Result<(), String> {
        self.lines.push(line.into());
        Ok(())
    }
    fn ports(&self) -> Vec<String> {
        vec!["COM2".into()]
    }
}
#[derive(Default)]
struct MemoryLan {
    name: Option<String>,
    starts: u32,
    stops: u32,
    frames: u64,
}
impl LanEmitter for MemoryLan {
    fn open(&mut self, name: &str) -> std::result::Result<(), String> {
        if self.name.is_none() {
            self.starts += 1;
        }
        self.name = Some(name.into());
        Ok(())
    }
    fn close(&mut self) {
        if self.name.take().is_some() {
            self.stops += 1;
        }
    }
    fn is_open(&self) -> bool {
        self.name.is_some()
    }
    fn adapter_name(&self) -> Option<&str> {
        self.name.as_deref()
    }
    fn frames_sent(&self) -> u64 {
        self.frames
    }
    fn send_superframe(&mut self, v: &[VectorData], _: u32) -> std::result::Result<(), String> {
        self.frames += v.len() as u64;
        Ok(())
    }
    fn adapters(&self) -> Vec<String> {
        vec!["fake0".into()]
    }
    fn npcap_available(&self) -> bool {
        false
    }
}
type MemoryEngine = Engine<MemoryCom, MemoryLan>;
fn engine(directory: &Path, templates: Vec<Template>) -> Result<MemoryEngine> {
    Ok(Engine::new(
        EmuSettings::default(),
        templates,
        base(directory)?,
        MemoryCom::default(),
        MemoryLan::default(),
    ))
}
fn control_engine(directory: &Path) -> Result<MemoryEngine> {
    engine(
        directory,
        vec![Template {
            name: "SelfTest".into(),
            device_type: "FLECS".into(),
            variables: vec![Variable {
                name: "Pump A Pressure".into(),
                legacy_name: "Pump A Pressure".into(),
                unit: "psi".into(),
                base: 100.,
                ..Default::default()
            }],
            ..Default::default()
        }],
    )
}
fn reply(engine: &mut MemoryEngine, line: &str) -> Result<Value> {
    Ok(serde_json::from_str(
        &control::process_line(line, engine).json,
    )?)
}
fn command(case: &mut Case, engine: &mut MemoryEngine, line: &str) -> Result<Value> {
    let value = reply(engine, line)?;
    case.check(value["ok"] == true, format!("{line}: {value}"));
    Ok(value)
}
struct ProbeExecutor(Mutex<MemoryEngine>);
impl icem_transport::emulator_control::Executor for ProbeExecutor {
    fn execute(&self, line: &str) -> ControlReply {
        match self.0.lock() {
            Ok(mut engine) => control::process_line(line, &mut *engine),
            Err(_) => ControlReply {
                cmd: String::new(),
                json: control::to_json(false, "", None, Some("selftest engine lock poisoned")),
                start_watch: false,
                quit: false,
            },
        }
    }
    fn watch_tick(&self) -> String {
        self.execute("status").json
    }
}
fn execute(id: u8, c: &mut Case, directory: &Path) -> Result {
    match id {
        1 => {
            let bytes = std::array::from_fn::<_, 124, _>(|i| i as u8);
            let frame = ace::build([0, 0x80, 0x2f, 0x23, 0xf4, 0x44], EQUIPMENT, PUMP, &bytes);
            let parsed = ace::parse(&frame).ok_or("valid frame rejected")?;
            c.check(parsed.equipment == EQUIPMENT, "equipment roundtrip");
            c.check(parsed.sv == PUMP, "SV roundtrip");
            c.check(parsed.payload == bytes, "payload roundtrip");
        }
        2 => {
            let frame = ace::build(
                [0, 0x80, 0x2f, 0x23, 0xf4, 0x44],
                EQUIPMENT,
                PUMP,
                &[0; 124],
            );
            let mut bad = frame;
            bad[17] = 255;
            c.check(ace::parse(&bad).is_none(), "magic byte17 FF accepted");
            bad = frame;
            bad[0] = 255;
            c.check(ace::parse(&bad).is_none(), "destination byte0 FF accepted");
            c.check(ace::parse(&frame[..151]).is_none(), "151 bytes accepted");
        }
        3 => {
            let frame = ace::build(
                [0, 0x80, 0x2f, 0x23, 0xf4, 0x44],
                EQUIPMENT,
                PUMP,
                &[0; 124],
            );
            c.check(
                frame[..28]
                    == [
                        3, 0, 1, 0, 0, 32, 0, 128, 47, 35, 244, 68, 0, 138, 3, 3, 8, 0, 208, 13, 0,
                        168, 111, 70, 19, 18, 2, 6,
                    ],
                "literal 28-byte header",
            );
            c.check(
                frame[28..].iter().all(|b| *b == 0),
                "124 zero payload bytes",
            );
        }
        4 => {
            #[cfg(windows)]
            {
                let mut emitter: icem_platform_windows::ComSerialEmitter = Default::default();
                let error = emitter.open("COM_NONEXISTENT_999");
                c.check(
                    error.as_ref().err().is_some_and(|e| !e.is_empty()),
                    "nonexistent COM must return nonempty error",
                );
                c.check(!emitter.is_open(), "COM must remain closed");
            }
            #[cfg(not(windows))]
            return Err("native COM selftest requires Windows".into());
        }
        5 | 20 => {
            let mut p = pipeline(directory)?;
            let baseline = p.superframe(EQUIPMENT, 0.);
            let clean = payload(&baseline, PUMP)?;
            p.can_transmit(PUMP, "Pump A Pressure")?;
            p.set_override(PUMP, "Pump A Pressure", 123.45);
            let edited = p.superframe(EQUIPMENT, 0.);
            let edited = payload(&edited, PUMP)?;
            c.near(
                float(edited, 0)?,
                f64::from(123.45_f32),
                1e-3,
                "pressure at O4/payload0",
            );
            c.evidence.push(format!(
                "Pump A Pressure O=4 payload[0..4]={:02X?} = {}",
                &edited[..4],
                float(edited, 0)?
            ));
            p.can_transmit(OPS, "Cmnt Ops Track Descript")?;
            if id == 5 {
                p.set_override(PUMP, "Side A Kicked Out?", 3.);
                let v = p.superframe(EQUIPMENT, 0.);
                let b = payload(&v, PUMP)?;
                c.check(integer(b, 16)? == 3, "HX O20 payload16 BE must equal3");
                c.evidence.push(format!(
                    "Side A Kicked Out? O=20 payload[16..20]={:02X?}",
                    &b[16..20]
                ));
                p.set_override(OPS, "Cmnt Ops Track Descript", 1.);
                let v = p.superframe(EQUIPMENT, 0.);
                let b = payload(&v, OPS)?;
                c.check(
                    b[4] == b'1' && b[5..].iter().all(|b| *b == 0),
                    "UC O8 size120 ASCII1/NUL",
                );
                c.evidence
                    .push(format!("UC O=8 payload[4..20]={:02X?}", &b[4..20]));
                p.clear_override(PUMP, "Pump A Pressure");
                let v = p.superframe(EQUIPMENT, 0.);
                c.near(
                    float(payload(&v, PUMP)?, 0)?,
                    float(clean, 0)?,
                    1e-6,
                    "clear returns baseline",
                );
                c.evidence
                    .push(format!("ClearOverride baseline = {}", float(clean, 0)?));
            } else {
                c.check(edited[4..8] == clean[4..8], "neighbor O8 untouched");
                let sig = p
                    .base
                    .registry()
                    .get(OPS)
                    .and_then(|v| {
                        v.signals
                            .iter()
                            .find(|s| s.name == "Cmnt Ops Track Descript")
                    })
                    .ok_or("missing UC definition")?;
                c.check(
                    sig.byte_offset == 8 && sig.size == 120 && sig.span(124) == Some(4..124),
                    "UC full span",
                );
                p.set_override(0x13120203, "Unit Number", 11038534.);
                let v = p.superframe(EQUIPMENT, 0.);
                let b = payload(&v, 0x13120203)?;
                c.check(integer(b, 0)? == 11038534, "Unit Number O4 BE");
                c.evidence
                    .push(format!("Unit Number payload[0..4]={:02X?}", &b[..4]));
            }
        }
        6 => {
            let b = base(directory)?;
            for sv in FIRMWARE_SVS {
                let Some(definition) = b.registry().get(sv) else {
                    c.check(false, format!("missing firmware {sv:08X}"));
                    continue;
                };
                c.check(!definition.signals.is_empty(), format!("{sv:08X} empty"));
                for s in &definition.signals {
                    c.check(
                        s.byte_offset >= 4 && s.span(124).is_some(),
                        format!("{sv:08X} {} span", s.name),
                    );
                }
            }
            for sv in LEGACY_SVS {
                c.check(
                    b.registry().get(sv).is_some(),
                    format!("legacy {sv:08X} retained"),
                );
            }
        }
        7 => {
            let fw = settings(Preset::FirmwareDefault);
            let metric = settings(Preset::Metric);
            for (a, b, t, label) in [
                (fw.to_target(1., "bbl/min"), 42., 1e-9, "FW flow"),
                (fw.to_target(212., "°F"), 100., 1e-9, "FW temperature"),
                (metric.to_target(1., "ppg"), 0.119826, 1e-6, "density"),
                (metric.to_target(1., "ft"), 0.3048, 1e-9, "level"),
                (metric.to_target(1., "psi"), 0.0689476, 1e-6, "pressure"),
                (fw.to_wire(42., "bbl/min"), 1., 1e-9, "inverse"),
                (fw.to_target(3., "gpm"), 3., 1e-9, "identity"),
            ] {
                c.near(a, b, t, label);
            }
            groups(
                c,
                &[
                    ("bbl/min", Group::Flow),
                    ("°F", Group::Temperature),
                    ("psi", Group::Pressure),
                    ("char", Group::None),
                ],
            );
        }
        8 => {
            let mut b = base(directory)?;
            let six = b.superframe(EQUIPMENT, 0.);
            c.check(
                six.iter().map(|v| v.sv_number).eq(LEGACY_SVS),
                "default six/order",
            );
            for v in &six {
                c.check(v.payload.len() == 124, "legacy payload length");
            }
            b.include_firmware_tables = true;
            let full = b.superframe(EQUIPMENT, 0.);
            c.check(
                full.iter()
                    .map(|v| v.sv_number)
                    .eq(LEGACY_SVS.into_iter().chain(FIRMWARE_SVS)),
                "full20/order",
            );
            for v in &full {
                c.check(v.payload.len() == 124, "full payload length");
            }
            for (p, expected, tol) in [
                (Preset::FirmwareDefault, 42., 1e-4),
                (Preset::Metric, 0.158987, 1e-6),
            ] {
                let out = converted(directory, MIX, prepared(), p)?;
                c.check(out.len() == 1, "prepared vector count");
                c.near(
                    float(payload(&out, MIX)?, 4)?,
                    expected,
                    tol,
                    "prepared flow O8",
                );
            }
        }
        9 => {
            let mut s = settings(Preset::Metric);
            c.check(s.target_unit(Group::Flow) == "m³/min", "metric flow target");
            c.check(
                s.target_unit(Group::Pressure) == "bar",
                "metric pressure target",
            );
            s.overrides.insert(Group::Flow, "gpm".into());
            c.check(s.target_unit(Group::Flow) == "gpm", "flow override");
            numeric_abs(
                c,
                &[
                    (1., "bbl/min", "gpm", 42., 1e-9),
                    (212., "°F", "°C", 100., 1e-9),
                    (5., "psi", "psi", 5., 1e-9),
                ],
            );
            c.near(
                settings(Preset::LanNative).to_target(3., "bbl/min"),
                3.,
                1e-9,
                "LanNative identity",
            );
            c.near(
                settings(Preset::Metric).to_target(1., "bbl/min"),
                0.158987,
                1e-6,
                "metric flow",
            );
            groups(
                c,
                &[
                    ("mA", Group::None),
                    ("bpm", Group::None),
                    ("char", Group::None),
                    ("m", Group::Level),
                    ("psi", Group::Pressure),
                ],
            );
        }
        10 => {
            for (preset, expected, tol) in [(2, 0.00378541, 1e-8), (0, 1., 1e-12)] {
                let template = Template {
                    name: "T10 fake".into(),
                    device_type: "FLECS".into(),
                    variables: vec![Variable {
                        name: "Flow Test".into(),
                        legacy_name: "Flow Test".into(),
                        unit: "gpm".into(),
                        base: 1.,
                        ..Default::default()
                    }],
                    ..Default::default()
                };
                let mut e = engine(directory, vec![template])?;
                command(c, &mut e, "setport COM2")?;
                command(c, &mut e, &format!("setpreset {preset}"))?;
                command(c, &mut e, "start com")?;
                command(c, &mut e, "setemulate com 1")?;
                e.tick();
                c.check(
                    e.com_emitter().lines.len() == 1,
                    "real runtime emitted COM tick",
                );
                let status = command(c, &mut e, "status")?;
                c.near(
                    status["data"]["com"]["values"]["Flow Test"]
                        .as_f64()
                        .ok_or("missing COM tick value")?,
                    expected,
                    tol,
                    "COM converted tick",
                );
            }
        }
        11 => numeric(c, NUMERIC_MATRIX),
        12 => {
            groups(
                c,
                &[
                    ("GPM", Group::Flow),
                    ("PPg", Group::Density),
                    ("PSI", Group::Pressure),
                    ("KPA", Group::Pressure),
                    (" degf ", Group::Temperature),
                ],
            );
            numeric(
                c,
                &[
                    (1., "gpm", "bbl/min", 1. / 42., 1e-9),
                    (1., "lb/gal", "g/cc", 0.119826, 1e-5),
                    (1., "g/cm3", "ppg", 8.3454, 1e-5),
                    (1., "kg/m3", "ppg", 1. / 119.826, 1e-5),
                    (1., "m3", "gal", 264.172, 1e-5),
                    (1., "psia", "bar", 0.0689476, 1e-5),
                    (1., "degf", "°c", units::convert(1., "f", "°c"), 1e-12),
                ],
            );
        }
        13 => {
            for unit in [
                "mA", "none", "char", "%", "Hz", "bpm", "sacks", "f3/sack", "gal/sack", "psi/ma",
                "ft/min", "", "mA",
            ] {
                groups(c, &[(unit, Group::None)]);
            }
            groups(
                c,
                &[
                    ("m", Group::Level),
                    ("m3", Group::Volume),
                    ("m3/min", Group::Flow),
                ],
            );
            numeric_abs(
                c,
                &[
                    (5., "mA", "gpm", 5., 1e-12),
                    (5., "bpm", "gpm", 5., 1e-12),
                    (5., "", "°c", 5., 1e-12),
                ],
            );
        }
        14 => {
            for preset in PRESETS {
                let s = settings(preset);
                for (unit, v) in [
                    ("bbl/min", 3.),
                    ("°f", 98.6),
                    ("ppg", 15.8),
                    ("bbl", 120.),
                    ("ft", 6.5),
                    ("psi", 2500.),
                ] {
                    let w = s.to_wire(s.to_target(v, unit), unit);
                    c.check(
                        (w - v).abs() / v.abs() < 1e-9,
                        format!("{preset:?}/{unit} roundtrip"),
                    );
                }
            }
            c.check(
                units::convert(units::convert(-40., "°f", "°c"), "°c", "°f") == -40.,
                "-40 exact roundtrip",
            );
            c.near(
                units::convert(units::convert(98.6, "°f", "°c"), "°c", "°f"),
                98.6,
                1e-9,
                "98.6 roundtrip",
            );
        }
        15 => {
            for (p, row) in PRESETS.into_iter().zip([
                ["gpm", "°C", "ppg", "bbl", "ft", "psi"],
                ["bbl/min", "°F", "lb/gal", "bbl", "ft", "psi"],
                ["m³/min", "°C", "g/cc", "m³", "m", "bar"],
                ["bbl/min", "°F", "ppg", "bbl", "ft", "psi"],
            ]) {
                for (g, expected) in Group::ALL.into_iter().zip(row) {
                    c.check(
                        units::display_unit(g, p) == expected,
                        format!("{p:?}/{g:?} display"),
                    );
                }
            }
            for g in Group::ALL {
                for unit in units::units_of(g) {
                    groups(c, &[(unit, g)]);
                }
            }
            let mut s = settings(Preset::Metric);
            s.overrides.insert(Group::Flow, "gpm".into());
            c.check(s.target_unit(Group::Flow) == "gpm", "override target");
            c.check(s.target_unit(Group::Pressure) == "bar", "pressure target");
        }
        16 => {
            let b = base(directory)?;
            let s = settings(Preset::FirmwareDefault);
            let mut count = 0;
            for sv in FIRMWARE_SVS {
                let Some(definition) = b.registry().get(sv) else {
                    c.check(false, format!("missing firmware {sv:08X}"));
                    continue;
                };
                for sig in &definition.signals {
                    count += 1;
                    c.check(
                        !s.needs_conversion(&sig.uom),
                        format!("{sv:08X}/{} firmware-native {}", sig.name, sig.uom),
                    );
                }
            }
            c.evidence
                .push(format!("{count} firmware signals in 14 tables checked"));
            c.check(s.needs_conversion("bbl/min"), "legacy bbl/min conversion");
            c.check(s.needs_conversion("f"), "legacy f conversion");
            c.check(!s.needs_conversion("lb/gal"), "firmware lb/gal identity");
            c.check(!s.needs_conversion("g/cc"), "firmware g/cc identity");
        }
        17 => {
            for (p, rate, density, tol) in [
                (Preset::FirmwareDefault, 42., f64::from(15.8_f32), 1e-4),
                (
                    Preset::Metric,
                    0.158987,
                    f64::from(15.8_f32 * 0.119826_f32),
                    0.001,
                ),
            ] {
                let out = converted(directory, MIX, prepared(), p)?;
                let bytes = payload(&out, MIX)?;
                c.near(float(bytes, 4)?, rate, tol, "flow");
                c.near(
                    float(bytes, 0)?,
                    density,
                    if p == Preset::FirmwareDefault {
                        0.
                    } else {
                        0.001
                    },
                    "density",
                );
                let mut bytes = [0; 124];
                bytes[4..].fill(0xab);
                let out = converted(directory, OPS, bytes, p)?;
                c.check(
                    payload(&out, OPS)?[4..].iter().all(|b| *b == 0xab),
                    "120 UC bytes unchanged",
                );
            }
        }
        18 => numeric_abs(
            c,
            &[
                (1., "°f", "gpm", 1., 1e-12),
                (212., "°f", "bbl", 212., 1e-12),
                (1., "bbl/min", "psi", 1., 1e-12),
                (1., "ppg", "ft", 1., 1e-12),
                (1., "ft", "kpa", 1., 1e-12),
                (1., "psi", "°c", 1., 1e-12),
            ],
        ),
        19 => {
            let b = base(directory)?;
            let s = settings(Preset::FirmwareDefault);
            c.check(!s.needs_conversion("g/cc"), "g/cc needs_conversion false");
            let sv = 0x1312021b;
            let def = b.registry().get(sv).ok_or("missing HPVT")?;
            let sig = def
                .signals
                .iter()
                .find(|s| s.name == "MB_DensHPVT")
                .ok_or("missing MB_DensHPVT")?;
            let span = sig.span(124).ok_or("HPVT span")?;
            c.check(sig.uom == "g/cc", "actual HPVT native g/cc");
            let mut bytes = [0; 124];
            bytes
                .get_mut(span.start..span.start + 4)
                .ok_or("HPVT float span")?
                .copy_from_slice(&1.19_f32.to_be_bytes());
            let out = converted(directory, sv, bytes, Preset::FirmwareDefault)?;
            c.check(out.len() == 1, "HPVT vector count");
            c.near(
                float(payload(&out, sv)?, span.start)?,
                f64::from(1.19_f32),
                0.,
                "HPVT exact identity",
            );
        }
        21 => {
            let mut e = control_engine(directory)?;
            for line in ["status", "  status  ", r#"{"cmd":"status"}"#] {
                command(c, &mut e, line)?;
            }
            command(c, &mut e, "set \"Pump A Pressure\" 5000")?;
            let v = command(c, &mut e, "get com.Pump A Pressure")?;
            c.near(
                v["data"]["value"].as_f64().ok_or("get value")?,
                5000.,
                1e-9,
                "quoted set applied",
            );
            command(c, &mut e, "set Pump A Pressure 123.5")?;
            let v = command(c, &mut e, "get com.Pump A Pressure")?;
            c.near(
                v["data"]["value"].as_f64().ok_or("get value")?,
                123.5,
                1e-9,
                "bare tokens set applied",
            );
            for line in [
                "",
                "   ",
                "{not json",
                "[]",
                r#"{"value":1}"#,
                "set",
                "set foo",
                "set foo bar",
            ] {
                let v = reply(&mut e, line)?;
                c.check(v["ok"] == false, format!("malformed {line:?}: {v}"));
            }
            let before = command(c, &mut e, "get com.Pump A Pressure")?;
            command(c, &mut e, "set lan.Pump A Pressure 42")?;
            let v = command(c, &mut e, "get lan.Pump A Pressure")?;
            c.near(
                v["data"]["value"].as_f64().ok_or("LAN get")?,
                42.,
                1e-9,
                "LAN prefix applied",
            );
            let after = command(c, &mut e, "get com.Pump A Pressure")?;
            c.check(
                after["data"] == before["data"],
                "LAN prefix isolated from COM",
            );
        }
        22 => {
            let mut e = control_engine(directory)?;
            for line in [
                "frobnicate",
                "start",
                "start foo",
                "stop udp",
                "get No Such Variable",
                "set No Such Variable 1",
                "profile No Such Template",
                r#"{"cmd":"set","name":"Pump A Pressure","value":"abc"}"#,
                r#"{"cmd":"set","name":"Pump A Pressure","value":1,"sv":"zz"}"#,
            ] {
                let v = reply(&mut e, line)?;
                c.check(
                    v["ok"] == false && v.get("error").is_some(),
                    format!("error expected {line}: {v}"),
                );
            }
        }
        23 => {
            let mut e = control_engine(directory)?;
            command(c, &mut e, "set \"Pump A Pressure\" 5000")?;
            let value = command(c, &mut e, "get \"com.Pump A Pressure\"")?;
            c.check(value["data"]["value"] == 5000., "get after set5000");
            command(c, &mut e, "clear \"Pump A Pressure\"")?;
            let value = command(c, &mut e, "get \"com.Pump A Pressure\"")?;
            c.check(value["data"]["value"] == 100., "clear returns base100");
            required(directory)?;
            let catalog = simulation::load_catalog(directory);
            c.check(
                catalog.warnings.is_empty(),
                format!("template warnings {:?}", catalog.warnings),
            );
            let mut sim = simulation::Engine::new(catalog.templates);
            let template = sim.current().ok_or("no current actual template")?;
            let (index, target) = template
                .variables
                .iter()
                .enumerate()
                .find(|(_, v)| !v.format.eq_ignore_ascii_case("elapsed") && v.toggle_period <= 0.)
                .ok_or("no actual target")?;
            let target = target.clone();
            sim.set_override(&target.name, 777.25);
            let values = sim.tick();
            c.near(
                *values.get(index).ok_or("tick index")?,
                777.25,
                1e-9,
                "actual simulation override tick1",
            );
            sim.clear_override(&target.name);
            let values = sim.tick();
            c.near(
                *values.get(index).ok_or("tick2 index")?,
                simulation::compute(&target, 2.),
                1e-9,
                "actual simulation clear tick2",
            );
            c.check(
                sim.last_values().len() == values.len(),
                "LastValues populated",
            );
        }
        24 => {
            let mut e = control_engine(directory)?;
            command(c, &mut e, "setport COM2")?;
            command(c, &mut e, "setadapter fake0")?;
            for line in [
                "start com",
                "start com",
                "stop com",
                "stop com",
                "start lan",
                "stop lan",
            ] {
                command(c, &mut e, line)?;
            }
            let com = e.com_emitter();
            let lan = e.lan_emitter();
            c.check(
                com.starts == 1 && com.stops == 1,
                format!("COM effective transitions {}/{}", com.starts, com.stops),
            );
            c.check(
                lan.starts == 1 && lan.stops == 1,
                format!("LAN effective transitions {}/{}", lan.starts, lan.stops),
            );
        }
        25 => {
            let mut e = control_engine(directory)?;
            let v = command(c, &mut e, "status")?;
            let data = &v["data"];
            for key in ["uptimeSec", "com", "lan", "units", "overrides", "selfTest"] {
                c.check(data.get(key).is_some(), format!("status missing {key}"));
            }
            for key in [
                "streaming",
                "portOpen",
                "port",
                "baud",
                "linesSent",
                "linesPerSec",
                "template",
                "values",
            ] {
                c.check(
                    data["com"].get(key).is_some(),
                    format!("status.com missing {key}"),
                );
            }
            for key in [
                "streaming",
                "adapterOpen",
                "framesSent",
                "framesPerSec",
                "equipment",
                "svCount",
                "targets",
            ] {
                c.check(
                    data["lan"].get(key).is_some(),
                    format!("status.lan missing {key}"),
                );
            }
            c.check(
                data.get("selfTest") == Some(&Value::Null),
                "selfTest is required null",
            );
        }
        26 => {
            let executor = Arc::new(ProbeExecutor(Mutex::new(control_engine(directory)?)));
            #[cfg(windows)]
            icem_transport::emulator_control::selftest_probe(executor)?;
            #[cfg(not(windows))]
            {
                let _ = executor;
                return Err("native named pipe selftest requires Windows".into());
            }
        }
        _ => return Err("unknown selftest ID".into()),
    }
    Ok(())
}
fn groups(c: &mut Case, rows: &[(&str, Group)]) {
    for (unit, group) in rows {
        c.check(
            units::group_of(unit) == *group,
            format!("GroupOf({unit:?}) == {group:?}"),
        );
    }
}
type Numeric = (f64, &'static str, &'static str, f64, f64);
fn numeric(c: &mut Case, rows: &[Numeric]) {
    for &(v, from, to, expected, relative) in rows {
        c.near(
            units::convert(v, from, to),
            expected,
            relative * expected.abs().max(1.),
            &format!("Convert({v},{from},{to})"),
        );
    }
}
fn numeric_abs(c: &mut Case, rows: &[Numeric]) {
    for &(v, from, to, expected, tolerance) in rows {
        c.near(
            units::convert(v, from, to),
            expected,
            tolerance,
            &format!("Convert({v},{from},{to})"),
        );
    }
}
const NUMERIC_MATRIX: &[Numeric] = &[
    (1., "bbl/min", "gpm", 42., 1e-9),
    (42., "gpm", "bbl/min", 1., 1e-9),
    (1., "bbl/min", "m3/min", 0.158987, 1e-5),
    (1., "gpm", "m3/min", 0.00378541, 1e-5),
    (1., "m3/min", "gpm", 264.172, 1e-5),
    (1., "m3/min", "bbl/min", 6.28981, 1e-5),
    (1., "bbl", "gal", 42., 1e-9),
    (1., "gal", "bbl", 1. / 42., 1e-9),
    (1., "bbl", "m3", 0.158987, 1e-5),
    (1., "gal", "m3", 0.00378541, 1e-5),
    (1., "ppg", "g/cc", 0.119826, 1e-5),
    (1., "g/cc", "ppg", 8.3454, 1e-5),
    (1., "ppg", "kg/m3", 119.826, 1e-5),
    (1., "g/cc", "kg/m3", 1000., 1e-9),
    (1., "ft", "m", 0.3048, 1e-9),
    (1., "m", "ft", 3.28084, 1e-5),
    (1., "psi", "bar", 0.0689476, 1e-5),
    (1., "psi", "kpa", 6.89476, 1e-5),
    (1., "psi", "mpa", 0.00689476, 1e-5),
    (1., "bar", "kpa", 100., 1e-9),
    (212., "°f", "°c", 100., 1e-9),
    (100., "°c", "°f", 212., 1e-9),
    (-40., "°f", "°c", -40., 0.),
    (32., "°f", "°c", 0., 0.),
];
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn dispatch_is_first_argument_only() {
        assert!(requested(Some("  --SeLfTeSt  ")));
        assert!(!requested(Some("--cli")));
        assert!(!requested(None));
    }
    #[test]
    fn fail_count_is_not_a_byte() -> Result {
        let mut c = Case::default();
        for _ in 0..267 {
            c.check(false, "firmware assertion");
        }
        assert_eq!(c.failures, 267);
        let mut out = Vec::new();
        summary(&mut out, c.failures)?;
        assert_eq!(String::from_utf8(out)?, "\n267 TEST(S) FAILED\n");
        Ok(())
    }
    #[test]
    fn portable_cases_use_real_catalogs() -> Result {
        let dir = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../iCemEmulator/templates");
        for id in (1..=26).filter(|id| !matches!(id, 4 | 26)) {
            let mut c = Case::default();
            execute(id, &mut c, &dir)?;
            assert_eq!(c.failures, 0, "T{id}: {:?}", c.details);
        }
        Ok(())
    }
    #[test]
    fn missing_assets_continue_to_late_cases_and_output_failure_is_error() -> Result {
        let mut out = Vec::new();
        assert!(run(&mut out, Path::new("/__icem_selftest_missing__"))? > 0);
        let text = String::from_utf8(out)?;
        assert!(text.contains("T26 FAIL"));
        assert!(!text.contains("ALL TESTS PASSED"));
        struct Broken;
        impl Write for Broken {
            fn write(&mut self, _: &[u8]) -> io::Result<usize> {
                Err(io::ErrorKind::BrokenPipe.into())
            }
            fn flush(&mut self) -> io::Result<()> {
                Err(io::ErrorKind::BrokenPipe.into())
            }
        }
        assert!(run(&mut Broken, Path::new("unused")).is_err());
        struct BrokenFlush;
        impl Write for BrokenFlush {
            fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
                Ok(bytes.len())
            }
            fn flush(&mut self) -> io::Result<()> {
                Err(io::ErrorKind::BrokenPipe.into())
            }
        }
        assert!(run(&mut BrokenFlush, Path::new("/__icem_selftest_missing__")).is_err());
        Ok(())
    }
}
