//! Deterministic emulator service. The application injects time and serializes
//! commands/ticks; this module owns no timer, thread, pipe or filesystem handle.
use icem_config::emulator::EmuSettings;
use icem_protocols::ace::decode_native;
use serde_json::{Map, Value, json};
use crate::{
    cement::Context,
    control::{ControlResult, EmuControl},
    lan::{BaseProvider, SignalDef},
    lan_pipeline::LanPipeline,
    runtime_ports::{ComEmitter, LanEmitter},
    simulation::{self, Template},
    units::{Group, Preset, Settings},
};

pub const ERR_NO_COM_PORT: &str = "no COM port configured (ComPort setting is empty)";
pub const ERR_NO_LAN_ADAPTER: &str = "no LAN adapter configured (LanAdapter setting is empty)";

pub struct Engine<C: ComEmitter, L: LanEmitter> {
    settings: EmuSettings,
    com: simulation::Engine,
    lan: LanPipeline,
    com_emitter: C,
    lan_emitter: L,
    com_enabled: bool,
    lan_enabled: bool,
    com_emulating: bool,
    lan_emulating: bool,
    lan_elapsed: f64,
    was_lan_active: bool,
    now: f64,
    started_at: f64,
    previous_sample: f64,
    previous_com_lines: u64,
    previous_lan_frames: u64,
    settings_revision: u64,
    quit_requested: bool,
}

fn data(value: Value) -> Map<String, Value> {
    match value { Value::Object(map) => map, _ => Map::new() }
}
fn ok(value: Value) -> ControlResult { ControlResult::success(Some(data(value))) }
fn sv_text(sv: u32) -> String { format!("0x{sv:08X}") }
fn matches_channel(channel: Option<&str>, target: &str) -> bool {
    channel.is_none_or(|channel| channel.eq_ignore_ascii_case(target))
}
fn preset_name(preset: Preset) -> &'static str {
    match preset {
        Preset::FirmwareDefault => "FirmwareDefault",
        Preset::LanNative => "LanNative",
        Preset::Metric => "Metric",
        Preset::Imperial => "Imperial",
    }
}
fn rounded(value: f64, digits: i32) -> f64 {
    let factor = 10_f64.powi(digits);
    (value * factor).round_ties_even() / factor
}

