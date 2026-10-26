#![cfg(windows)]
use icem_emulation::cement::*;
use serde_json::Value;
use std::{collections::BTreeMap, path::PathBuf};
type TestResult<T = ()> = Result<T, Box<dyn std::error::Error>>;
fn rows<'a>(v: &'a Value, key: &str) -> TestResult<&'a Vec<Value>> {
    v[key]
        .as_array()
        .ok_or_else(|| format!("missing array {key}").into())
}
fn string<'a>(v: &'a Value, key: &str) -> TestResult<&'a str> {
    v[key]
        .as_str()
        .ok_or_else(|| format!("missing string {key}").into())
}
fn bits(v: &Value) -> TestResult<f64> {
    Ok(f64::from_bits(u64::from_str_radix(
        v.as_str().ok_or("not bits")?,
        16,
    )?))
}
fn same(a: f64, e: f64, label: &str) {
    if e.is_nan() {
        assert!(a.is_nan(), "{label}: {a:?} != NaN");
    } else if e == 0.0 {
        assert_eq!(a.to_bits(), e.to_bits(), "{label}: signed zero");
    } else if e.is_infinite() {
        assert_eq!(a, e, "{label}");
    } else {
        assert!(
            (a - e).abs() <= e.abs().max(1.0) * 1e-12,
            "{label}: {a:?} != {e:?}"
        );
    }
}
fn encoded(a: &SignalValue, e: &Value, label: &str) -> TestResult {
    match a {
        SignalValue::Float(v) => {
            assert_eq!(string(e, "kind")?, "double", "{label}");
            same(*v, bits(&e["bits"])?, label);
        }
        SignalValue::Unsigned(v) => {
            assert_eq!(string(e, "kind")?, "uint", "{label}");
            assert_eq!(Some(u64::from(*v)), e["value"].as_u64(), "{label}");
        }
        SignalValue::Bool(v) => {
            assert_eq!(string(e, "kind")?, "bool", "{label}");
            assert_eq!(Some(*v), e["value"].as_bool(), "{label}");
        }
        SignalValue::Text(v) => {
            assert_eq!(string(e, "kind")?, "string", "{label}");
            assert_eq!(serde_json::to_value(v)?, e["value"], "{label}");
        }
    }
    Ok(())
}
fn samples(actual: Vec<SignalSample>, expected: &[Value]) -> TestResult {
    let map: BTreeMap<_, _> = expected
        .iter()
        .map(|v| Ok((string(v, "path")?, v)))
        .collect::<TestResult<_>>()?;
    assert_eq!(map.len(), expected.len(), "duplicate source paths");
    assert_eq!(
        actual.len(),
        map.len(),
        "complete source port/parameter set"
    );
    for a in actual {
        let e = map
            .get(a.path.as_str())
            .ok_or_else(|| format!("extra path {}", a.path))?;
        let dir = match a.direction {
            Direction::Input => "input",
            Direction::Output => "output",
            Direction::Parameter => "parameter",
        };
        assert_eq!(dir, string(e, "direction")?, "{}", a.path);
        assert_eq!(serde_json::to_value(a.source)?, e["source"], "{}", a.path);
        encoded(&a.stored, &e["stored"], &format!("{} stored", a.path))?;
        encoded(&a.value, &e["read"], &format!("{} read", a.path))?;
    }
    Ok(())
}
fn snapshot(n: &CementNetwork, e: &Value) -> TestResult {
    samples(n.signals(), rows(e, "ports")?)?;
    samples(n.parameters(), rows(e, "parameters")?)?;
    let wire = e["wire"].as_object().ok_or("wire object")?;
    assert_eq!(WIRE_SIGNALS.len(), wire.len(), "complete wire-name set");
    for s in WIRE_SIGNALS {
        let expected = bits(
            wire.get(s.name)
                .ok_or_else(|| format!("extra wire {}", s.name))?,
        )?;
        same((s.read)(n), expected, s.name);
        same(
            n.try_wire_value(&s.name.to_ascii_lowercase())
                .ok_or("missing lower-case key")?,
            expected,
            s.name,
        );
        same(
            n.try_wire_value(&s.name.to_ascii_uppercase())
                .ok_or("missing upper-case key")?,
            expected,
            s.name,
        );
    }
    assert_eq!(n.try_wire_value("unregistered"), None);
    assert_eq!(n.wire_value("unregistered"), 0.0);
    Ok(())
}
fn change(n: &mut CementNetwork, i: usize) {
    match i {
        3 => {
            n.pump_a.inputs.rate = 12.0;
            n.pump_a.inputs.opko_setting = 1000.0;
        }
        4 => n.pump_a.inputs.rate = 0.0,
        5 => n.reset = true,
        6 => n.reset = false,
        7 => n.job_active = false,
        8 => n.job_active = true,
        9 => {
            n.mixer.inputs.water_valve_cmd = -50.0;
            n.mixer.inputs.cement_valve_cmd = 150.0;
        }
        10 => {
            n.mixer.inputs.water_valve_cmd = 125.0;
            n.mixer.inputs.cement_valve_cmd = -25.0;
        }
        11 => n.sequencer.parameters.unit_number_value = u32::MAX,
        12 => {
            n.pump_a.parameters.resistance = 225.0;
            n.pump_a.parameters.pressure_base = -500.0;
            n.pump_a.parameters.lag_tau_seconds = 0.25;
            n.pump_a.parameters.noise_sigma = 0.05;
            n.mixer.parameters.density_tau_seconds = 0.5;
            n.mixer.parameters.water_rate_gain = 4.2;
            n.mixer.parameters.pot_tau_seconds = 0.1;
            n.mixer.parameters.density_noise_sigma = 0.1;
            n.mixer.parameters.sacks_per_bbl = 1.1;
            n.mixer.parameters.blend_yield_value = 2.0;
            n.mixer.parameters.blend_wtr_req_value = 6.0;
            n.temperature.parameters.target = -30.0;
            n.temperature.parameters.tau_seconds = 0.1;
            n.sequencer.parameters.unit_number_value = 42;
        }
        13 => {
            n.raw_io.outputs.spare_analog_in[3] = 12.125;
            n.raw_io.outputs.spare_stage_total[0] = -7.0;
            n.raw_io.outputs.spare_job_total[1] = 83.0;
            n.raw_io.outputs.spare_counter_in[2] = 65537.0;
            n.raw_io.outputs.spare_combined1 = -0.0;
            n.raw_io.outputs.spare_combined2 = 1.5;
        }
        14 => {
            n.pump_a.inputs.reset = true;
            n.pump_b.inputs.reset = true;
            n.mixer.inputs.job_active = false;
            n.mixer.inputs.reset_stage = true;
            n.sequencer.inputs.job_active = false;
            n.sequencer.inputs.reset = true;
            n.f300_hpvt.inputs.flow_ref = 999.0;
        }
        15 => {
            n.reset = true;
            n.job_active = false;
        }
        16 => {
            n.reset = false;
            n.job_active = true;
        }
        17 => n.pump_b.inputs.rate = -2.0,
        18 => n.pump_b.inputs.opko_setting = 0.0,
        _ => {}
    }
}
#[test]
fn full_factory_matches_unmodified_original_csharp() -> TestResult {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let output = std::process::Command::new("pwsh")
        .args(["-NoProfile", "-File"])
        .arg(root.join("rust/tests/support/DfbNetworkOracle.ps1"))
        .arg("-RepoRoot")
        .arg(&root)
        .output()?;
    assert!(
        output.status.success(),
        "C# DFB oracle failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let oracle: Value = serde_json::from_slice(&output.stdout)?;
    assert!(
        string(&oracle, "runtime")?
            .split('.')
            .next()
            .ok_or("runtime")?
            .parse::<u32>()?
            >= 9
    );
    assert_eq!(serde_json::to_value(BLOCK_ORDER)?, oracle["blocks"]);
    let mut rng_draws = 0;
    for row in rows(&oracle, "random")? {
        let mut rng = SeededRandom::new(i32::try_from(row["seed"].as_i64().ok_or("seed")?)?);
        for e in rows(row, "values")? {
            assert_eq!(
                rng.next_double().to_bits(),
                bits(e)?.to_bits(),
                "seed {}",
                row["seed"]
            );
            rng_draws += 1;
        }
    }
    for row in rows(&oracle, "contexts")? {
        let mut ctx = Context::new(
            0.5,
            0.25,
            row["tick"].as_i64().ok_or("tick")?,
            i32::try_from(row["seed"].as_i64().ok_or("seed")?)?,
        );
        for e in rows(row, "values")? {
            assert_eq!(
                ctx.random.next_double().to_bits(),
                bits(e)?.to_bits(),
                "combined seed {row}"
            );
            rng_draws += 1;
        }
    }
    let mut frames = 0;
    for trajectory in rows(&oracle, "trajectories")? {
        let seed = i32::try_from(trajectory["seed"].as_i64().ok_or("seed")?)?;
        let mut n = CementNetwork::default();
        for (i, row) in rows(trajectory, "rows")?.iter().enumerate() {
            change(&mut n, i);
            if i > 0 {
                let mut ctx = Context::new(
                    row["elapsed"].as_f64().ok_or("elapsed")?,
                    row["dt"].as_f64().ok_or("dt")?,
                    row["tick"].as_i64().ok_or("tick")?,
                    seed,
                );
                n.execute(&mut ctx);
                assert_eq!(
                    ctx.random.next_double().to_bits(),
                    bits(&row["sentinel"])?.to_bits(),
                    "draw consumption seed={seed} step={i}"
                );
            }
            snapshot(&n, &row["state"]).map_err(|e| format!("seed={seed} step={i}: {e}"))?;
            frames += 1;
        }
    }
    for row in rows(&oracle, "edges")? {
        let v = bits(&row["input"])?;
        let target = string(row, "target")?;
        let mut n = CementNetwork::default();
        match target {
            "water" => n.mixer.inputs.water_valve_cmd = v,
            "blend" => n.mixer.inputs.blend_target = v,
            "pump" => n.pump_a.inputs.rate = v,
            _ => {}
        }
        let mut ctx = Context::new(
            if target == "elapsed" { v } else { 1.0 },
            if target == "dt" { v } else { 1.0 },
            1,
            7,
        );
        n.execute(&mut ctx);
        assert_eq!(
            ctx.random.next_double().to_bits(),
            bits(&row["sentinel"])?.to_bits(),
            "{row}"
        );
        snapshot(&n, &row["state"]).map_err(|e| format!("edge {target} {v}: {e}"))?;
    }
    let mut probe = CementNetwork::default();
    probe.raw_io.outputs.digital_inputs = 0xa55a;
    probe.raw_io.outputs.relay_out = 0x96;
    probe.raw_io.outputs.digital_outputs = u32::MAX;
    probe.raw_io.outputs.spare_analog_in[0] = 42.5;
    snapshot(&probe, &oracle["probe"])?;
    let report = serde_json::json!({"runtime":oracle["runtime"],"blocks":BLOCK_ORDER,"registered_ports":probe.signals().len(),"parameters":probe.parameters().len(),"wire_names":WIRE_SIGNALS.len(),"rng_draws_exact":rng_draws,"trajectory_frames":frames,"network_ticks":frames-rows(&oracle,"trajectories")?.len(),"edge_frames":rows(&oracle,"edges")?.len(),"bit_probe":true,"numeric_tolerance":"1e-12 relative/absolute floor1; zero sign/Infinity exact; NaN classification","rng_equality":"exact bits, including post-network sentinel","result":"PASS"});
    std::fs::write(
        root.join("rust/target/dfb-differential-report.json"),
        serde_json::to_vec_pretty(&report)?,
    )?;
    Ok(())
}
