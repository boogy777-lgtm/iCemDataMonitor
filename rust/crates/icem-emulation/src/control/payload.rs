//! Owned domain replies. IEEE outputs remain f64 until the v1 adapter/display boundary.
use crate::Channel;
use std::collections::BTreeMap;
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Status {
 pub uptime_sec: f64, pub pid: u32, pub version: String,
 pub com: ComStatus, pub lan: LanStatus, pub units: UnitStatus,
 pub overrides: Vec<Override>,
}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ComStatus {
 pub streaming: bool, pub emulating: bool, pub port_open: bool, pub port: Option<String>,
 pub baud: u32, pub lines_sent: u64, pub lines_per_sec: f64,
 pub device_type: Option<String>, pub template: Option<String>, pub variable_count: usize,
 pub values: BTreeMap<String,f64>,
}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct LanStatus {
 pub streaming: bool, pub emulating: bool, pub adapter_open: bool, pub adapter: Option<String>,
 pub frames_sent: u64, pub frames_per_sec: f64, pub npcap_available: bool,
 pub equipment: u32, pub sv_count: usize, pub firmware_sv: bool, pub svs: Vec<u32>, pub targets: Vec<String>,
}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct UnitStatus {
 pub preset: String,
 pub flow_override: Option<String>, pub temperature_override: Option<String>,
 pub density_override: Option<String>, pub volume_override: Option<String>,
 pub level_override: Option<String>, pub pressure_override: Option<String>,
}
#[derive(Debug, Clone, PartialEq)]
pub enum Signal { Com {name:String}, Lan {sv:u32,name:String} }
#[derive(Debug, Clone, PartialEq)]
pub struct Override {pub signal:Signal,pub value:f64}
#[derive(Debug, Clone, PartialEq)]
pub enum Start {Com {port:Option<String>,already:bool},Lan {adapter:Option<String>,already:bool}}
#[derive(Debug, Clone, PartialEq)]
pub struct Stop {pub channel:Channel,pub was_streaming:bool}
#[derive(Debug, Clone, PartialEq)]
pub struct AppliedSignal {pub signal:Signal,pub uom:String}
#[derive(Debug, Clone, PartialEq)]
pub struct Set {pub applied:Vec<AppliedSignal>,pub value:f64}
#[derive(Debug, Clone, PartialEq)]
pub struct Clear {pub cleared:Vec<Channel>}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ComVariable {pub name:String,pub uom:String,pub value:f64,pub overridden:bool,pub exclude_from_telemetry:bool}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct LanVariable {pub sv:u32,pub name:String,pub uom:String,pub value:Option<f64>,pub value_type:String,pub byte_offset:i32,pub size:i32,pub transmittable:bool,pub overridden:bool}
#[derive(Debug, Clone, PartialEq)]
pub enum Get {Com(ComVariable),Lan {sv:u32,name:String,uom:String,value:f64,overridden:bool}}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ComVariables {pub template:Option<String>,pub variables:Vec<ComVariable>}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Variables {pub com:Option<ComVariables>,pub lan:Option<Vec<LanVariable>>}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Profile {pub device_type:String,pub name:String}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ProfileInfo {pub profile:Profile,pub variable_count:usize,pub active:bool}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Endpoint {pub name:String,pub open:bool}
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Adapters {pub adapters:Vec<Endpoint>,pub npcap_available:bool}
#[derive(Debug, Clone, PartialEq)]
pub struct PortChanged {pub port:String,pub streaming:bool}
#[derive(Debug, Clone, PartialEq)]
pub struct AdapterChanged {pub adapter:String,pub streaming:bool}
#[derive(Debug, Clone, PartialEq)]
pub struct Emulation {pub channel:Channel,pub emulating:bool}
#[derive(Debug, Clone, PartialEq)]
pub struct UnitChanged {pub group:String,pub unit:Option<String>}
/// One payload family for each operator command; no open-ended property bag.
#[derive(Debug, Clone, PartialEq)]
pub enum Payload {
 Status(Status), Start(Start), Stop(Stop), Set(Set), Get(Get), Clear(Clear),
 Profiles(Vec<ProfileInfo>), Profile(Profile), Variables(Variables), Watch, Quit, Help,
 Ports(Vec<Endpoint>), Adapters(Adapters), SetPort(PortChanged),SetAdapter(AdapterChanged),
 SetEquipment(u32),SetFirmware(bool),SetEmulation(Emulation),SetPreset(String),SetUnit(UnitChanged),
}
impl Payload {
 pub const fn command_name(&self)-> &'static str {match self {
 Self::Status(_)=>"status",Self::Start(_)=>"start",Self::Stop(_)=>"stop",Self::Set(_)=>"set",Self::Get(_)=>"get",Self::Clear(_)=>"clear",Self::Profiles(_)=>"profiles",Self::Profile(_)=>"profile",Self::Variables(_)=>"vars",Self::Watch=>"watch",Self::Quit=>"quit",Self::Help=>"help",Self::Ports(_)=>"ports",Self::Adapters(_)=>"adapters",Self::SetPort(_)=>"setport",Self::SetAdapter(_)=>"setadapter",Self::SetEquipment(_)=>"setequipment",Self::SetFirmware(_)=>"setfirmware",Self::SetEmulation(_)=>"setemulate",Self::SetPreset(_)=>"setpreset",Self::SetUnit(_)=>"setunit",
 }}
}
