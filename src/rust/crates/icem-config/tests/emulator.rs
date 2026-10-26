use icem_config::emulator::{EmuSettings, SettingsPaths};
use std::{fs, path::PathBuf};
type R = Result<(), Box<dyn std::error::Error>>;
fn temp(name: &str) -> Result<PathBuf, std::io::Error> {
    let p = std::env::temp_dir().join(format!("icem-xml-{}-{name}", std::process::id()));
    if p.exists() {
        fs::remove_dir_all(&p)?;
    }
    fs::create_dir_all(&p)?;
    Ok(p)
}
#[test]
fn defaults_schema_and_source_inventory() -> R {
    let d = EmuSettings::default();
    assert!(
        !d.lan_enabled
            && !d.com_enabled
            && !d.com_emule_enabled
            && !d.lan_emule_enabled
            && !d.lan_firmware_sv_enabled
    );
    assert_eq!(
        (d.lan_equipment, d.unit_preset, d.window_x),
        (0x00A8_6F46, 1, -1)
    );
    let source = include_str!("../../../../net/iCemEmule.Core/Core/EmuSettings.cs");
    let names: Vec<_> = source
        .lines()
        .filter(|s| s.contains("{ get; set; }") && !s.contains("static "))
        .map(|s| {
            s.split('{')
                .next()
                .unwrap_or("")
                .split_whitespace()
                .last()
                .unwrap_or("")
        })
        .collect();
    assert_eq!(names, EmuSettings::XML_FIELDS);
    assert_eq!(EmuSettings::from_xml(&d.to_xml())?, d);
    Ok(())
}
#[test]
fn tolerant_xml_hex_first_and_safe_bounds() -> R {
    let s = EmuSettings::from_xml(
        "<anything><LanEnabled> True </LanEnabled><LanEnabled>false</LanEnabled><LanEquipment>1234</LanEquipment><ComPort>A&amp;B<![CDATA[<C>]]></ComPort><WindowY>99999999999999</WindowY><ComEnabled>1</ComEnabled></anything>",
    )?;
    assert!(s.lan_enabled);
    assert!(!s.com_enabled);
    assert_eq!(s.lan_equipment, 0x1234);
    assert_eq!(s.com_port, "A&B<C>");
    assert_eq!(s.window_y, -1);
    assert_eq!(EmuSettings::from_xml(&s.to_xml())?, s);
    assert_eq!(
        EmuSettings::from_xml("<r><LanEquipment>+1234</LanEquipment></r>")?.lan_equipment,
        1234
    );
    assert_eq!(
        EmuSettings::from_xml("<r xmlns='urn:test'><LanEnabled>true</LanEnabled></r>")?,
        EmuSettings::default()
    );
    assert!(EmuSettings::from_xml("<!DOCTYPE r [<!ENTITY x 'boom'>]><r>&x;</r>").is_err());
    assert!(EmuSettings::from_xml(&format!("<r>{}</r>", "a".repeat(1_048_576))).is_err());
    Ok(())
}
#[test]
fn copy_only_migration_override_and_corrupt_defaults() -> R {
    let dir = temp("migration")?;
    let paths = SettingsPaths::station(dir.join("program"), dir.join("local"));
    let legacy = paths.legacy.as_ref().ok_or("legacy")?;
    fs::create_dir_all(legacy.parent().ok_or("parent")?)?;
    fs::write(legacy, "<r><ComPort>COM17</ComPort></r>")?;
    assert_eq!(EmuSettings::load(&paths).settings.com_port, "COM17");
    assert!(legacy.exists());
    fs::write(legacy, "<r><ComPort>COM99</ComPort></r>")?;
    assert_eq!(EmuSettings::load(&paths).settings.com_port, "COM17");
    let explicit = SettingsPaths::explicit(dir.join("explicit.xml"));
    assert_eq!(
        EmuSettings::load(&explicit).settings,
        EmuSettings::default()
    );
    let s = EmuSettings {
        com_port: "COM4 & <port>".into(),
        ..Default::default()
    };
    s.save(&explicit)?;
    assert_eq!(EmuSettings::load(&explicit).settings, s);
    fs::write(&explicit.canonical, "broken")?;
    let r = EmuSettings::load(&explicit);
    assert_eq!(r.settings, EmuSettings::default());
    assert_eq!(r.diagnostics.len(), 1);
    fs::remove_dir_all(dir)?;
    Ok(())
}
#[test]
fn utf16_utf32_operator_files() -> R {
    let dir = temp("encoding")?;
    let p = SettingsPaths::explicit(dir.join("s.xml"));
    let xml = "<r><ComPort>串口☃</ComPort></r>";
    for little in [true, false] {
        let mut b = if little {
            vec![255, 254]
        } else {
            vec![254, 255]
        };
        b.extend(xml.encode_utf16().flat_map(|v| {
            if little {
                v.to_le_bytes()
            } else {
                v.to_be_bytes()
            }
        }));
        fs::write(&p.canonical, b)?;
        assert_eq!(EmuSettings::load(&p).settings.com_port, "串口☃");
        let mut b = if little {
            vec![255, 254, 0, 0]
        } else {
            vec![0, 0, 254, 255]
        };
        b.extend(xml.chars().flat_map(|v| {
            if little {
                u32::from(v).to_le_bytes()
            } else {
                u32::from(v).to_be_bytes()
            }
        }));
        fs::write(&p.canonical, b)?;
        assert_eq!(EmuSettings::load(&p).settings.com_port, "串口☃");
    }
    fs::remove_dir_all(dir)?;
    Ok(())
}