impl<C: ComEmitter, L: LanEmitter> Engine<C, L> {
    pub fn new(settings: EmuSettings, templates: Vec<Template>, base: BaseProvider, com: C, lan: L) -> Self {
        let mut pipeline = LanPipeline::new(base);
        pipeline.frozen = true;
        pipeline.base.include_firmware_tables = settings.lan_firmware_sv_enabled;
        if let Some(network) = &mut pipeline.network {
            network.sequencer.parameters.unit_number_value = settings.lan_equipment;
        }
        let mut result = Self {
            settings, com: simulation::Engine::new(templates), lan: pipeline,
            com_emitter: com, lan_emitter: lan,
            com_enabled: false, lan_enabled: false,
            com_emulating: false, lan_emulating: false,
            lan_elapsed: 0.0, was_lan_active: false,
            now: 0.0, started_at: 0.0, previous_sample: 0.0,
            previous_com_lines: 0, previous_lan_frames: 0,
            settings_revision: 0, quit_requested: false,
        };
        result.apply_units();
        result
    }
    pub fn settings(&self) -> &EmuSettings { &self.settings }
    /// Incremented exactly where the original calls Save; start/stop stream
    /// updates are retained until another save or shutdown, as in the original.
    pub fn settings_revision(&self) -> u64 { self.settings_revision }
    pub fn quit_requested(&self) -> bool { self.quit_requested }
    pub fn com_emitter(&self) -> &C { &self.com_emitter }
    pub fn lan_emitter(&self) -> &L { &self.lan_emitter }
    pub fn lan_elapsed(&self) -> f64 { self.lan_elapsed }
    pub fn set_time(&mut self, seconds: f64) { self.now = seconds; }
    pub fn start(&mut self) {
        self.com_emulating = self.settings.com_emule_enabled;
        self.lan_emulating = self.settings.lan_emule_enabled;
        self.lan.frozen = !self.lan_emulating;
        self.lan.base.include_firmware_tables = self.settings.lan_firmware_sv_enabled;
        if self.settings.com_enabled && !self.settings.com_port.is_empty()
            && self.com_emitter.open(&self.settings.com_port).is_ok() {
            self.com_enabled = true;
            self.com.reset_clock();
        }
        if self.settings.lan_enabled && !self.settings.lan_adapter.is_empty()
            && self.lan_emitter.open(&self.settings.lan_adapter).is_ok() {
            self.lan_enabled = true;
        }
    }
    pub fn shutdown(&mut self) {
        self.com_emitter.close();
        self.lan_emitter.close();
        self.settings_revision = self.settings_revision.wrapping_add(1);
    }
    fn save_requested(&mut self) { self.settings_revision = self.settings_revision.wrapping_add(1); }
    fn apply_units(&mut self) {
        let mut settings = Settings {
            preset: u8::try_from(self.settings.unit_preset).ok().and_then(|n| Preset::try_from(n).ok()).unwrap_or(Preset::LanNative),
            ..Settings::default()
        };
        for (group, value) in [
            (Group::Flow, &self.settings.unit_flow_override),
            (Group::Temperature, &self.settings.unit_temperature_override),
            (Group::Density, &self.settings.unit_density_override),
            (Group::Volume, &self.settings.unit_volume_override),
            (Group::Level, &self.settings.unit_level_override),
            (Group::Pressure, &self.settings.unit_pressure_override),
        ] {
            if !value.is_empty() { settings.overrides.insert(group, value.clone()); }
        }
        self.lan.settings = settings;
    }
    pub fn tick(&mut self) {
        self.lan.base.include_firmware_tables = self.settings.lan_firmware_sv_enabled;
        if self.com_enabled && self.com_emitter.is_open() {
            // C# Tick returns its shared cache array; FrozenComValues returns a
            // clone unless seeding. Unit projection mutates cache only in those
            // two fresh cases. Preserve that externally visible legacy quirk.
            let fresh = self.com_emulating || self.com.current().is_none() || self.com.last_values().is_empty();
            let mut values = if self.com_emulating { self.com.tick() } else { self.com.frozen_values() };
            if let Some(template) = self.com.current() && !values.is_empty() {
                for (value, variable) in values.iter_mut().zip(&template.variables) {
                    *value = self.lan.settings.to_target(*value, &variable.unit);
                }
                let line = simulation::format_line(&values, template);
                if fresh { let _ = self.com.replace_last_values(&values); }
                let _ = self.com_emitter.send_line(&line);
            }
        }
        let lan_active = self.lan_enabled && self.lan_emitter.is_open();
        if lan_active {
            if !self.was_lan_active { self.lan_elapsed = 0.0; }
            if self.lan_emulating && let Some(network) = &mut self.lan.network {
                network.execute(&mut Context::new(self.lan_elapsed, 1.0, self.lan_elapsed as i64, 7));
            }
            let vectors = self.lan.superframe(self.settings.lan_equipment, self.lan_elapsed);
            let _ = self.lan_emitter.send_superframe(&vectors, self.settings.lan_equipment);
            self.lan_elapsed += 1.0;
        }
        self.was_lan_active = lan_active;
    }
    fn find_lan_signals(&self, name: &str, sv: Option<u32>) -> Vec<(u32, SignalDef)> {
        self.lan.base.registry().all().iter()
            .filter(|(number, _)| sv.is_none_or(|sv| sv == **number))
            .flat_map(|(number, vector)| vector.signals.iter()
                .filter(|signal| signal.name.eq_ignore_ascii_case(name))
                .map(|signal| (*number, signal.clone())))
            .collect()
    }
    fn ambiguity(name: &str, candidates: &[(u32, SignalDef)], setting: bool) -> String {
        format!("ambiguous LAN signal '{name}' in SVs: {}; {}",
            candidates.iter().map(|(sv, _)| sv_text(*sv)).collect::<Vec<_>>().join(", "),
            if setting { "disambiguate with JSON form {\"cmd\":\"set\",\"sv\":\"0x...\",\"name\":...}" } else { "pass \"sv\" in JSON form" })
    }
}

