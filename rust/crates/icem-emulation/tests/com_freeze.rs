use icem_emulation::{simulation::*, units::*};
type R = Result<(), Box<dyn std::error::Error>>;
fn t(name: &str, n: usize) -> Template {
    Template {
        name: name.into(),
        device_type: "FLECS".into(),
        variables: (0..n)
            .map(|i| Variable {
                name: format!("Value{i}"),
                format: "elapsed".into(),
                unit: "ft".into(),
                ..Default::default()
            })
            .collect(),
        ..Default::default()
    }
}
#[test]
fn frozen_copy_seed_overrides_clear_and_original_projected_cache() -> R {
    let mut e = Engine::new(vec![t("a", 2)]);
    assert_eq!(e.frozen_values(), [1.0, 1.0]);
    e.set_override("Value0", 8.0);
    e.set_override("VALUE0", 9.0);
    assert_eq!(
        e.snapshot_overrides().keys().collect::<Vec<_>>(),
        ["Value0"]
    );
    assert_eq!(e.frozen_values(), [9.0, 1.0]);
    assert_eq!(e.last_values(), [1.0, 1.0]);
    e.clear_override("value0");
    assert_eq!(e.frozen_values(), [1.0, 1.0]);
    assert_eq!(e.elapsed(), 1.0);
    let units = Settings {
        preset: Preset::Metric,
        ..Default::default()
    };
    let mut v = e.tick();
    for x in &mut v {
        *x = units.to_target(*x, "ft");
    }
    e.replace_last_values(&v)?;
    assert_eq!(e.last_values(), [0.6096, 0.6096]);
    let frozen = e.frozen_values();
    assert_eq!(units.to_target(frozen[0], "ft"), 0.18580608);
    assert_eq!(e.last_values(), [0.6096, 0.6096]);
    assert!(e.replace_last_values(&[]).is_err());
    Ok(())
}
#[test]
fn profile_change_preserves_cached_length_and_empty_template_behavior() {
    let mut e = Engine::new(vec![t("a", 2), t("b", 1), t("empty", 0)]);
    e.tick();
    e.set_template("FLECS", "b");
    e.set_override("Value0", 17.0);
    assert_eq!(e.frozen_values(), [17.0, 1.0]);
    e.set_template("FLECS", "empty");
    assert_eq!(e.frozen_values(), [1.0, 1.0]);
    assert!(e.tick().is_empty());
    assert_eq!(e.last_values(), [1.0, 1.0]);
}
#[test]
fn discovery_override_exclusions_and_fallback() -> R {
    let dir = std::env::temp_dir().join(format!("icem-catalog-{}", std::process::id()));
    if dir.exists() {
        std::fs::remove_dir_all(&dir)?;
    }
    let c = load_catalog(&dir);
    assert_eq!(c.templates[0].variables.len(), 34);
    assert_eq!(
        c.templates[0]
            .variables
            .iter()
            .filter(|v| !v.exclude)
            .count(),
        32
    );
    std::fs::create_dir_all(&dir)?;
    std::fs::write(
        dir.join("variable_registry.json"),
        r#"{"variables":[{"name":"p","display":"P","simulation":{"base":1}}]}"#,
    )?;
    std::fs::write(
        dir.join("variable_registry.emulator.json"),
        r#"{"variables":[{"name":"p","display":"P","simulation":{"base":99}}]}"#,
    )?;
    std::fs::write(dir.join("lantable_registry.json"), "broken")?;
    std::fs::write(dir.join("bad.json"), "broken")?;
    std::fs::write(
        dir.join("profile.json"),
        r#"{"name":"test","deviceType":"FLECS","variables":[{"registry":"p"}]}"#,
    )?;
    let c = load_catalog(&dir);
    assert_eq!(c.templates.len(), 1);
    assert_eq!(c.templates[0].variables[0].base, 99.0);
    assert_eq!(c.warnings.len(), 1);
    std::fs::remove_dir_all(dir)?;
    Ok(())
}
