#![cfg(windows)]
use icem_platform_windows::sniffer_catalog::Catalog;
use icem_protocols::ace::host::{Kind, decode};
use serde_json::{Value, json};
use std::{
    io::Read,
    path::PathBuf,
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};
struct Oracle {
    child: Child,
    directory: PathBuf,
}
impl Drop for Oracle {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = std::fs::remove_dir_all(&self.directory);
    }
}
fn text<'a>(value: &'a Value, key: &str) -> Result<&'a str, Box<dyn std::error::Error>> {
    value[key]
        .as_str()
        .ok_or_else(|| format!("missing {key}").into())
}
fn bytes(hex: &str) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let (pairs, rest) = hex.as_bytes().as_chunks::<2>();
    if !rest.is_empty() {
        return Err("odd oracle hex".into());
    }
    pairs
        .iter()
        .map(|p| Ok(u8::from_str_radix(std::str::from_utf8(p)?, 16)?))
        .collect()
}
#[test]
fn original_host_decoder_matches_catalog_types_spans_and_pinned_wire()
-> Result<(), Box<dyn std::error::Error>> {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let directory = std::env::temp_dir().join(format!("icem-host-decoder-{}", std::process::id()));
    std::fs::create_dir(&directory)?;
    let output = directory.join("oracle.json");
    let errors = directory.join("oracle-errors.txt");
    let child = Command::new("pwsh")
        .args(["-NoProfile", "-File"])
        .arg(root.join("rust/tests/support/HostSnifferOracle.ps1"))
        .arg("-RepoRoot")
        .arg(&root)
        .stdout(Stdio::from(std::fs::File::create(&output)?))
        .stderr(Stdio::from(std::fs::File::create(&errors)?))
        .spawn()?;
    let mut oracle = Oracle { child, directory };
    let deadline = Instant::now() + Duration::from_secs(120);
    loop {
        if let Some(status) = oracle.child.try_wait()? {
            if !status.success() {
                return Err(
                    format!("Host C# decoder: {}", std::fs::read_to_string(&errors)?).into(),
                );
            }
            break;
        }
        if Instant::now() >= deadline {
            return Err("Host oracle deadline".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    const LIMIT: u64 = 16 * 1024 * 1024;
    let mut raw = String::new();
    std::fs::File::open(output)?
        .take(LIMIT + 1)
        .read_to_string(&mut raw)?;
    if raw.len() as u64 > LIMIT {
        return Err("Host oracle output budget".into());
    }
    let result: Value = serde_json::from_str(&raw)?;
    assert_eq!(result["vectors"], 194);
    assert!(result["pcap_vectors"].as_u64().is_some_and(|v| v > 0));
    let cases = result["cases"].as_array().ok_or("missing cases")?;
    let mut checks = 0;
    let mut types = std::collections::BTreeSet::new();
    let mut catalog =
        Catalog::load(&root.join("iCemDataMonitor.Plugins.Icem/templates/lantable_registry.json"))?;
    for case in cases {
        let frame = bytes(text(case, "frame")?)?;
        let signals = case["signals"].as_array().ok_or("signals")?;
        let active = matches!(
            case["origin"].as_str(),
            Some("catalog-pattern" | "pinned-pcap")
        );
        let number = u32::try_from(case["sv"].as_u64().ok_or("sv")?)?;
        if active {
            let definition = catalog
                .definition(number)
                .ok_or("Host definition missing")?;
            assert_eq!(definition.name, text(case, "name")?);
            assert_eq!(definition.equipment_type, text(case, "equipment_type")?);
            assert_eq!(definition.signals.len(), signals.len());
            for (actual, expected) in definition.signals.iter().zip(signals) {
                assert_eq!(actual.name, text(expected, "name")?);
                assert_eq!(actual.uom, text(expected, "uom")?);
                assert_eq!(actual.value_type, text(expected, "value_type")?);
                assert_eq!(
                    i64::from(actual.byte_offset),
                    expected["byte_offset"].as_i64().ok_or("offset")?
                );
                assert_eq!(
                    i64::from(actual.size),
                    expected["size"].as_i64().ok_or("size")?
                );
                assert_eq!(
                    actual.das_factor.to_bits(),
                    u64::from_str_radix(text(expected, "das_factor_bits")?, 16)?,
                    "factor SV={number:08X} signal={}",
                    actual.name
                );
            }
            catalog.observe(&frame, Duration::from_millis(checks as u64));
        }
        let factors = signals
            .iter()
            .map(|s| {
                Ok(f64::from_bits(u64::from_str_radix(
                    text(s, "das_factor_bits")?,
                    16,
                )?))
            })
            .collect::<Result<Vec<_>, Box<dyn std::error::Error>>>()?;
        let actual: Result<Vec<_>, _> = signals
            .iter()
            .zip(factors)
            .map(|(s, factor)| {
                let offset = s["byte_offset"]
                    .as_i64()
                    .and_then(|n| i32::try_from(n).ok())
                    .unwrap_or(i32::MAX);
                let size = s["size"]
                    .as_i64()
                    .and_then(|n| i32::try_from(n).ok())
                    .unwrap_or(i32::MAX);
                let kind = s["value_type"].as_str().unwrap_or("");
                types.insert(kind.to_owned());
                decode(&frame, offset, size, Kind::parse(kind), factor)
            })
            .collect();
        if !case["error"].is_null() {
            assert!(actual.is_err(), "expected vector failure {case}");
            continue;
        }
        let actual = actual.map_err(|e| format!("unexpected decode error {e:?}"))?;
        let expected = case["decoded"].as_array().ok_or("decoded")?;
        if active {
            let vectors = catalog.vectors(Duration::from_millis(checks as u64));
            let vector = vectors
                .iter()
                .find(|v| v.number == number)
                .ok_or("observed vector missing")?;
            assert!(!vector.unknown);
            let last: std::collections::BTreeMap<_, _> = expected
                .iter()
                .map(|v| (v["name"].as_str().unwrap_or(""), v))
                .collect();
            let cached = catalog.signals(vector.id);
            assert_eq!(cached.len(), last.len());
            for signal in cached {
                let expected = last
                    .get(signal.name.as_str())
                    .ok_or("cached signal missing")?;
                assert_eq!(signal.uom, text(expected, "uom")?);
                assert_eq!(signal.decoded.text.as_deref(), expected["text"].as_str());
                assert_eq!(signal.decoded.sentinel, expected["sentinel"] == true);
                match (signal.decoded.value, expected["bits"].as_str()) {
                    (None, None) => {}
                    (Some(a), Some(b)) => {
                        let b = f64::from_bits(u64::from_str_radix(b, 16)?);
                        if b.is_nan() {
                            assert!(a.is_nan());
                        } else {
                            assert_eq!(
                                a.to_bits(),
                                b.to_bits(),
                                "SV={number:08X} origin={} signal={}",
                                case["origin"],
                                expected["name"]
                            );
                        }
                    }
                    _ => return Err("cached null/numeric mismatch".into()),
                }
            }
        }

        assert_eq!(actual.len(), expected.len());
        for (actual, expected) in actual.iter().zip(expected) {
            assert_eq!(actual.text.as_deref(), expected["text"].as_str());
            assert_eq!(
                actual.sentinel,
                expected["sentinel"].as_bool().ok_or("sentinel")?
            );
            match (actual.value, expected["bits"].as_str()) {
                (None, None) => {}
                (Some(a), Some(b)) => {
                    let b = f64::from_bits(u64::from_str_radix(b, 16)?);
                    if b.is_nan() {
                        assert!(a.is_nan());
                    } else {
                        assert_eq!(
                            a.to_bits(),
                            b.to_bits(),
                            "SV={number:08X} origin={} signal={}",
                            case["origin"],
                            expected["name"]
                        );
                    }
                }
                _ => return Err("Host null/numeric mismatch".into()),
            }
            checks += 1;
        }
    }
    assert!(
        types.is_superset(
            &[
                "FF", "FD", "UL", "SL", "SI", "UI", "US", "SS", "SC", "HX", "UC"
            ]
            .map(str::to_owned)
            .into()
        )
    );
    assert_eq!(catalog.vectors(Duration::ZERO).len(), 194);
    let target = root.join("rust/target");
    std::fs::create_dir_all(&target)?;
    std::fs::write(
        target.join("host-sniffer-differential-report.json"),
        serde_json::to_vec_pretty(
            &json!({"pass":true,"runtime":result["runtime"],"vectors":194,"cases":cases.len(),"signalChecks":checks,"pcapVectors":result["pcap_vectors"]}),
        )?,
    )?;
    Ok(())
}
