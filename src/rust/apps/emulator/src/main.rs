#![forbid(unsafe_code)]
//! Explicit development preview; not the production --cli/control-pipe contract.
use icem_emulation::simulation::{Engine, format_line, load_template};
use std::{io::Write, path::PathBuf, process::ExitCode, time::Duration};
struct Options {
    template: PathBuf,
    registry: Option<PathBuf>,
    ticks: u16,
    interval: u64,
    json: bool,
    overrides: Vec<(String, f64)>,
}
fn options() -> Result<Options, String> {
    let mut args = std::env::args().skip(1);
    let mut template = None;
    let mut registry = None;
    let mut ticks = 1;
    let mut interval = 1000;
    let mut json = false;
    let mut overrides = Vec::new();
    while let Some(key) = args.next() {
        let value = args.next().ok_or("each option requires a value")?;
        match key.as_str() {
            "--template" => template = Some(PathBuf::from(value)),
            "--registry" => registry = Some(PathBuf::from(value)),
            "--ticks" => ticks = value.parse::<u16>().map_err(|_| "invalid tick count")?,
            "--interval-ms" => interval = value.parse::<u64>().map_err(|_| "invalid interval")?,
            "--format" => {
                json = match value.as_str() {
                    "json" => true,
                    "flecs" => false,
                    _ => return Err("format must be flecs|json".into()),
                }
            }
            "--set" => {
                let (name, value) = value.rsplit_once('=').ok_or("set requires NAME=VALUE")?;
                let value = value.parse::<f64>().map_err(|_| "invalid override")?;
                if name.is_empty() || !value.is_finite() {
                    return Err("set requires a name and finite value".into());
                }
                overrides.push((name.into(), value));
            }
            _ => return Err(format!("unknown option {key}")),
        }
    }
    if !(1..=3600).contains(&ticks) || interval > 60000 {
        return Err("ticks must be 1..3600 and interval 0..60000".into());
    }
    Ok(Options {
        template: template.ok_or("--template is required")?,
        registry,
        ticks,
        interval,
        json,
        overrides,
    })
}
fn run(options: Options) -> Result<(), Box<dyn std::error::Error>> {
    let template = load_template(&options.template, options.registry.as_deref())?;
    let mut engine = Engine::new(vec![template]);
    for (name, value) in options.overrides {
        let canonical = engine
            .find_variable(&name)
            .map(|(_, v)| v.name.clone())
            .ok_or_else(|| format!("unknown variable '{name}'"))?;
        engine.set_override(&canonical, value);
    }
    eprintln!(
        "DEVELOPMENT EMULATOR preview; physical COM/LAN, control pipe, GPUI and tray pending. No hardware streams started."
    );
    let mut output = std::io::stdout().lock();
    for tick in 0..options.ticks {
        let values = engine.tick();
        let template = engine.current().ok_or("missing template")?;
        if options.json {
            let line = serde_json::json!({"template":template.name,"deviceType":template.device_type,"elapsed":engine.elapsed(),"values":values,"flecs":format_line(&values,template)});
            writeln!(output, "{line}")?;
        } else {
            output.write_all(format_line(&values, template).as_bytes())?;
            output.write_all(b"\r\n")?;
        }
        output.flush()?;
        if tick + 1 < options.ticks {
            std::thread::sleep(Duration::from_millis(options.interval));
        }
    }
    Ok(())
}
fn main() -> ExitCode {
    let options = match options() {
        Ok(options) => options,
        Err(error) => {
            eprintln!(
                "{error}\nUsage: icem-emulator-dev --template PATH [--registry PATH] [--ticks 1] [--interval-ms 1000] [--format flecs|json] [--set NAME=VALUE]"
            );
            return ExitCode::from(2);
        }
    };
    match run(options) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("{error}");
            ExitCode::from(1)
        }
    }
}
