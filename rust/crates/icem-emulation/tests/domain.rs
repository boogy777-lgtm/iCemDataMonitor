use icem_emulation::{dfb::*, simulation::*, units::*};
use serde_json::json;
type TestResult = Result<(), Box<dyn std::error::Error>>;
#[test]
fn dfb_lag_latch_and_reset_order_are_stateful() {
    let mut lag = FirstOrderLag::default();
    assert_eq!(lag.execute(100.0, 1.0, 2.0), 50.0);
    assert_eq!(lag.execute(100.0, 1.0, 2.0), 75.0);
    assert_eq!(lag.execute(20.0, 1.0, 0.0), 20.0);
    assert!(
        FirstOrderLag::default()
            .execute(1.0, 1.0, f64::NAN)
            .is_nan()
    );
    let mut latch = KickoutLatch::default();
    assert_eq!(latch.execute(500.0, 500.0, false), (true, 500.0));
    assert_eq!(latch.execute(1.0, 500.0, false), (true, 500.0));
    assert_eq!(latch.execute(999.0, 500.0, true), (false, 0.0));
    assert_eq!(latch.execute(999.0, 500.0, false), (true, 999.0));
    let mut totals = Totalizer::default();
    assert_eq!(totals.execute(2.0, 0.5, false), (1.0, 1.0));
    assert_eq!(totals.execute(4.0, 0.5, true), (0.0, 3.0));
    assert_eq!(totals.execute(-2.0, 0.5, false), (-1.0, 2.0));
}
#[test]
fn units_aliases_cross_group_affine_and_preset_override_order() {
    assert_eq!(convert(1.0, "bbl/min", "gpm"), 42.0);
    assert_eq!(convert(32.0, "degF", "°c"), 0.0);
    assert_eq!(convert(100.0, "c", "°F"), 212.0);
    assert_eq!(convert(9.0, "°F", "gpm"), 9.0);
    assert_eq!(convert(9.0, "bpm", "gpm"), 9.0);
    assert_eq!(group_of("psi/mA"), Group::None);
    let mut settings = Settings {
        preset: Preset::Metric,
        ..Settings::default()
    };
    assert_eq!(settings.to_target(1.0, "ft"), 0.3048);
    assert_eq!(settings.to_wire(0.3048, "ft"), 1.0);
    settings.overrides.insert(Group::Level, "ft".into());
    assert_eq!(settings.to_target(1.0, "ft"), 1.0);
    settings.preset = Preset::FirmwareDefault;
    settings.overrides.insert(Group::Pressure, "bar".into());
    assert!(!settings.needs_conversion("psi"));
    assert_eq!(settings.to_target(1.0, "psi"), 0.0689476); // original separate NeedsConversion gate
    for group in Group::ALL {
        for unit in units_of(group) {
            assert_eq!(group_of(unit), group);
        }
    }
}
#[test]
fn formatter_rounding_exclusion_short_values_and_nonfinite() {
    let template = Template {
        variables: vec![
            Variable {
                exclude: true,
                ..Variable::default()
            },
            Variable {
                format: "elapsed".into(),
                ..Variable::default()
            },
            Variable {
                format: "int".into(),
                ..Variable::default()
            },
            Variable {
                format: "INT".into(),
                ..Variable::default()
            },
            Variable::default(),
            Variable::default(),
        ],
        ..Template::default()
    };
    assert_eq!(
        format_line(&[9.0, 1.25, 2.5, 3.5, -0.0, f64::INFINITY], &template),
        "1.2,2,4,-0.00,Infinity"
    );
    assert_eq!(format_line(&[], &template), "0.0,0,0,0.00,0.00");
}
#[test]
fn original_profile_has_34_variables_and_32_wire_fields() -> TestResult {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../../iCemEmulator/templates/flecs_4.53.json");
    let template = load_template(&path, None)?;
    assert_eq!(template.variables.len(), 34);
    assert_eq!(template.variables.iter().filter(|v| !v.exclude).count(), 32);
    let mut engine = Engine::new(vec![template]);
    let first = engine.tick();
    assert_eq!(engine.elapsed(), 1.0);
    assert_eq!(first.len(), 34);
    let (_, var) = engine.find_variable("pS pUmP pReSs").ok_or("no pressure")?;
    let name = var.name.clone();
    engine.set_override(&name, 1234.5);
    let second = engine.tick();
    let (i, _) = engine.find_variable(&name).ok_or("missing")?;
    assert_eq!(second[i], 1234.5);
    assert!(engine.clear_override(&name));
    assert_eq!(
        format_line(&first, engine.current().ok_or("no template")?)
            .split(',')
            .count(),
        32
    );
    Ok(())
}
#[test]
fn template_resolution_uses_family_index_and_profile_swap_preserves_clock() -> TestResult {
    let registry = json!({"variables":[{"name":"canonical","display":"Display","families":{"flecs":{"alias":"Alias","index":9}},"simulation":{"base":3.0}}]});
    let template = template_from_json(
        &json!({"name":"A","deviceType":"FLECS","variables":[{"registry":"Display","index":0},{"name":"time","index":1,"format":"elapsed"}]}),
        &registry,
    )?;
    assert_eq!(template.variables[0].name, "time");
    assert_eq!(template.variables[1].name, "Alias");
    assert_eq!(template.variables[1].index, 9);
    let other = Template {
        name: "B".into(),
        device_type: "PLC".into(),
        variables: vec![Variable {
            name: "time".into(),
            format: "elapsed".into(),
            ..Variable::default()
        }],
        ..Template::default()
    };
    let mut engine = Engine::new(vec![template, other]);
    engine.tick();
    engine.set_template("plc", "unknown");
    assert_eq!(engine.current().ok_or("missing")?.name, "B");
    assert_eq!(engine.tick(), [2.0]);
    engine.reset_clock();
    assert_eq!(engine.tick(), [1.0]);
    assert!(
        template_from_json(
            &json!({"name":"bad","deviceType":"FLECS","variables":[{"base":"wrong"}]}),
            &registry
        )
        .is_err()
    );
    Ok(())
}
