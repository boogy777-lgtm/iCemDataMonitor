#![forbid(unsafe_code)]
//! Explicit LAN development trace, with no adapter, serial, pipe, host or UI side effects.
use icem_emulation::{
    cement::Context,
    lan::{BaseProvider, Registry},
    lan_pipeline::LanPipeline,
    units::{Group, Preset},
};
use icem_protocols::ace;
use std::{io::Write, path::PathBuf, process::ExitCode, time::Duration};
struct Options {
    templates: PathBuf,
    ticks: u16,
    interval: u64,
    equipment: u32,
    firmware: bool,
    emulate: bool,
    freeze_at: Option<u16>,
    preset: Preset,
    units: Vec<(Group, String)>,
    overrides: Vec<(u32, String, f64)>,
    hex: bool,
}
fn number(v: &str) -> Result<u32, String> {
    if let Some(v) = v.strip_prefix("0x").or_else(|| v.strip_prefix("0X")) {
        u32::from_str_radix(v, 16).map_err(|_| "invalid hexadecimal uint32".into())
    } else {
        v.parse().map_err(|_| "invalid decimal uint32".into())
    }
}
fn group(v: &str) -> Result<Group, String> {
    match v.trim().to_ascii_lowercase().as_str() {
        "flow" => Ok(Group::Flow),
        "temperature" => Ok(Group::Temperature),
        "density" => Ok(Group::Density),
        "volume" => Ok(Group::Volume),
        "level" => Ok(Group::Level),
        "pressure" => Ok(Group::Pressure),
        _ => Err("unknown unit group".into()),
    }
}
fn options() -> Result<Options, String> {
    let mut o = Options {
        templates: PathBuf::new(),
        ticks: 1,
        interval: 1000,
        equipment: 0x00a86f46,
        firmware: false,
        emulate: false,
        freeze_at: None,
        preset: Preset::LanNative,
        units: Vec::new(),
        overrides: Vec::new(),
        hex: false,
    };
    let mut args = std::env::args().skip(1);
    while let Some(key) = args.next() {
        let value = args.next().ok_or("each option requires a value")?;
        match key.as_str() {
            "--templates" => o.templates = value.into(),
            "--ticks" => o.ticks = value.parse().map_err(|_| "invalid ticks")?,
            "--interval-ms" => o.interval = value.parse().map_err(|_| "invalid interval")?,
            "--equipment" => o.equipment = number(&value)?,
            "--firmware" => {
                o.firmware = value.parse().map_err(|_| "firmware requires true|false")?
            }
            "--emulate" => o.emulate = value.parse().map_err(|_| "emulate requires true|false")?,
            "--freeze-at" => o.freeze_at = Some(value.parse().map_err(|_| "invalid freeze tick")?),
            "--preset" => {
                o.preset = Preset::try_from(value.parse::<u8>().map_err(|_| "invalid preset")?)?
            }
            "--unit" => {
                let (name, unit) = value.split_once('=').ok_or("unit requires GROUP=UNIT")?;
                o.units.push((group(name)?, unit.trim().into()));
            }
            "--set" => {
                let (key, value) = value.rsplit_once('=').ok_or("set requires SV:NAME=VALUE")?;
                let (sv, name) = key.split_once(':').ok_or("set requires SV:NAME=VALUE")?;
                let value: f64 = value.parse().map_err(|_| "invalid override")?;
                if name.is_empty() || !value.is_finite() {
                    return Err("set requires name and finite value".into());
                }
                o.overrides.push((number(sv)?, name.into(), value));
            }
            "--format" => {
                o.hex = match value.as_str() {
                    "json" => false,
                    "hex" => true,
                    _ => return Err("format must be json|hex".into()),
                }
            }
            _ => return Err(format!("unknown option {key}")),
        }
    }
    if o.templates.as_os_str().is_empty() {
        return Err("--templates is required".into());
    }
    if !(1..=3600).contains(&o.ticks)
        || o.interval > 60000
        || o.freeze_at.is_some_and(|t| t >= o.ticks)
    {
        return Err(
            "ticks must be 1..3600, interval 0..60000, freeze-at a zero-based tick < ticks".into(),
        );
    }
    Ok(o)
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
fn run(o: Options) -> Result<(), Box<dyn std::error::Error>> {
    if !o.templates.is_dir()
        || !o.templates.join("lantable_registry.json").is_file()
        || !o.templates.join("variable_registry.json").is_file()
    {
        return Err("--templates requires an existing directory with lantable_registry.json and variable_registry.json".into());
    }
    let firmware = o.templates.join("firmware_registry.json");
    let registry = Registry::load(
        &o.templates.join("lantable_registry.json"),
        firmware.is_file().then_some(firmware.as_path()),
    )?;
    for diagnostic in registry.diagnostics() {
        eprintln!("{diagnostic}");
    }
    let emulator = o.templates.join("variable_registry.emulator.json");
    let base = BaseProvider::load(
        registry,
        &o.templates.join("variable_registry.json"),
        emulator.is_file().then_some(emulator.as_path()),
    )?;
    let mut pipeline = LanPipeline::new(base);
    pipeline.base.include_firmware_tables = o.firmware;
    pipeline.frozen = !o.emulate;
    pipeline.settings.preset = o.preset;
    for (group, unit) in o.units {
        pipeline.settings.overrides.insert(group, unit);
    }
    if let Some(n) = &mut pipeline.network {
        n.sequencer.parameters.unit_number_value = o.equipment;
    }
    for (sv, name, value) in o.overrides {
        pipeline.can_transmit(sv, &name)?;
        // The original host resolves names before storing the case-sensitive override key.
        let canonical = pipeline
            .base
            .registry()
            .get(sv)
            .and_then(|v| {
                v.signals
                    .iter()
                    .find(|s| s.name.eq_ignore_ascii_case(&name))
            })
            .map(|s| s.name.clone())
            .ok_or("signal disappeared after validation")?;
        pipeline.set_override(sv, &canonical, value);
    }
    eprintln!(
        "DEVELOPMENT LAN trace; production CLI/IPC, physical emitters, GPUI and tray pending. No hardware streams started."
    );
    let mut output = std::io::stdout().lock();
    for tick in 0..o.ticks {
        if o.freeze_at == Some(tick) {
            pipeline.frozen = true;
        }
        let elapsed = f64::from(tick);
        if !pipeline.frozen
            && let Some(n) = &mut pipeline.network
        {
            n.execute(&mut Context::new(elapsed, 1.0, i64::from(tick), 7));
        }
        let vectors = pipeline.superframe(o.equipment, elapsed);
        let mut frames = Vec::new();
        for v in vectors {
            let frame = ace::build([2, 0, 0, 0, 0, 1], o.equipment, v.sv_number, &v.payload);
            if o.hex {
                writeln!(output, "{}", hex(&frame))?;
            } else {
                frames.push(serde_json::json!({"sv":v.sv_number,"svHex":format!("0x{:08X}",v.sv_number),"name":v.name,"payload":hex(&v.payload),"frame":hex(&frame)}));
            }
        }
        if !o.hex {
            let active = pipeline.active_sv_numbers();
            writeln!(
                output,
                "{}",
                serde_json::json!({"tick":tick,"elapsed":elapsed,"equipment":o.equipment,"frozen":pipeline.frozen,"activeSvNumbers":active,"frames":frames})
            )?;
        }
        output.flush()?;
        if tick + 1 < o.ticks {
            std::thread::sleep(Duration::from_millis(o.interval));
        }
    }
    Ok(())
}
fn main() -> ExitCode {
    let options = match options() {
        Ok(o) => o,
        Err(e) => {
            eprintln!(
                "{e}\nUsage: icem-lan-dev --templates PATH [--ticks 1] [--interval-ms 1000] [--equipment 0x00A86F46] [--firmware false] [--emulate false] [--freeze-at ZERO_BASED_TICK] [--preset 0..3] [--unit GROUP=UNIT] [--set SV:NAME=VALUE] [--format json|hex]"
            );
            return ExitCode::from(2);
        }
    };
    match run(options) {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("{e}");
            ExitCode::from(1)
        }
    }
}
