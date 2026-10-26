#[cfg(windows)]
use std::fs;
use std::process::Command;
#[test]
fn missing_and_blank_adapter_exit_before_startup() {
    for args in [
        vec!["--landiag"],
        vec!["  --LaNdIaG ", " \t", "--config", "should-not-exist.xml"],
    ] {
        let output = Command::new(env!("CARGO_BIN_EXE_icem-emulator-engine-dev"))
            .args(args)
            .output()
            .unwrap_or_else(|e| panic!("{e:?}"));
        assert_eq!(output.status.code(), Some(2));
        assert!(String::from_utf8_lossy(&output.stdout).contains("requires an adapter substring"));
    }
}
#[cfg(windows)]
#[test]
fn unmatched_adapter_has_no_settings_effects() {
    let root = std::env::temp_dir().join(format!("icem-landiag-{}", std::process::id()));
    fs::create_dir_all(&root).unwrap_or_else(|e| panic!("{e:?}"));
    let output = Command::new(env!("CARGO_BIN_EXE_icem-emulator-engine-dev"))
        .args([
            "--landiag",
            "NO_MATCH_2e69c46f-1cb6-4f97-9dad-233aa15219aa",
            "--config",
            "ignored.xml",
        ])
        .env("LOCALAPPDATA", &root)
        .env("PROGRAMDATA", &root)
        .current_dir(&root)
        .output()
        .unwrap_or_else(|e| panic!("{e:?}"));
    assert_eq!(output.status.code(), Some(1));
    assert_eq!(
        fs::read_dir(&root)
            .unwrap_or_else(|e| panic!("{e:?}"))
            .count(),
        0
    );
    fs::remove_dir_all(root).unwrap_or_else(|e| panic!("{e:?}"));
}
