#[path = "support/com_catalog.rs"]
mod fixtures;
use fixtures::Result;
use icem_emulation::simulation::{load_catalog, load_template};
#[test]
fn malformed_registry_recovery_and_template_isolation_are_explicit() -> Result {
    let stage = fixtures::Stage::new()?;
    for case in fixtures::cases() {
        let path = stage.write(&case)?;
        let catalog = load_catalog(&path);
        assert_eq!(
            fixtures::warning_counts(&catalog.warnings),
            case.warnings,
            "{}: {:?}",
            case.label,
            catalog.warnings
        );
        assert_eq!(
            catalog.templates.len(),
            case.expected.len(),
            "{}",
            case.label
        );
        for (name, expected) in &case.expected {
            let actual = catalog
                .templates
                .iter()
                .find(|t| t.name == *name)
                .ok_or_else(|| format!("{} missing {name}", case.label))?;
            if *name == "flecs 4.53" {
                assert_eq!(actual.variables.len(), 34);
                continue;
            }
            assert_eq!(
                actual
                    .variables
                    .iter()
                    .map(|v| (v.name.as_str(), v.base))
                    .collect::<Vec<_>>(),
                *expected,
                "{} {name}",
                case.label
            );
        }
    }
    Ok(())
}
#[test]
fn explicit_file_load_does_not_recover_a_malformed_registry() -> Result {
    let stage = fixtures::Stage::new()?;
    for case in fixtures::cases().into_iter().filter(|c| c.warnings[0] > 0) {
        let path = stage.write(&case)?;
        assert!(
            load_template(&path.join("inline.json"), None).is_err(),
            "{}",
            case.label
        );
    }
    Ok(())
}

#[test]
fn null_record_position_does_not_change_registry_recovery() -> Result {
    let stage = fixtures::Stage::new()?;
    let mut previous = None;
    for case in fixtures::cases().into_iter().filter(|c| {
        matches!(
            c.label.as_str(),
            "null-record-before-match" | "null-record-after-match"
        )
    }) {
        let catalog = load_catalog(&stage.write(&case)?);
        assert_eq!(fixtures::warning_counts(&catalog.warnings), [1, 0, 0, 2, 0]);
        if let Some(expected) = previous {
            assert_eq!(catalog.templates, expected);
        }
        previous = Some(catalog.templates);
    }
    Ok(())
}
