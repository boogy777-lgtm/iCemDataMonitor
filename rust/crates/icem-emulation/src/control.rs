//! Pure operator protocol v1. Framing, watch timers and ACK drain belong to transports.
use serde_json::{Map, Value, json};
pub const PROTO_VERSION: u16 = crate::PROTOCOL_VERSION;
pub const COMMAND_NAMES: &[&str] = crate::COMMANDS;
pub type Data = Map<String, Value>;
#[derive(Debug, Clone, PartialEq)]
pub struct ControlResult {
    pub ok: bool,
    pub error: Option<String>,
    pub data: Option<Data>,
}
impl ControlResult {
    pub fn success(data: Option<Data>) -> Self {
        Self {
            ok: true,
            error: None,
            data,
        }
    }
    pub fn fail(error: impl Into<String>) -> Self {
        Self {
            ok: false,
            error: Some(error.into()),
            data: None,
        }
    }
}
pub trait EmuControl {
    fn get_status(&mut self) -> Data;
    fn start_channel(&mut self, channel: &str) -> ControlResult;
    fn stop_channel(&mut self, channel: &str) -> ControlResult;
    fn set_variable(
        &mut self,
        channel: Option<&str>,
        name: &str,
        value: f64,
        sv: Option<u32>,
    ) -> ControlResult;
    fn clear_variable(
        &mut self,
        channel: Option<&str>,
        name: &str,
        sv: Option<u32>,
    ) -> ControlResult;
    fn get_variable(&mut self, channel: Option<&str>, name: &str, sv: Option<u32>)
    -> ControlResult;
    fn get_profiles(&mut self) -> ControlResult;
    fn set_profile(&mut self, device_type: Option<&str>, name: &str) -> ControlResult;
    fn get_variables(&mut self, channel: Option<&str>) -> ControlResult;
    fn get_ports(&mut self) -> ControlResult;
    fn get_adapters(&mut self) -> ControlResult;
    fn set_port(&mut self, port: &str) -> ControlResult;
    fn set_adapter(&mut self, adapter: &str) -> ControlResult;
    fn set_equipment(&mut self, equipment: u32) -> ControlResult;
    fn set_firmware(&mut self, on: bool) -> ControlResult;
    fn set_emulation(&mut self, channel: &str, on: bool) -> ControlResult;
    fn set_preset(&mut self, preset_index: i32) -> ControlResult;
    fn set_unit(&mut self, group: &str, unit: Option<&str>) -> ControlResult;
    /// Mark shutdown; the transport flushes the ACK before final teardown.
    fn request_quit(&mut self);
}
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ControlReply {
    pub cmd: String,
    pub json: String,
    pub start_watch: bool,
    pub quit: bool,
}
pub fn to_json(ok: bool, cmd: &str, data: Option<Data>, error: Option<&str>) -> String {
    let mut value = Map::new();
    value.insert("ok".into(), json!(ok));
    value.insert("cmd".into(), json!(cmd));
    if ok {
        if let Some(data) = data {
            value.insert("data".into(), Value::Object(data));
        }
    } else {
        value.insert("error".into(), json!(error.unwrap_or("error")));
    }
    Value::Object(value).to_string()
}
fn reply(cmd: &str, r: ControlResult) -> ControlReply {
    ControlReply {
        cmd: cmd.into(),
        json: to_json(r.ok, cmd, r.data, r.error.as_deref()),
        start_watch: false,
        quit: false,
    }
}
fn error(cmd: &str, msg: impl Into<String>) -> ControlReply {
    reply(cmd, ControlResult::fail(msg))
}
fn ok(cmd: &str, data: Value) -> ControlReply {
    reply(cmd, ControlResult::success(data.as_object().cloned()))
}
fn whitespace(c: char) -> bool {
    matches!(c,'\u{9}'..='\u{d}'|'\u{20}'|'\u{85}'|'\u{a0}'|'\u{1680}'|'\u{2000}'..='\u{200a}'|'\u{2028}'|'\u{2029}'|'\u{202f}'|'\u{205f}'|'\u{3000}')
}
fn trim(s: &str) -> &str {
    s.trim_matches(whitespace)
}
/// Quotes group whitespace, backslashes are literal, unmatched quotes are accepted.
pub fn tokenize(line: &str) -> Vec<String> {
    let mut result = Vec::new();
    let mut current = String::new();
    let (mut quoted, mut has) = (false, false);
    for c in line.chars() {
        if quoted {
            if c == '"' {
                quoted = false;
            } else {
                current.push(c);
            }
        } else if c == '"' {
            quoted = true;
            has = true;
        } else if whitespace(c) {
            if !current.is_empty() || has {
                result.push(std::mem::take(&mut current));
                has = false;
            }
        } else {
            current.push(c);
            has = true;
        }
    }
    if !current.is_empty() || has {
        result.push(current);
    }
    result
}
/// NumberStyles.Float, InvariantCulture; explicit NaN preserves .NET canonical bits.
pub fn parse_number(text: &str) -> Option<f64> {
    let text = text.trim_matches(|c: char| matches!(c, '\u{9}'..='\u{d}' | ' '));
    if ["NaN", "+NaN", "-NaN"]
        .iter()
        .any(|n| text.eq_ignore_ascii_case(n))
    {
        return Some(f64::from_bits(0xfff8_0000_0000_0000));
    }
    if ["Infinity", "+Infinity"]
        .iter()
        .any(|n| text.eq_ignore_ascii_case(n))
    {
        return Some(f64::INFINITY);
    }
    if text.eq_ignore_ascii_case("-Infinity") {
        return Some(f64::NEG_INFINITY);
    }
    if !text
        .bytes()
        .all(|c| c.is_ascii_digit() || matches!(c, b'+' | b'-' | b'.' | b'e' | b'E'))
    {
        return None;
    }
    text.parse().ok()
}
fn uint(text: &str) -> Option<u32> {
    let text = trim(text);
    if text.get(..2).is_some_and(|s| s.eq_ignore_ascii_case("0x")) {
        let hex = trim(&text[2..]);
        if hex.is_empty() || !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
            return None;
        }
        u32::from_str_radix(hex, 16).ok()
    } else {
        let digits = text.strip_prefix('+').unwrap_or(text);
        if digits == "-0" {
            return Some(0);
        }
        if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        digits.parse().ok()
    }
}
fn valid_channel(c: Option<&str>) -> Result<&str, String> {
    match c {
        Some(c) if c.eq_ignore_ascii_case("com") || c.eq_ignore_ascii_case("lan") => Ok(c),
        Some(c) => Err(format!("unknown channel '{c}' (com|lan)")),
        None => Err("channel required (com|lan)".into()),
    }
}
#[derive(Default)]
struct Parsed {
    cmd: String,
    channel: Option<String>,
    name: Option<String>,
    value: Option<f64>,
    device_type: Option<String>,
    sv: Option<String>,
}
fn parse_json(line: &str) -> Result<Parsed, String> {
    let root: Value = serde_json::from_str(line).map_err(|e| format!("invalid JSON: {e}"))?;
    let root = root
        .as_object()
        .ok_or("JSON command must be an object with a 'cmd' string property")?;
    let get = |key| root.get(key).and_then(Value::as_str).map(str::to_owned);
    let cmd = get("cmd").ok_or("missing 'cmd' string property")?;
    let value = match root.get("value") {
        None => None,
        Some(Value::Number(n)) => n.as_f64(),
        Some(Value::String(s)) => Some(parse_number(s).ok_or("'value' must be a number")?),
        Some(_) => return Err("'value' must be a number".into()),
    };
    Ok(Parsed {
        cmd,
        value,
        channel: get("channel"),
        name: get("name"),
        device_type: get("deviceType"),
        sv: get("sv"),
    })
}
fn parse_text(line: &str) -> Result<Parsed, ControlReply> {
    let t = tokenize(line);
    let Some(cmd) = t.first() else {
        return Err(error("", "empty command line"));
    };
    let mut p = Parsed {
        cmd: cmd.clone(),
        ..Parsed::default()
    };
    let require = |n, msg: &str| {
        if t.len() < n {
            Err(error(cmd, msg))
        } else {
            Ok(())
        }
    };
    match cmd.to_lowercase().as_str() {
        "start" | "stop" => {
            require(2, &format!("'{cmd}' requires a channel (com|lan)"))?;
            p.channel = Some(t[1].clone());
        }
        "set" => {
            require(
                3,
                "'set' requires a name and a value: set \"<name>\" <value>",
            )?;
            let last = &t[t.len() - 1];
            p.value = Some(
                parse_number(last)
                    .ok_or_else(|| error(cmd, format!("value '{last}' is not a number")))?,
            );
            p.name = Some(t[1..t.len() - 1].join(" "));
        }
        "get" | "clear" => {
            require(2, &format!("'{cmd}' requires a variable name"))?;
            p.name = Some(t[1..].join(" "));
        }
        "profile" => {
            require(2, "'profile' requires a template name (see 'profiles')")?;
            p.name = Some(t[1..].join(" "));
        }
        "vars" => p.channel = t.get(1).cloned(),
        "setport" | "setadapter" => {
            let endpoint = if cmd.eq_ignore_ascii_case("setport") {
                "a port"
            } else {
                "an adapter"
            };
            require(
                2,
                &format!("'{}' requires {endpoint} name", cmd.to_lowercase()),
            )?;
            p.name = Some(t[1..].join(" "));
        }
        "setequipment" => {
            require(2, "'setequipment' requires 0x... or decimal")?;
            p.value = Some(
                if trim(&t[1])
                    .get(..2)
                    .is_some_and(|s| s.eq_ignore_ascii_case("0x"))
                {
                    uint(&t[1]).map(f64::from)
                } else {
                    parse_number(&t[1])
                }
                .ok_or_else(|| error(cmd, format!("equipment '{}' is not a number", t[1])))?,
            );
        }
        "setfirmware" | "setpreset" => {
            let message = if cmd.eq_ignore_ascii_case("setfirmware") {
                "'setfirmware' requires 0|1"
            } else {
                "'setpreset' requires 0..3"
            };
            p.value = Some(
                t.get(1)
                    .and_then(|v| parse_number(v))
                    .ok_or_else(|| error(cmd, message))?,
            );
        }
        "setemulate" => {
            p.value = Some(t.get(2).and_then(|v| parse_number(v)).ok_or_else(|| {
                error(
                    cmd,
                    "'setemulate' requires a channel and 0|1: setemulate com 0",
                )
            })?);
            p.channel = Some(t[1].clone());
        }
        "setunit" => {
            require(
                2,
                "'setunit' requires a group (flow|temperature|density|volume|level|pressure) and optional unit",
            )?;
            p.channel = Some(t[1].clone());
            p.name = Some(t[2..].join(" "));
        }
        _ => {}
    }
    Ok(p)
}
pub fn process_line(line: &str, ctl: &mut impl EmuControl) -> ControlReply {
    if trim(line).is_empty() {
        return error("", "empty command line");
    }
    let mut p = if line.trim_start_matches(whitespace).starts_with('{') {
        match parse_json(line) {
            Ok(p) => p,
            Err(e) => return error("", e),
        }
    } else {
        match parse_text(line) {
            Ok(p) => p,
            Err(e) => return e,
        }
    };
    p.cmd = trim(&p.cmd).to_lowercase();
    if let Some(name) = &p.name {
        for prefix in ["com.", "lan."] {
            if name
                .get(..4)
                .is_some_and(|s| s.eq_ignore_ascii_case(prefix))
            {
                p.channel = Some(prefix[..3].into());
                p.name = Some(name[4..].into());
                break;
            }
        }
    }
    let cmd = p.cmd.as_str();
    let sv = match p.sv.as_deref() {
        None => None,
        Some(s) => match uint(s) {
            Some(v) => Some(v),
            None => return error(cmd, format!("sv '{s}' is not a uint32 (decimal or 0x...)")),
        },
    };
    let name = p.name.as_deref().filter(|s| !s.is_empty());
    let channel = p.channel.as_deref();
    let result = match cmd {
        "status" => {
            let mut data = ctl.get_status();
            data.insert("proto".into(), json!(PROTO_VERSION));
            return reply(cmd, ControlResult::success(Some(data)));
        }
        "start" | "stop" => match valid_channel(channel) {
            Ok(c) => {
                if cmd == "start" {
                    ctl.start_channel(c)
                } else {
                    ctl.stop_channel(c)
                }
            }
            Err(e) => return error(cmd, e),
        },
        "set" | "get" | "clear" => {
            let Some(name) = name else {
                return error(cmd, format!("'{cmd}' requires a variable name"));
            };
            if cmd == "set" {
                let Some(v) = p.value else {
                    return error(cmd, "'set' requires a numeric value");
                };
                ctl.set_variable(channel, name, v, sv)
            } else if cmd == "get" {
                ctl.get_variable(channel, name, sv)
            } else {
                ctl.clear_variable(channel, name, sv)
            }
        }
        "profiles" => ctl.get_profiles(),
        "profile" => {
            let Some(name) = name else {
                return error(cmd, "'profile' requires a template name (see 'profiles')");
            };
            ctl.set_profile(p.device_type.as_deref(), name)
        }
        "vars" => {
            if let Some(c) = channel
                && let Err(e) = valid_channel(Some(c))
            {
                return error(cmd, e);
            }
            ctl.get_variables(channel)
        }
        "watch" => {
            let mut r = ok(cmd, json!({"intervalSec":1}));
            r.start_watch = true;
            return r;
        }
        "quit" => {
            ctl.request_quit();
            let mut r = ok(cmd, json!({"quitting":true}));
            r.quit = true;
            return r;
        }
        "help" => return ok(cmd, json!({"commands":COMMAND_NAMES})),
        "ports" => ctl.get_ports(),
        "adapters" => ctl.get_adapters(),
        "setport" | "setadapter" => {
            let Some(name) = name else {
                return error(
                    cmd,
                    format!(
                        "'{cmd}' requires {} name",
                        if cmd == "setport" {
                            "a port"
                        } else {
                            "an adapter"
                        }
                    ),
                );
            };
            if cmd == "setport" {
                ctl.set_port(name)
            } else {
                ctl.set_adapter(name)
            }
        }
        "setequipment" => {
            let Some(v) = p.value.filter(|v| !(*v < 0.0 || *v > f64::from(u32::MAX))) else {
                return error(cmd, "'setequipment' requires 0x... or decimal uint32");
            };
            ctl.set_equipment(v as u32)
        }
        "setfirmware" => {
            let Some(v) = p.value else {
                return error(cmd, "'setfirmware' requires 0|1");
            };
            ctl.set_firmware(v != 0.0)
        }
        "setemulate" => {
            let c = match valid_channel(channel) {
                Ok(c) => c,
                Err(e) => return error(cmd, e),
            };
            let Some(v) = p.value else {
                return error(cmd, "'setemulate' requires 0|1");
            };
            ctl.set_emulation(c, v != 0.0)
        }
        "setpreset" => {
            let Some(v) = p.value.filter(|v| !(*v < 0.0 || *v > 3.0)) else {
                return error(cmd, "'setpreset' requires 0..3");
            };
            ctl.set_preset(v as i32)
        }
        "setunit" => {
            let Some(group) = channel.filter(|g| !g.is_empty()) else {
                return error(cmd, "'setunit' requires a group");
            };
            ctl.set_unit(group, name)
        }
        _ => {
            return error(
                cmd,
                format!(
                    "unknown command '{cmd}'; expected one of: {}",
                    COMMAND_NAMES.join(", ")
                ),
            );
        }
    };
    reply(cmd, result)
}
