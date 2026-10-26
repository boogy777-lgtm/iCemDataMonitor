//! Frozen pipe v1 projection; never read by application or presentation.
use super::payload::*;
use serde_json::{Value, json};
fn sv(v: u32) -> String {
    format!("0x{v:08X}")
}
fn signal(v: Signal) -> Value {
    match v {
        Signal::Com { name } => json!({"channel":"com","name":name}),
        Signal::Lan { sv: n, name } => json!({"channel":"lan","sv":sv(n),"name":name}),
    }
}
fn com(v: ComVariable) -> Value {
    json!({"name":v.name,"uom":v.uom,"value":v.value,"overridden":v.overridden,"excludeFromTelemetry":v.exclude_from_telemetry})
}
fn lan(v: LanVariable) -> Value {
    json!({"sv":sv(v.sv),"name":v.name,"uom":v.uom,"value":v.value,"valueType":v.value_type,"byteOffset":v.byte_offset,"size":v.size,"transmittable":v.transmittable,"overridden":v.overridden})
}
fn endpoint(v: Endpoint) -> Value {
    json!({"name":v.name,"open":v.open})
}
pub(super) fn data(value: Payload) -> Value {
    match value {
        Payload::Status(s) => {
            json!({"proto":super::PROTO_VERSION,"uptimeSec":s.uptime_sec,"pid":s.pid,"version":s.version,"selfTest":null,
"com":{"streaming":s.com.streaming,"emulating":s.com.emulating,"portOpen":s.com.port_open,"port":s.com.port,"baud":s.com.baud,"linesSent":s.com.lines_sent,"linesPerSec":s.com.lines_per_sec,"deviceType":s.com.device_type,"template":s.com.template,"variableCount":s.com.variable_count,"values":s.com.values},
"lan":{"streaming":s.lan.streaming,"emulating":s.lan.emulating,"adapterOpen":s.lan.adapter_open,"adapter":s.lan.adapter,"framesSent":s.lan.frames_sent,"framesPerSec":s.lan.frames_per_sec,"npcapAvailable":s.lan.npcap_available,"equipment":sv(s.lan.equipment),"svCount":s.lan.sv_count,"firmwareSv":s.lan.firmware_sv,"svs":s.lan.svs.into_iter().map(sv).collect::<Vec<_>>(),"targets":s.lan.targets},
"units":{"preset":s.units.preset,"flowOverride":s.units.flow_override,"temperatureOverride":s.units.temperature_override,"densityOverride":s.units.density_override,"volumeOverride":s.units.volume_override,"levelOverride":s.units.level_override,"pressureOverride":s.units.pressure_override},
"overrides":s.overrides.into_iter().map(|v|{let mut s=signal(v.signal);s["value"]=json!(v.value);s}).collect::<Vec<_>>() })
        }
        Payload::Start(s) => {
            let (mut v, already) = match s {
                Start::Com { port, already } => (
                    json!({"channel":"com","port":port,"streaming":true}),
                    already,
                ),
                Start::Lan { adapter, already } => (
                    json!({"channel":"lan","adapter":adapter,"streaming":true}),
                    already,
                ),
            };
            if already {
                v["already"] = json!(true);
            }
            v
        }
        Payload::Stop(v) => {
            json!({"channel":v.channel.as_str(),"wasStreaming":v.was_streaming,"streaming":false})
        }
        Payload::Set(v) => {
            json!({"applied":v.applied.into_iter().map(|v|{let mut s=signal(v.signal);s["uom"]=json!(v.uom);s}).collect::<Vec<_>>(),"value":v.value,"units":"native","note":"LAN overrides are pre-unit-conversion (native); the wire value follows the Units preset"})
        }
        Payload::Get(v) => match v {
            Get::Com(v) => {
                let mut v = com(v);
                v["channel"] = json!("com");
                v
            }
            Get::Lan {
                sv: n,
                name,
                uom,
                value,
                overridden,
            } => {
                json!({"channel":"lan","sv":sv(n),"name":name,"uom":uom,"value":value,"native":true,"overridden":overridden})
            }
        },
        Payload::Clear(v) => {
            json!({"cleared":v.cleared.into_iter().map(crate::Channel::as_str).collect::<Vec<_>>()})
        }
        Payload::Profiles(v) => {
            json!({"profiles":v.into_iter().map(|v|json!({"deviceType":v.profile.device_type,"name":v.profile.name,"variableCount":v.variable_count,"active":v.active})).collect::<Vec<_>>()})
        }
        Payload::Profile(v) => {
            json!({"deviceType":v.device_type,"name":v.name,"note":"template swap is on-the-fly; COM stream not interrupted"})
        }
        Payload::Variables(v) => {
            let mut r = serde_json::Map::new();
            if let Some(v) = v.com {
                r.insert("com".into(),json!({"template":v.template,"variables":v.variables.into_iter().map(com).collect::<Vec<_>>()}));
            }
            if let Some(v) = v.lan {
                r.insert(
                    "lan".into(),
                    json!({"variables":v.into_iter().map(lan).collect::<Vec<_>>()}),
                );
            }
            Value::Object(r)
        }
        Payload::Watch => json!({"intervalSec":1}),
        Payload::Quit => json!({"quitting":true}),
        Payload::Help => json!({"commands":super::COMMAND_NAMES}),
        Payload::Ports(v) => json!({"ports":v.into_iter().map(endpoint).collect::<Vec<_>>()}),
        Payload::Adapters(v) => {
            json!({"adapters":v.adapters.into_iter().map(endpoint).collect::<Vec<_>>(),"npcapAvailable":v.npcap_available})
        }
        Payload::SetPort(v) => json!({"port":v.port,"streaming":v.streaming}),
        Payload::SetAdapter(v) => json!({"adapter":v.adapter,"streaming":v.streaming}),
        Payload::SetEquipment(v) => json!({"equipment":sv(v)}),
        Payload::SetFirmware(v) => json!({"firmwareSv":v}),
        Payload::SetEmulation(v) => json!({"channel":v.channel.as_str(),"emulating":v.emulating}),
        Payload::SetPreset(v) => json!({"preset":v}),
        Payload::SetUnit(v) => json!({"group":v.group,"unit":v.unit}),
    }
}
