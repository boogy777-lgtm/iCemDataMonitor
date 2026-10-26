#![forbid(unsafe_code)]
//! Diagnostic fixed DFB network runner. Never starts COM/LAN, host, pipe, GUI or tray.
use icem_emulation::cement::{
    CementNetwork, Context, Direction, SignalSample, SignalValue, WIRE_SIGNALS,
};
use std::{io::Write, process::ExitCode, time::Duration};
struct Options {
    ticks: u16,
    interval: u64,
    seed: i32,
    dt: f64,
    network: CementNetwork,
}
fn options() -> Result<Options, String> {
    let mut o = Options {
        ticks: 1,
        interval: 1000,
        seed: 7,
        dt: 1.0,
        network: CementNetwork::default(),
    };
    let mut args = std::env::args().skip(1);
    while let Some(key) = args.next() {
        let value = args.next().ok_or("each option requires a value")?;
        match key.as_str() {
            "--ticks" => o.ticks = value.parse().map_err(|_| "invalid ticks")?,
            "--interval-ms" => o.interval = value.parse().map_err(|_| "invalid interval")?,
            "--seed" => o.seed = value.parse().map_err(|_| "invalid seed")?,
            "--dt" => o.dt = value.parse().map_err(|_| "invalid dt")?,
            "--job-active" => {
                o.network.job_active = value
                    .parse()
                    .map_err(|_| "job-active requires true|false")?
            }
            "--reset" => {
                o.network.reset = value.parse().map_err(|_| "reset requires true|false")?
            }
            "--unit" => {
                o.network.sequencer.parameters.unit_number_value =
                    value.parse().map_err(|_| "invalid unit")?
            }
            "--rate-a" | "--rate-b" | "--opko-a" | "--opko-b" | "--water-valve" => {
                let number: f64 = value.parse().map_err(|_| "invalid setpoint")?;
                if !number.is_finite() {
                    return Err("setpoint must be finite".into());
                }
                match key.as_str() {
                    "--rate-a" => o.network.pump_a.inputs.rate = number,
                    "--rate-b" => o.network.pump_b.inputs.rate = number,
                    "--opko-a" => o.network.pump_a.inputs.opko_setting = number,
                    "--opko-b" => o.network.pump_b.inputs.opko_setting = number,
                    _ => o.network.mixer.inputs.water_valve_cmd = number,
                }
            }
            _ => return Err(format!("unknown option {key}")),
        }
    }
    if !(1..=3600).contains(&o.ticks)
        || o.interval > 60000
        || !o.dt.is_finite()
        || o.dt <= 0.0
        || o.dt > 3600.0
    {
        return Err("ticks must be 1..3600, interval 0..60000, dt finite and 0<dt<=3600".into());
    }
    Ok(o)
}
fn value(v: &SignalValue) -> serde_json::Value {
    match v {
        SignalValue::Float(v) => {
            serde_json::json!({"kind":"double", "value":v, "bits":format!("{:016x}",v.to_bits())})
        }
        SignalValue::Unsigned(v) => serde_json::json!({"kind":"uint", "value":v}),
        SignalValue::Bool(v) => serde_json::json!({"kind":"bool", "value":v}),
        SignalValue::Text(v) => serde_json::json!({"kind":"string", "value":v}),
    }
}
fn sample(s: SignalSample) -> serde_json::Value {
    let direction = match s.direction {
        Direction::Input => "input",
        Direction::Output => "output",
        Direction::Parameter => "parameter",
    };
    serde_json::json!({"path":s.path,"direction":direction,"source":s.source,"stored":value(&s.stored),"read":value(&s.value)})
}
fn run(mut o: Options) -> Result<(), Box<dyn std::error::Error>> {
    eprintln!(
        "DEVELOPMENT DFB trace; physical emitters, pipe, GPUI and tray pending. No hardware streams started."
    );
    let mut output = std::io::stdout().lock();
    let mut elapsed = 0.0;
    for tick in 1..=o.ticks {
        elapsed += o.dt;
        let mut ctx = Context::new(elapsed, o.dt, i64::from(tick), o.seed);
        o.network.execute(&mut ctx);
        let wire: serde_json::Map<_, _> = WIRE_SIGNALS
            .iter()
            .map(|s| {
                (
                    s.name.into(),
                    value(&SignalValue::Float((s.read)(&o.network))),
                )
            })
            .collect();
        let line = serde_json::json!({"tick":tick,"elapsed":elapsed,"dt":o.dt,"seed":o.seed,"signals":o.network.signals().into_iter().map(sample).collect::<Vec<_>>(),"parameters":o.network.parameters().into_iter().map(sample).collect::<Vec<_>>(),"wire":wire});
        writeln!(output, "{line}")?;
        output.flush()?;
        if tick < o.ticks {
            std::thread::sleep(Duration::from_millis(o.interval));
        }
    }
    Ok(())
}
fn main() -> ExitCode {
    let o = match options() {
        Ok(o) => o,
        Err(e) => {
            eprintln!(
                "{e}\nUsage: icem-dfb-dev [--ticks 1] [--interval-ms 1000] [--seed 7] [--dt 1] [--job-active true|false] [--reset true|false] [--unit 1] [--rate-a 3] [--rate-b 3] [--opko-a 3000] [--opko-b 3000] [--water-valve 60]"
            );
            return ExitCode::from(2);
        }
    };
    match run(o) {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("{e}");
            ExitCode::from(1)
        }
    }
}
