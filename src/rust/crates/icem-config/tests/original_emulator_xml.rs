use icem_config::emulator::EmuSettings;
use std::{fs, path::PathBuf};
type R = Result<(), Box<dyn std::error::Error>>;
#[test]
fn differential_original_xml_load_save() -> R {
    let pwsh = if cfg!(windows) {
        std::ffi::OsString::from("pwsh")
    } else if let Some(p) = std::env::var_os("ICEM_ORACLE_PWSH") {
        p
    } else {
        return Ok(());
    };
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let dir = std::env::temp_dir().join(format!("icem-xml-oracle-{}", std::process::id()));
    if dir.exists() {
        fs::remove_dir_all(&dir)?;
    }
    let input = dir.join("in");
    let output = dir.join("out");
    fs::create_dir_all(&input)?;
    let cases = [
        "<iCemEmule/>",
        "<other><LanEquipment>1234</LanEquipment><WindowX> +45 </WindowX><ComEnabled> True </ComEnabled></other>",
        "<r><LanEquipment>+1234</LanEquipment><UnitPreset>999</UnitPreset><ComPort>A&amp;B&lt;C&gt;&#xD;</ComPort></r>",
        "<r><LanEquipment>4294967295</LanEquipment><WindowY>-2147483648</WindowY><WindowWidth>2147483648</WindowWidth><LanEnabled>0</LanEnabled></r>",
        "<r><LanEnabled>true</LanEnabled><LanEnabled>false</LanEnabled><ComPort>first</ComPort><ComPort>second</ComPort></r>",
        "<r xmlns='urn:test'><LanEnabled>true</LanEnabled></r>",
        "<r><ComPort>A<child>B</child>C<![CDATA[<D>]]></ComPort></r>",
        "broken",
    ];
    for (i, c) in cases.iter().enumerate() {
        fs::write(input.join(format!("{i}.xml")), c)?;
    }
    let p = std::process::Command::new(pwsh)
        .args(["-NoProfile", "-File"])
        .arg(root.join("rust/tests/support/ConfigComOracle.ps1"))
        .arg("-RepoRoot")
        .arg(&root)
        .arg("-InputDir")
        .arg(&input)
        .arg("-OutputDir")
        .arg(&output)
        .output()?;
    assert!(p.status.success(), "{}", String::from_utf8_lossy(&p.stderr));
    for (i, c) in cases.iter().enumerate() {
        let expected =
            EmuSettings::from_xml(&fs::read_to_string(output.join(format!("{i}.xml")))?)?;
        let loaded = EmuSettings::from_xml(c).unwrap_or_default();
        let actual = EmuSettings::from_xml(&loaded.to_xml())?;
        assert_eq!(actual, expected, "case{i}: {c}");
    }
    fs::remove_dir_all(dir)?;
    Ok(())
}
