use super::*;
#[test]
fn defaults_and_every_field_round_trip() -> Result<(), ConfigError> {
    let defaults = IniOptions::default();
    assert!(defaults.validate().is_empty());
    assert_eq!(IniOptions::parse(&defaults.render())?, defaults);
    assert_eq!(KEYS.len(), 16);
    let custom = IniOptions {
        modbus_on: 0,
        web_on: 0,
        opcda_on: 1,
        lansniffer_on: 0,
        ui_set_as_default: 0,
        web_remote_on: 1,
        modbus_port: 1502,
        modbus_tick_ms: 200,
        web_port: 18090,
        mmf_name: "Local\\Тест".into(),
        mmf_path: "D:\\Стенд\\Data.dat".into(),
        wits_base: 700,
        wits_len: 2,
        lan_ext_base: 800,
        lan_ext_len: 2,
        total: 65535,
    };
    assert_eq!(IniOptions::parse(&custom.render())?, custom);
    Ok(())
}
#[test]
fn optional_and_retired_keys_case_comments_and_cr_lines() -> Result<(), ConfigError> {
    let defaults = IniOptions::default();
    let mut text = defaults
        .render()
        .lines()
        .filter(|line| {
            !line.starts_with("ui_set_as_default=")
                && !line.starts_with("web_on=")
                && !line.starts_with("web_remote_on=")
        })
        .collect::<Vec<_>>()
        .join("\r");
    text = text.replace(
        "modbus_on=1",
        "MODBUS_ON=+1\r; comment\r# comment\remule_on=nonnumeric\rcom_port=garbage",
    );
    text.push_str(
        "\rcom_base=nonsense\rcom_len=-1\rlan_base=-1\rlan_len=-1\rraw_base=-1\rraw_len=-1\r",
    );
    assert_eq!(IniOptions::parse(&text)?, defaults);
    Ok(())
}
#[test]
fn collects_parse_errors_without_contract_default_fallback() {
    let text = IniOptions::default()
        .render()
        .replace("modbus_on=1", "modbus_on=bad\nmodbus_on=bad\nunknown=2")
        .replace("web_port=8090", "web_port=2147483648")
        .replace("total=10001", "broken");
    let error = IniOptions::parse(&text).err();
    assert!(error.is_some());
    if let Some(error) = error {
        for expected in [
            "duplicate",
            "unknown key",
            "malformed",
            "missing key 'total'",
            "modbus_on: not an integer",
            "web_port: not an integer",
        ] {
            assert!(
                error.violations.iter().any(|line| line.contains(expected)),
                "{expected}: {error}"
            );
        }
    }
}
#[test]
fn validates_ports_flags_cadence_fit_overlap_and_dynamic_total() {
    let config = IniOptions {
        modbus_on: 2,
        web_remote_on: -1,
        modbus_port: 0,
        web_port: 65536,
        modbus_tick_ms: 199,
        mmf_name: String::new(),
        ..IniOptions::default()
    };
    assert_eq!(config.validate().len(), 6);
    let config = IniOptions {
        total: 650,
        lan_ext_base: 648,
        ..IniOptions::default()
    };
    assert!(config.validate().is_empty());
    let config = IniOptions {
        total: 650,
        ..IniOptions::default()
    };
    assert!(
        config
            .validate()
            .iter()
            .any(|error| error.contains("out of range"))
    );
    let config = IniOptions {
        wits_len: 100,
        lan_ext_len: 0,
        ..IniOptions::default()
    };
    assert!(
        config
            .validate()
            .iter()
            .any(|error| error.contains("overlap"))
    ); // actual zero-length C# predicate
}
#[test]
fn required_keys_are_checked_from_same_schema() {
    let default = IniOptions::default().render();
    for key in KEYS.iter().filter(|key| !key.optional) {
        let text = default
            .lines()
            .filter(|line| !line.starts_with(&format!("{}=", key.name)))
            .collect::<Vec<_>>()
            .join("\n");
        assert!(IniOptions::parse(&text).is_err(), "missing {}", key.name);
    }
    for (line, expected) in [
        ("x=1", "outside any section"),
        ("[bad]\nx=1", "unknown section"),
        ("[station\nx=1", "malformed section"),
    ] {
        let error = IniOptions::parse(line).err();
        assert!(error.is_some_and(|error| {
            error
                .violations
                .iter()
                .any(|error| error.contains(expected))
        }));
    }
}
#[test]
fn utf8_utf16_utf32_bom_load_compatibility() -> Result<(), ConfigError> {
    let text = IniOptions {
        mmf_name: "Global\\Шина".into(),
        ..IniOptions::default()
    }
    .render();
    let mut encodings = vec![
        text.as_bytes().to_vec(),
        [vec![239, 187, 191], text.as_bytes().to_vec()].concat(),
    ];
    for little in [true, false] {
        let mut encoded = if little {
            vec![255, 254]
        } else {
            vec![254, 255]
        };
        for word in text.encode_utf16() {
            encoded.extend_from_slice(&if little {
                word.to_le_bytes()
            } else {
                word.to_be_bytes()
            });
        }
        encodings.push(encoded);
        let mut encoded = if little {
            vec![255, 254, 0, 0]
        } else {
            vec![0, 0, 254, 255]
        };
        for character in text.chars() {
            let word = u32::from(character);
            encoded.extend_from_slice(&if little {
                word.to_le_bytes()
            } else {
                word.to_be_bytes()
            });
        }
        encodings.push(encoded);
    }
    for bytes in encodings {
        assert_eq!(
            IniOptions::parse(&decode_text(&bytes))?.mmf_name,
            "Global\\Шина"
        );
    }
    Ok(())
}