impl<C: ComEmitter, L: LanEmitter> EmuControl for Engine<C, L> {
    fn get_status(&mut self) -> Map<String, Value> {
        let dt = (self.now - self.previous_sample).max(0.001);
        let com_lines = self.com_emitter.lines_sent();
        let lan_frames = self.lan_emitter.frames_sent();
        let com_rate = (com_lines as f64 - self.previous_com_lines as f64) / dt;
        let lan_rate = (lan_frames as f64 - self.previous_lan_frames as f64) / dt;
        self.previous_sample = self.now;
        self.previous_com_lines = com_lines;
        self.previous_lan_frames = lan_frames;
        let template = self.com.current();
        let com_overrides = self.com.snapshot_overrides();
        let lan_overrides = self.lan.snapshot_overrides();
        let mut overrides = Vec::new();
        for (name, value) in &com_overrides { overrides.push(json!({"channel":"com","name":name,"value":value})); }
        for ((sv, name), value) in lan_overrides { overrides.push(json!({"channel":"lan","sv":sv_text(sv),"name":name,"value":value})); }
        let mut com_values = Map::new();
        if let Some(template) = template {
            for (variable, value) in template.variables.iter().zip(self.com.last_values()) {
                if !variable.exclude { com_values.insert(variable.name.clone(), json!(value)); }
            }
        }
        let override_of = |group| self.lan.settings.overrides.get(&group);
        let svs = self.lan.active_sv_numbers();
        data(json!({
            "uptimeSec": rounded(self.now-self.started_at,1), "pid":std::process::id(), "version":env!("CARGO_PKG_VERSION"), "selfTest":null,
            "com":{
                "streaming":self.com_enabled&&self.com_emitter.is_open(), "emulating":self.com_emulating,
                "portOpen":self.com_emitter.is_open(), "port":self.com_emitter.port_name().or_else(|| (!self.settings.com_port.is_empty()).then_some(self.settings.com_port.as_str())),
                "baud":9600,"linesSent":com_lines,"linesPerSec":rounded(com_rate,2),
                "deviceType":template.map(|t|&t.device_type),"template":template.map(|t|&t.name),
                "variableCount":template.map_or(0,|t|t.variables.len()),"values":com_values
            },
            "lan":{
                "streaming":self.lan_enabled&&self.lan_emitter.is_open(),"emulating":self.lan_emulating,
                "adapterOpen":self.lan_emitter.is_open(),"adapter":self.lan_emitter.adapter_name().or_else(|| (!self.settings.lan_adapter.is_empty()).then_some(self.settings.lan_adapter.as_str())),
                "framesSent":lan_frames,"framesPerSec":rounded(lan_rate,2),
                "npcapAvailable":self.lan_emitter.is_open()||self.lan_emitter.npcap_available(),
                "equipment":sv_text(self.settings.lan_equipment),"svCount":svs.len(),"firmwareSv":self.lan.base.include_firmware_tables,
                "svs":svs.iter().map(|sv|sv_text(*sv)).collect::<Vec<_>>(),"targets":["03:00:01:00:00:20"]
            },
            "units":{"preset":preset_name(self.lan.settings.preset),
                "flowOverride":override_of(Group::Flow),"temperatureOverride":override_of(Group::Temperature),
                "densityOverride":override_of(Group::Density),"volumeOverride":override_of(Group::Volume),
                "levelOverride":override_of(Group::Level),"pressureOverride":override_of(Group::Pressure)},
            "overrides":overrides
        }))
    }
    fn start_channel(&mut self, channel: &str) -> ControlResult {
        if channel.eq_ignore_ascii_case("com") {
            if self.com_enabled && self.com_emitter.is_open() {
                return ok(json!({"channel":"com","already":true,"port":self.com_emitter.port_name(),"streaming":true}));
            }
            if self.settings.com_port.is_empty() { return ControlResult::fail(ERR_NO_COM_PORT); }
            if let Err(error) = self.com_emitter.open(&self.settings.com_port) { return ControlResult::fail(error); }
            self.com.reset_clock(); self.com_enabled = true; self.settings.com_enabled = true;
            return ok(json!({"channel":"com","port":self.settings.com_port,"streaming":true}));
        }
        if self.lan_enabled && self.lan_emitter.is_open() {
            return ok(json!({"channel":"lan","already":true,"adapter":self.lan_emitter.adapter_name(),"streaming":true}));
        }
        if self.settings.lan_adapter.is_empty() { return ControlResult::fail(ERR_NO_LAN_ADAPTER); }
        if let Err(error) = self.lan_emitter.open(&self.settings.lan_adapter) { return ControlResult::fail(error); }
        self.lan_enabled = true; self.settings.lan_enabled = true;
        ok(json!({"channel":"lan","adapter":self.settings.lan_adapter,"streaming":true}))
    }
    fn stop_channel(&mut self, channel: &str) -> ControlResult {
        if channel.eq_ignore_ascii_case("com") {
            let was = self.com_enabled || self.com_emitter.is_open();
            self.com_enabled=false; self.settings.com_enabled=false; self.com_emitter.close();
            return ok(json!({"channel":"com","wasStreaming":was,"streaming":false}));
        }
        let was = self.lan_enabled || self.lan_emitter.is_open();
        self.lan_enabled=false; self.settings.lan_enabled=false; self.lan_emitter.close();
        ok(json!({"channel":"lan","wasStreaming":was,"streaming":false}))
    }
    fn set_variable(&mut self, channel: Option<&str>, name: &str, value: f64, sv: Option<u32>) -> ControlResult {
        let mut applied = Vec::new();
        if matches_channel(channel,"com") {
            if let Some((_, variable)) = self.com.find_variable(name) {
                let variable=variable.clone();
                self.com.set_override(&variable.name,value);
                applied.push(json!({"channel":"com","name":variable.name,"uom":variable.unit}));
            } else if channel.is_some() {
                return ControlResult::fail(format!("COM variable '{name}' not in template '{}' (see 'vars com')",self.com.current().map_or("",|t|&t.name)));
            }
        }
        if matches_channel(channel,"lan") {
            let candidates=self.find_lan_signals(name,sv);
            if candidates.len()>1 { return ControlResult::fail(Self::ambiguity(name,&candidates,true)); }
            if let Some((sv,signal))=candidates.first() {
                if let Err(reason)=self.lan.can_transmit(*sv,&signal.name) { return ControlResult::fail(format!("lan signal '{}': {reason}",signal.name)); }
                self.lan.set_override(*sv,&signal.name,value);
                applied.push(json!({"channel":"lan","sv":sv_text(*sv),"name":signal.name,"uom":signal.uom}));
            } else if channel.is_some() { return ControlResult::fail(format!("LAN signal '{name}' not in lantable (see 'vars lan')")); }
        }
        if applied.is_empty() { return ControlResult::fail(format!("variable '{name}' not found on com or lan")); }
        ok(json!({"applied":applied,"value":value,"units":"native","note":"LAN overrides are pre-unit-conversion (native); the wire value follows the Units preset"}))
    }
    fn clear_variable(&mut self, channel: Option<&str>, name: &str, sv: Option<u32>) -> ControlResult {
        let mut cleared=Vec::new(); let mut known=false;
        if matches_channel(channel,"com") && let Some((_,variable))=self.com.find_variable(name) {
            let name=variable.name.clone(); known=true;
            if self.com.clear_override(&name) { cleared.push("com"); }
        }
        if matches_channel(channel,"lan") {
            let candidates=self.find_lan_signals(name,sv);
            if candidates.len()>1 { return ControlResult::fail(Self::ambiguity(name,&candidates,false)); }
            if let Some((sv,signal))=candidates.first() { known=true; self.lan.clear_override(*sv,&signal.name); cleared.push("lan"); }
        }
        if !known { return ControlResult::fail(format!("variable '{name}' not found on {}",channel.unwrap_or("com or lan"))); }
        ok(json!({"cleared":cleared}))
    }
    fn get_variable(&mut self, channel: Option<&str>, name: &str, sv: Option<u32>) -> ControlResult {
        let com_variable=if matches_channel(channel,"com") { self.com.find_variable(name).map(|(i,v)|(i,v.clone())) } else { None };
        let candidates=if matches_channel(channel,"lan") { self.find_lan_signals(name,sv) } else { Vec::new() };
        if channel.is_none()&&com_variable.is_some()&&!candidates.is_empty() { return ControlResult::fail(format!("variable '{name}' exists on both com and lan; prefix with com. or lan.")); }
        if let Some((index,variable))=com_variable {
            let overrides=self.com.snapshot_overrides();
            let manual=overrides.iter().find(|(name,_)|name.eq_ignore_ascii_case(&variable.name)).map(|(_,value)|*value);
            let value=manual.unwrap_or_else(||self.com.last_values().get(index).copied().unwrap_or(variable.base));
            return ok(json!({"channel":"com","name":variable.name,"value":value,"uom":variable.unit,"overridden":manual.is_some(),"excludeFromTelemetry":variable.exclude}));
        }
        if candidates.len()>1 { return ControlResult::fail(Self::ambiguity(name,&candidates,false)); }
        if let Some((sv,signal))=candidates.first() {
            let vectors=self.lan.native_superframe(self.settings.lan_equipment,self.lan_elapsed);
            let Some(vector)=vectors.iter().find(|v|v.sv_number==*sv) else { return ControlResult::fail(format!("SV {} not in the current superframe (firmware SVs off?)",sv_text(*sv))); };
            let Some(value)=decode_native(&vector.payload,signal.byte_offset,signal.size,&signal.value_type,signal.das_factor) else { return ControlResult::fail(format!("signal '{}' is not numeric-decodable (type {})",signal.name,signal.value_type)); };
            let overridden=self.lan.snapshot_overrides().contains_key(&(*sv,signal.name.clone()));
            return ok(json!({"channel":"lan","sv":sv_text(*sv),"name":signal.name,"value":value,"uom":signal.uom,"native":true,"overridden":overridden}));
        }
        ControlResult::fail(format!("variable '{name}' not found on {}",channel.unwrap_or("com or lan")))
    }
    fn get_profiles(&mut self) -> ControlResult {
        let current=self.com.current();
        ok(json!({"profiles":self.com.templates().iter().map(|t|json!({"deviceType":t.device_type,"name":t.name,"variableCount":t.variables.len(),"active":current.is_some_and(|c|std::ptr::eq(c,t))})).collect::<Vec<_>>()}))
    }
    fn set_profile(&mut self, device_type: Option<&str>, name: &str) -> ControlResult {
        let matches=self.com.templates().iter().filter(|t|t.name.eq_ignore_ascii_case(name)&&device_type.is_none_or(|d|t.device_type.eq_ignore_ascii_case(d))).collect::<Vec<_>>();
        if matches.is_empty() { return ControlResult::fail(format!("profile '{name}' not found (see 'profiles')")); }
        if matches.len()>1 { return ControlResult::fail(format!("ambiguous profile '{name}'; pass \"deviceType\" in JSON form")); }
        let template=matches[0].clone(); self.com.set_template(&template.device_type,&template.name);
        ok(json!({"deviceType":template.device_type,"name":template.name,"note":"template swap is on-the-fly; COM stream not interrupted"}))
    }
    fn get_variables(&mut self, channel: Option<&str>) -> ControlResult {
        let mut result=Map::new();
        let com_overrides=self.com.snapshot_overrides(); let lan_overrides=self.lan.snapshot_overrides();
        if matches_channel(channel,"com") {
            let template=self.com.current();
            let variables=template.map(|t|t.variables.iter().enumerate().map(|(i,v)|json!({"name":v.name,"uom":v.unit,"value":self.com.last_values().get(i).copied().unwrap_or(v.base),"overridden":com_overrides.keys().any(|name|name.eq_ignore_ascii_case(&v.name)),"excludeFromTelemetry":v.exclude})).collect::<Vec<_>>()).unwrap_or_default();
            result.insert("com".into(),json!({"template":template.map(|t|&t.name),"variables":variables}));
        }
        if matches_channel(channel,"lan") {
            let mut variables=Vec::new();
            let vectors=self.lan.native_superframe(self.settings.lan_equipment,self.lan_elapsed);
            for sv in self.lan.active_sv_numbers() {
                let Some(definition)=self.lan.base.registry().get(sv) else {continue};
                let vector=vectors.iter().find(|v|v.sv_number==sv);
                for signal in &definition.signals {
                    let value=vector.and_then(|v|decode_native(&v.payload,signal.byte_offset,signal.size,&signal.value_type,signal.das_factor));
                    variables.push(json!({"sv":sv_text(sv),"name":signal.name,"uom":signal.uom,"value":value,"valueType":signal.value_type,"byteOffset":signal.byte_offset,"size":signal.size,"transmittable":self.lan.can_transmit(sv,&signal.name).is_ok(),"overridden":lan_overrides.contains_key(&(sv,signal.name.clone()))}));
                }
            }
            result.insert("lan".into(),json!({"variables":variables}));
        }
        ControlResult::success(Some(result))
    }
    fn get_ports(&mut self) -> ControlResult {
        ok(json!({"ports":self.com_emitter.ports().iter().map(|name|json!({"name":name,"open":self.com_emitter.is_open()&&self.com_emitter.port_name()==Some(name.as_str())})).collect::<Vec<_>>()}))
    }
    fn get_adapters(&mut self) -> ControlResult {
        let list=self.lan_emitter.adapters();
        ok(json!({"adapters":list.iter().map(|name|json!({"name":name,"open":self.lan_emitter.is_open()&&self.lan_emitter.adapter_name()==Some(name.as_str())})).collect::<Vec<_>>(),"npcapAvailable":!list.is_empty()||self.lan_emitter.is_open()||self.lan_emitter.npcap_available()}))
    }
    fn set_port(&mut self, port: &str) -> ControlResult {
        if port.trim().is_empty() {return ControlResult::fail("empty port name")}
        let was=self.com_enabled; let old=self.settings.com_port.clone();
        if self.com_emitter.is_open() {self.com_emitter.close();}
        self.settings.com_port=port.into(); self.settings.com_enabled=self.com_enabled||was;
        if was {
            if let Err(error)=self.com_emitter.open(port) {self.com_emitter.close();self.settings.com_port=old;return ControlResult::fail(error)}
            self.com.reset_clock(); self.com_enabled=true;
        }
        self.save_requested();ok(json!({"port":port,"streaming":self.com_enabled}))
    }
    fn set_adapter(&mut self, adapter: &str) -> ControlResult {
        if adapter.trim().is_empty() {return ControlResult::fail("empty adapter name")}
        let was=self.lan_enabled; let old=self.settings.lan_adapter.clone();
        if self.lan_emitter.is_open() {self.lan_emitter.close();}
        self.settings.lan_adapter=adapter.into(); self.settings.lan_enabled=self.lan_enabled||was;
        if was {
            if let Err(error)=self.lan_emitter.open(adapter) {self.lan_emitter.close();self.settings.lan_adapter=old;return ControlResult::fail(error)}
            self.lan_enabled=true;
        }
        self.save_requested();ok(json!({"adapter":adapter,"streaming":self.lan_enabled}))
    }
    fn set_equipment(&mut self, equipment: u32) -> ControlResult {
        self.settings.lan_equipment=equipment;
        if let Some(network)=&mut self.lan.network {network.sequencer.parameters.unit_number_value=equipment;}
        self.save_requested();ok(json!({"equipment":sv_text(equipment)}))
    }
    fn set_firmware(&mut self, on: bool) -> ControlResult {
        self.settings.lan_firmware_sv_enabled=on;self.save_requested();ok(json!({"firmwareSv":on}))
    }
    fn set_emulation(&mut self, channel: &str, on: bool) -> ControlResult {
        if channel.eq_ignore_ascii_case("com") {self.com_emulating=on;self.settings.com_emule_enabled=on;}
        else if channel.eq_ignore_ascii_case("lan") {self.lan_emulating=on;self.lan.frozen=!on;self.settings.lan_emule_enabled=on;}
        else {return ControlResult::fail(format!("unknown channel '{channel}' (com|lan)"))}
        self.save_requested();ok(json!({"channel":channel.to_ascii_lowercase(),"emulating":on}))
    }
    fn set_preset(&mut self, preset: i32) -> ControlResult {
        if !(0..=3).contains(&preset) {return ControlResult::fail(format!("preset index {preset} out of range 0..3"))}
        self.settings.unit_preset=preset;self.apply_units();self.save_requested();ok(json!({"preset":preset_name(self.lan.settings.preset)}))
    }
    fn set_unit(&mut self, group: &str, unit: Option<&str>) -> ControlResult {
        let g=group.trim().to_ascii_lowercase();let unit=unit.unwrap_or("").trim();
        let target=match g.as_str() {
            "flow"=>&mut self.settings.unit_flow_override,"temperature"=>&mut self.settings.unit_temperature_override,
            "density"=>&mut self.settings.unit_density_override,"volume"=>&mut self.settings.unit_volume_override,
            "level"=>&mut self.settings.unit_level_override,"pressure"=>&mut self.settings.unit_pressure_override,
            _=>return ControlResult::fail(format!("unknown unit group '{group}' (flow|temperature|density|volume|level|pressure)")),
        };
        *target=unit.into();self.apply_units();self.save_requested();ok(json!({"group":g,"unit":(!unit.is_empty()).then_some(unit)}))
    }
    fn request_quit(&mut self) { self.quit_requested=true; }
}
