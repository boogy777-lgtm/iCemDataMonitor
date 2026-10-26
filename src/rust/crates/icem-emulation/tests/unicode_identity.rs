//! Observable D22d Unicode contracts; expected identities are BCL-checked on Windows.
#[path = "support/unicode_identity.rs"]
mod fixtures;
use fixtures::{PAIRS, Result, SV, Stage, precedence_files, profile, registry, vector, write};
use icem_config::emulator::EmuSettings;
use icem_emulation::{
    control::process_line,
    lan::{BaseProvider, Registry, VectorData},
    lan_pipeline::LanPipeline,
    runtime::Engine,
    runtime_ports::{ComEmitter, LanEmitter},
    simulation::{self, Template, Variable, format_line, template_from_json},
};
use serde_json::{Value, json};
use std::path::Path;

fn template(name: &str) -> Template {
    Template {
        name: name.into(),
        device_type: "FLECS".into(),
        variables: vec![Variable {
            name: name.into(),
            legacy_name: format!("alias-{name}"),
            base: 11.,
            ..Default::default()
        }],
        ..Default::default()
    }
}
#[test]
fn registry_name_and_display_references_produce_exact_com_lines() -> Result {
    assert_eq!(PAIRS.len(), 13, "extend the shared BCL fixture table");
    for (name, query, equal) in PAIRS {
        for display_reference in [false, true] {
            let source = if display_reference {
                registry("independent-name", name, 42.5)
            } else {
                registry(name, "independent-display", 42.5)
            };
            let parsed = template_from_json(&profile("Unicode profile", query), &source)?;
            let values = parsed
                .variables
                .iter()
                .map(|v| simulation::compute(v, 1.))
                .collect::<Vec<_>>();
            assert_eq!(
                format_line(&values, &parsed),
                if equal { "42.50,7" } else { "7" },
                "{name:?} -> {query:?}, display={display_reference}"
            );
            assert_eq!(parsed.variables.len(), if equal { 2 } else { 1 });
        }
    }
    Ok(())
}
#[test]
fn profile_lookup_aliases_and_override_dictionary_follow_ordinal_identity() {
    for (name, query, equal) in PAIRS {
        let mut engine = simulation::Engine::new(vec![template("sentinel"), template(name)]);
        engine.set_template("flecs", query);
        assert_eq!(
            engine.current().map(|t| t.name.as_str()),
            Some(if equal { name } else { "sentinel" }),
            "profile {name:?}/{query:?}"
        );
        engine.set_template("flecs", name);
        assert_eq!(
            engine.find_variable(query).is_some(),
            equal,
            "name {name:?}/{query:?}"
        );
        assert_eq!(
            engine.find_variable(&format!("ALIAS-{query}")).is_some(),
            equal,
            "alias {name:?}/{query:?}"
        );
        engine.set_override(query, 13.);
        engine.set_override(name, 19.);
        let overrides = engine.snapshot_overrides();
        assert_eq!(overrides.len(), if equal { 1 } else { 2 });
        assert_eq!(
            overrides.get(query),
            Some(&if equal { 19. } else { 13. }),
            "dictionary retains first spelling"
        );
        assert_eq!(engine.tick(), vec![19.]);
        assert_eq!(engine.frozen_values(), vec![19.]);
        assert!(engine.clear_override(query));
        assert_eq!(engine.tick(), vec![if equal { 11. } else { 19. }]);
    }
}
fn payload(values: &[f32]) -> [u8; 124] {
    let mut result = [0; 124];
    for (slot, value) in result.as_chunks_mut::<4>().0.iter_mut().zip(values) {
        slot.copy_from_slice(&value.to_be_bytes());
    }
    result
}
fn base(directory: &Path) -> Result<BaseProvider> {
    let registry = Registry::load(
        &directory.join("lantable_registry.json"),
        Some(&directory.join("firmware_registry.json")),
    )?;
    BaseProvider::load(registry, &directory.join("variable_registry.json"), None)
}
#[test]
fn lan_primary_and_firmware_precedence_preserves_every_nonzero_payload_byte() -> Result {
    let stage = Stage::new()?;
    let names = precedence_files(&stage.0)?;
    assert_eq!(names.len(), 7);
    let firmware = stage.0.join("firmware_variables.json");
    let held = stage.0.join("firmware-held.json");
    std::fs::rename(&firmware, &held)?;
    assert_eq!(
        base(&stage.0)?.superframe(1, 0.)[0].payload,
        payload(&[11., 11., 33., 33., 55., 11., 11.])
    );
    std::fs::rename(&held, &firmware)?;
    let provider = base(&stage.0)?;
    let frames = provider.superframe(1, 0.);
    let frame = frames
        .iter()
        .find(|v| v.sv_number == SV)
        .ok_or("missing fixture vector")?;
    assert_eq!(frame.payload, payload(&[77., 77., 33., 33., 99., 11., 11.]));
    assert_eq!(frame.name, "identity-vector");
    Ok(())
}
#[test]
fn lan_lookup_validates_unicode_without_changing_exact_override_keys() -> Result {
    let stage = Stage::new()?;
    for (index, (name, query, equal)) in PAIRS.into_iter().enumerate() {
        let directory = stage.directory(&index.to_string())?;
        write(
            &directory.join("variable_registry.json"),
            &registry(name, "display", 11.),
        )?;
        write(
            &directory.join("lantable_registry.json"),
            &vector(&[name, query]),
        )?;
        write(
            &directory.join("firmware_registry.json"),
            &json!({"serviceVectors":[]}),
        )?;
        let mut pipeline = LanPipeline::new(base(&directory)?);
        pipeline.network = None;
        assert_eq!(
            pipeline.base.superframe(1, 0.)[0].payload,
            payload(&[11., if equal { 11. } else { 0. }]),
            "{name:?}/{query:?}"
        );
        assert!(pipeline.can_transmit(SV, query).is_ok());
        assert_eq!(
            pipeline.resolve_signal(SV, query)?.name,
            if equal { name } else { query },
            "first matching signal must use ordinal identity"
        );
        pipeline.set_override(SV, name, 17.);
        pipeline.set_override(SV, query, 29.);
        let expected_count = if name == query { 1 } else { 2 };
        assert_eq!(pipeline.snapshot_overrides().len(), expected_count);
        assert_eq!(
            pipeline.native_superframe(1, 0.)[0].payload,
            payload(&[17., 29.])
        );
        pipeline.clear_override(SV, query);
        assert_eq!(
            pipeline.native_superframe(1, 0.)[0].payload,
            payload(&[17., if equal { 11. } else { 0. }])
        );
    }
    Ok(())
}
#[derive(Default)]
struct Com {
    endpoint: Option<String>,
    lines: Vec<String>,
}
impl ComEmitter for Com {
    fn open(&mut self, name: &str) -> std::result::Result<(), String> {
        self.endpoint = Some(name.into());
        Ok(())
    }
    fn close(&mut self) {
        self.endpoint = None;
    }
    fn is_open(&self) -> bool {
        self.endpoint.is_some()
    }
    fn port_name(&self) -> Option<&str> {
        self.endpoint.as_deref()
    }
    fn lines_sent(&self) -> u64 {
        self.lines.len() as u64
    }
    fn send_line(&mut self, line: &str) -> std::result::Result<(), String> {
        if self.endpoint.is_none() {
            return Err("test COM endpoint closed".into());
        }
        self.lines.push(line.into());
        Ok(())
    }
    fn ports(&self) -> Vec<String> {
        vec!["COM12".into()]
    }
}
#[derive(Default)]
struct Lan {
    endpoint: Option<String>,
    frames: Vec<VectorData>,
}
impl LanEmitter for Lan {
    fn open(&mut self, name: &str) -> std::result::Result<(), String> {
        self.endpoint = Some(name.into());
        Ok(())
    }
    fn close(&mut self) {
        self.endpoint = None;
    }
    fn is_open(&self) -> bool {
        self.endpoint.is_some()
    }
    fn adapter_name(&self) -> Option<&str> {
        self.endpoint.as_deref()
    }
    fn frames_sent(&self) -> u64 {
        self.frames.len() as u64
    }
    fn send_superframe(
        &mut self,
        frames: &[VectorData],
        _: u32,
    ) -> std::result::Result<(), String> {
        if self.endpoint.is_none() {
            return Err("test LAN endpoint closed".into());
        }
        self.frames.extend_from_slice(frames);
        Ok(())
    }
    fn adapters(&self) -> Vec<String> {
        vec!["identity-adapter".into()]
    }
    fn npcap_available(&self) -> bool {
        true
    }
}
fn command(engine: &mut Engine<Com, Lan>, line: &str, success: bool) -> Result<Value> {
    let reply = process_line(line, engine);
    let value: Value = serde_json::from_str(&reply.json)?;
    assert_eq!(value["ok"], success, "{line}: {value}");
    Ok(value)
}
#[test]
fn runtime_profiles_alias_set_update_get_clear_report_canonical_override_state() -> Result {
    let stage = Stage::new()?;
    for (index, (name, query, equal)) in PAIRS.into_iter().enumerate() {
        let directory = stage.directory(&index.to_string())?;
        write(
            &directory.join("variable_registry.json"),
            &registry(name, "display", 11.),
        )?;
        write(&directory.join("lantable_registry.json"), &vector(&[name]))?;
        write(
            &directory.join("firmware_registry.json"),
            &json!({"serviceVectors":[]}),
        )?;
        let mut engine = Engine::new(
            EmuSettings::default(),
            vec![template(name)],
            base(&directory)?,
            Com::default(),
            Lan::default(),
        );
        command(&mut engine, &format!("profile {query}"), equal)?;
        command(&mut engine, &format!("get com.{query}"), equal)?;
        command(&mut engine, &format!("set com.ALIAS-{query} 17"), equal)?;
        command(&mut engine, &format!("set lan.{query} 17"), equal)?;
        if !equal {
            continue;
        }
        command(&mut engine, &format!("set com.{query} 19"), true)?;
        command(&mut engine, &format!("set lan.{query} 19"), true)?;
        for channel in ["com", "lan"] {
            let reply = command(&mut engine, &format!("get {channel}.{name}"), true)?;
            assert_eq!(reply["data"]["name"], name);
            assert_eq!(reply["data"]["value"], 19.);
            assert_eq!(reply["data"]["overridden"], true);
        }
        let vars = command(&mut engine, "vars com", true)?;
        assert_eq!(vars["data"]["com"]["variables"][0]["overridden"], true);
        let status = command(&mut engine, "status", true)?;
        let overrides = status["data"]["overrides"]
            .as_array()
            .ok_or("missing overrides")?;
        assert_eq!(overrides.len(), 2);
        assert!(
            overrides
                .iter()
                .all(|v| v["name"] == name && v["value"] == 19.)
        );
        command(&mut engine, "setport COM12", true)?;
        command(&mut engine, "start com", true)?;
        command(&mut engine, "setemulate com 1", true)?;
        command(&mut engine, "setadapter identity-adapter", true)?;
        command(&mut engine, "start lan", true)?;
        engine.tick();
        assert_eq!(
            engine.com_emitter().lines.last().map(String::as_str),
            Some("19.00")
        );
        assert_eq!(
            engine
                .lan_emitter()
                .frames
                .iter()
                .find(|f| f.sv_number == SV)
                .ok_or("no emitted LAN vector")?
                .payload,
            payload(&[19.])
        );
        command(&mut engine, &format!("clear com.ALIAS-{query}"), true)?;
        command(&mut engine, &format!("clear lan.{query}"), true)?;
        engine.tick();
        for channel in ["com", "lan"] {
            let reply = command(&mut engine, &format!("get {channel}.{query}"), true)?;
            assert_eq!(reply["data"]["value"], 11.);
            assert_eq!(reply["data"]["overridden"], false);
        }
        assert_eq!(
            command(&mut engine, "status", true)?["data"]["overrides"],
            json!([])
        );
    }
    Ok(())
}

