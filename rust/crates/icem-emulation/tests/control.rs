use icem_emulation::control::{self, ControlResult, Data, EmuControl};
use serde_json::{Value, json};
#[derive(Default)]
struct Fake {
    calls: Vec<Value>,
}
impl Fake {
    fn call(&mut self, method: &str, args: Value) -> ControlResult {
        self.calls.push(json!({"method":method,"args":args}));
        if args
            .as_array()
            .is_some_and(|a| a.iter().any(|v| v.as_str() == Some("fail")))
        {
            ControlResult::fail("backend rejected")
        } else {
            ControlResult::success(json!({"method":method,"args":args}).as_object().cloned())
        }
    }
}
impl EmuControl for Fake {
    fn get_status(&mut self) -> Data {
        self.call("GetStatus", json!([]));
        json!({"state":"ready","proto":99})
            .as_object()
            .cloned()
            .unwrap_or_default()
    }
    fn start_channel(&mut self, c: &str) -> ControlResult {
        self.call("StartChannel", json!([c]))
    }
    fn stop_channel(&mut self, c: &str) -> ControlResult {
        self.call("StopChannel", json!([c]))
    }
    fn set_variable(&mut self, c: Option<&str>, n: &str, v: f64, sv: Option<u32>) -> ControlResult {
        self.call(
            "SetVariable",
            json!([c, n, format!("{:016X}", v.to_bits()), sv]),
        )
    }
    fn clear_variable(&mut self, c: Option<&str>, n: &str, sv: Option<u32>) -> ControlResult {
        self.call("ClearVariable", json!([c, n, sv]))
    }
    fn get_variable(&mut self, c: Option<&str>, n: &str, sv: Option<u32>) -> ControlResult {
        self.call("GetVariable", json!([c, n, sv]))
    }
    fn get_profiles(&mut self) -> ControlResult {
        self.call("GetProfiles", json!([]))
    }
    fn set_profile(&mut self, d: Option<&str>, n: &str) -> ControlResult {
        self.call("SetProfile", json!([d, n]))
    }
    fn get_variables(&mut self, c: Option<&str>) -> ControlResult {
        self.call("GetVariables", json!([c]))
    }
    fn get_ports(&mut self) -> ControlResult {
        self.call("GetPorts", json!([]))
    }
    fn get_adapters(&mut self) -> ControlResult {
        self.call("GetAdapters", json!([]))
    }
    fn set_port(&mut self, p: &str) -> ControlResult {
        self.call("SetPort", json!([p]))
    }
    fn set_adapter(&mut self, a: &str) -> ControlResult {
        self.call("SetAdapter", json!([a]))
    }
    fn set_equipment(&mut self, e: u32) -> ControlResult {
        self.call("SetEquipment", json!([e]))
    }
    fn set_firmware(&mut self, on: bool) -> ControlResult {
        self.call("SetFirmware", json!([on]))
    }
    fn set_emulation(&mut self, c: &str, on: bool) -> ControlResult {
        self.call("SetEmulation", json!([c, on]))
    }
    fn set_preset(&mut self, p: i32) -> ControlResult {
        self.call("SetPreset", json!([p]))
    }
    fn set_unit(&mut self, g: &str, u: Option<&str>) -> ControlResult {
        self.call("SetUnit", json!([g, u]))
    }
    fn request_quit(&mut self) {
        self.calls.push(json!({"method":"RequestQuit","args":[]}));
    }
}
fn actual(line: &str) -> Result<Value, Box<dyn std::error::Error>> {
    let mut ctl = Fake::default();
    let r = control::process_line(line, &mut ctl);
    Ok(
        json!({"line":line,"cmd":r.cmd,"reply":serde_json::from_str::<Value>(&r.json)?,"start_watch":r.start_watch,"quit":r.quit,"calls":ctl.calls}),
    )
}
#[test]
fn commands_and_bad_requests_are_total() -> Result<(), Box<dyn std::error::Error>> {
    let cases: Vec<String> =
        serde_json::from_str(include_str!("../../../tests/support/control-cases.json"))?;
    for line in cases {
        assert!(actual(&line)?["reply"]["ok"].is_boolean(), "{line}");
    }
    assert_eq!(control::COMMAND_NAMES.len(), 21);
    assert_eq!(
        actual("help")?["reply"]["data"]["commands"],
        json!(icem_emulation::COMMANDS)
    );
    Ok(())
}
#[test]
fn grouping_prefix_and_ack() -> Result<(), Box<dyn std::error::Error>> {
    assert_eq!(
        control::tokenize("get \"unfinished name"),
        ["get", "unfinished name"]
    );
    assert_eq!(
        actual("set LAN.X 2")?["calls"],
        json!([{"method":"SetVariable","args":["lan","X","4000000000000000",null]}])
    );
    assert_eq!(actual("watch")?["start_watch"], true);
    assert_eq!(actual("quit")?["quit"], true);
    assert_eq!(actual("status")?["reply"]["data"]["proto"], 1);
    assert_eq!(actual("get fail")?["reply"]["error"], "backend rejected");
    Ok(())
}
#[test]
fn original_dotnet_dispatch() -> Result<(), Box<dyn std::error::Error>> {
    let root = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let bytes = if let Some(path) = std::env::var_os("ICEM_CONTROL_ORACLE") {
        std::fs::read(path)?
    } else if cfg!(windows) {
        let o = std::process::Command::new("pwsh")
            .args(["-NoProfile", "-File"])
            .arg(root.join("rust/tests/support/ControlOracle.ps1"))
            .arg("-RepoRoot")
            .arg(&root)
            .output()?;
        assert!(o.status.success(), "{}", String::from_utf8_lossy(&o.stderr));
        o.stdout
    } else {
        return Ok(());
    };
    let oracle: Value = serde_json::from_slice(&bytes)?;
    let rows = oracle["rows"].as_array().ok_or("missing rows")?;
    let mut native_errors = 0;
    for expected in rows {
        let line = expected["line"].as_str().ok_or("missing line")?;
        let mut got = actual(line)?;
        let mut expected = expected.clone();
        if expected["reply"]["error"]
            .as_str()
            .is_some_and(|e| e.starts_with("invalid JSON:"))
        {
            assert!(
                got["reply"]["error"]
                    .as_str()
                    .is_some_and(|e| e.starts_with("invalid JSON:"))
            );
            got["reply"]["error"] = json!("invalid JSON");
            expected["reply"]["error"] = json!("invalid JSON");
            native_errors += 1;
        }
        assert_eq!(got, expected, "{line}");
    }
    std::fs::create_dir_all(root.join("rust/target"))?;
    std::fs::write(
        root.join("rust/target/control-differential-report.json"),
        serde_json::to_vec_pretty(
            &json!({"result":"PASS","runtime":oracle["runtime"],"cases":rows.len(),"command_count":21,"native_json_diagnostic_cases":native_errors,"comparison":"parsed envelope, backend method+arguments, ACK flags; native JSON syntax diagnostic prose normalized"}),
        )?,
    )?;
    Ok(())
}
