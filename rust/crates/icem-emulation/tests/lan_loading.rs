//! Production file recovery; strict parser and encoding/Unicode policies are separate.
#[path = "support/lan_loading.rs"]
mod fixtures;
use fixtures::{A, B, Result};
use icem_emulation::lan::{BaseProvider, Registry};
#[test]
fn file_fault_retains_completed_prefix_and_attempts_firmware() -> Result {
    let stage = fixtures::Stage::new()?;
    for case in fixtures::cases() {
        let path = stage.write(&case)?;
        let registry = Registry::load(
            &path.join("lantable_registry.json"),
            Some(&path.join("firmware_registry.json")),
        )?;
        let expected_diagnostics = usize::from(
            (case.label.starts_with("registry-") && case.label != "registry-duplicate-first-wins")
                || case.label == "firmware-registry-prefix",
        );
        assert_eq!(
            registry.diagnostics().len(),
            expected_diagnostics,
            "{}",
            case.label
        );
        assert_eq!(
            registry.all().len(),
            case.expected_vectors.len(),
            "{}",
            case.label
        );
        for (sv, name) in &case.expected_vectors {
            assert_eq!(
                registry.get(*sv).map(|v| v.name.as_str()),
                Some(*name),
                "{}",
                case.label
            );
        }
        let provider = BaseProvider::load(registry, &path.join("variable_registry.json"), None)?;
        let frames = provider.superframe(1, 0.0);
        let a = frames
            .iter()
            .find(|v| v.sv_number == A)
            .ok_or("missing active A")?;
        let mut expected = [0; 124];
        for (chunk, value) in expected[..12]
            .as_chunks_mut::<4>()
            .0
            .iter_mut()
            .zip(case.expected_a)
        {
            *chunk = value.to_be_bytes();
        }
        assert_eq!(a.payload, expected, "{}", case.label);
        let b = frames
            .iter()
            .find(|v| v.sv_number == B)
            .ok_or("missing active B")?;
        assert_eq!(
            &b.payload[..4],
            &case.expected_b.to_be_bytes(),
            "{}",
            case.label
        );
        assert_eq!(&b.payload[4..], &[0; 120], "{}", case.label);
    }
    Ok(())
}
#[test]
fn strict_json_parser_still_reports_malformed_data() {
    for case in fixtures::cases()
        .into_iter()
        .filter(|c| c.label.starts_with("registry-") && c.label != "registry-duplicate-first-wins")
    {
        assert!(Registry::from_json(&case.legacy).is_err(), "{}", case.label);
    }
}

#[test]
fn missing_registry_files_have_bounded_owned_diagnostics() -> Result {
    let stage = fixtures::Stage::new()?;
    let registry = Registry::load(
        &stage.root.join("missing-legacy.json"),
        Some(&stage.root.join("missing-firmware.json")),
    )?;
    assert!(registry.all().is_empty());
    assert_eq!(registry.diagnostics().len(), 2);
    assert!(registry.diagnostics()[0].contains("missing-legacy.json"));
    assert!(registry.diagnostics()[1].contains("missing-firmware.json"));
    Ok(())
}