#[cfg(windows)]
fn oracle(script: &str, arguments: &[(&str, &Path)]) -> Result<Value> {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let mut command = std::process::Command::new("pwsh");
    command
        .args(["-NoProfile", "-File"])
        .arg(root.join("rust/tests/support").join(script));
    command.arg("-RepoRoot").arg(&root);
    for (key, value) in arguments {
        command.arg(key).arg(value);
    }
    let output = command.output()?;
    assert!(
        output.status.success(),
        "{script}: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    Ok(serde_json::from_slice(&output.stdout)?)
}
#[test]
#[cfg(windows)]
fn original_simulation_engine_and_bcl_recheck_all_pair_expectations() -> Result {
    let stage = Stage::new()?;
    let pairs = stage.0.join("pairs.json");
    write(
        &pairs,
        &json!(
            PAIRS
                .iter()
                .map(|(name, query, _)| json!({"name":name,"query":query}))
                .collect::<Vec<_>>()
        ),
    )?;
    let bcl = oracle("UnicodeIdentityOracle.ps1", &[("-PairsPath", &pairs)])?;
    let rows = bcl["rows"]
        .as_array()
        .ok_or("missing original simulation rows")?;
    assert_eq!(rows.len(), PAIRS.len());
    for (row, (name, query, equal)) in rows.iter().zip(PAIRS) {
        assert_eq!(row["name"], name);
        assert_eq!(row["query"], query);
        assert_eq!(
            row["equal"], equal,
            "BCL expectation drift for {name:?}/{query:?}"
        );
        let mut engine = simulation::Engine::new(vec![template("sentinel"), template(name)]);
        engine.set_template("flecs", query);
        assert_eq!(
            engine.current().map(|v| v.name.as_str()),
            row["selected"].as_str()
        );
        engine.set_template("flecs", name);
        assert_eq!(json!(engine.find_variable(query).is_some()), row["found"]);
        assert_eq!(
            json!(engine.find_variable(&format!("ALIAS-{query}")).is_some()),
            row["alias"]
        );
        engine.set_override(query, 13.);
        engine.set_override(name, 19.);
        let overrides = engine.snapshot_overrides();
        assert_eq!(json!(overrides.len()), row["count"]);
        assert_eq!(overrides.get(query).copied(), row["updated"].as_f64());
        let mut expected_keys = row["keys"]
            .as_array()
            .ok_or("missing dictionary keys")?
            .iter()
            .map(|v| v.as_str().ok_or("invalid dictionary key"))
            .collect::<std::result::Result<Vec<_>, _>>()?;
        expected_keys.sort_unstable();
        assert_eq!(
            overrides.keys().map(String::as_str).collect::<Vec<_>>(),
            expected_keys
        );
        assert_eq!(
            engine.tick(),
            serde_json::from_value::<Vec<f64>>(row["tick"].clone())?
        );
        assert_eq!(json!(engine.clear_override(query)), row["cleared"]);
        assert_eq!(
            engine.tick(),
            serde_json::from_value::<Vec<f64>>(row["after"].clone())?
        );
    }
    eprintln!("BCL/original SimulationEngine runtime: {}", bcl["runtime"]);
    Ok(())
}
#[test]
#[cfg(windows)]
fn original_com_catalog_resolves_unicode_name_and_display_references() -> Result {
    let stage = Stage::new()?;
    let input = stage.directory("input")?;
    for (index, (name, query, _)) in PAIRS.into_iter().enumerate() {
        for display in [false, true] {
            let directory = input.join(format!("{index:02}-{display}"));
            std::fs::create_dir_all(&directory)?;
            let source = if display {
                registry("independent-name", name, 42.5)
            } else {
                registry(name, "independent-display", 42.5)
            };
            write(&directory.join("variable_registry.json"), &source)?;
            write(
                &directory.join("profile.json"),
                &profile("Unicode profile", query),
            )?;
        }
    }
    let output = stage.directory("output")?;
    let catalog = oracle(
        "ComCatalogOracle.ps1",
        &[("-InputDir", &input), ("-OutputDir", &output)],
    )?;
    let cases = catalog["cases"]
        .as_array()
        .ok_or("missing original catalog cases")?;
    assert_eq!(cases.len(), PAIRS.len() * 2);
    for case in cases {
        let label = case["label"].as_str().ok_or("missing catalog label")?;
        let actual = simulation::load_catalog(&input.join(label));
        let expected = case["templates"]
            .as_array()
            .ok_or("missing original templates")?;
        assert_eq!(actual.templates.len(), 1);
        assert_eq!(expected.len(), 1);
        let template = &actual.templates[0];
        let values = template
            .variables
            .iter()
            .map(|v| simulation::compute(v, 1.))
            .collect::<Vec<_>>();
        assert_eq!(
            json!(format_line(&values, template)),
            expected[0]["line"],
            "{label}"
        );
        assert_eq!(
            template.variables.len(),
            expected[0]["template"]["Variables"]
                .as_array()
                .ok_or("missing original variables")?
                .len()
        );
    }
    Ok(())
}
#[test]
#[cfg(windows)]
fn original_lan_loader_matches_unicode_lookup_and_overwrite_precedence_bytes() -> Result {
    let stage = Stage::new()?;
    let input = stage.directory("input")?;
    let directory = input.join("precedence");
    std::fs::create_dir_all(&directory)?;
    precedence_files(&directory)?;
    let primary = input.join("primary");
    std::fs::create_dir_all(&primary)?;
    precedence_files(&primary)?;
    std::fs::remove_file(primary.join("firmware_variables.json"))?;
    for (index, (name, query, _)) in PAIRS.into_iter().enumerate() {
        let directory = input.join(format!("{index:02}"));
        std::fs::create_dir_all(&directory)?;
        write(
            &directory.join("variable_registry.json"),
            &registry(name, "display", 11.),
        )?;
        write(
            &directory.join("lantable_registry.json"),
            &vector(&[name, query]),
        )?;
        write(
            &directory.join("firmware_registry.json"),
            &json!({"serviceVectors":[]}),
        )?;
    }
    let lan = oracle("LanPayloadOracle.ps1", &[("-LoadingFixtures", &input)])?;
    let cases = lan["cases"]
        .as_array()
        .ok_or("missing original LAN cases")?;
    assert_eq!(cases.len(), PAIRS.len() + 2);
    for case in cases {
        let label = case["label"].as_str().ok_or("missing LAN label")?;
        let frames = base(&input.join(label))?.superframe(1, 0.);
        let expected = case["frames"].as_array().ok_or("missing original frames")?;
        assert_eq!(frames.len(), expected.len());
        for (frame, row) in frames.iter().zip(expected) {
            assert_eq!(json!(frame.sv_number), row["sv"]);
            assert_eq!(json!(frame.name), row["name"]);
            let hex = frame
                .payload
                .iter()
                .map(|byte| format!("{byte:02x}"))
                .collect::<String>();
            assert_eq!(json!(hex), row["payload"], "{label}");
        }
    }
    Ok(())
}
