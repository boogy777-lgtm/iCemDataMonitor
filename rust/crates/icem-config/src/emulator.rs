//! Original additive emulator XML schema and copy-only legacy discovery.
use std::{
    fmt, fs,
    io::{self, Read},
    path::{Path, PathBuf},
    sync::Mutex,
};
const MAX_BYTES: u64 = 1_048_576;
static IO_LOCK: Mutex<()> = Mutex::new(());
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SettingsError(pub String);
impl fmt::Display for SettingsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}
impl std::error::Error for SettingsError {}
trait Field: Sized {
    fn parse(s: &str) -> Option<Self>;
    fn render(&self) -> String;
}
impl Field for String {
    fn parse(s: &str) -> Option<Self> {
        Some(s.into())
    }
    fn render(&self) -> String {
        self.clone()
    }
}
impl Field for bool {
    fn parse(s: &str) -> Option<Self> {
        if s.trim().eq_ignore_ascii_case("true") {
            Some(true)
        } else if s.trim().eq_ignore_ascii_case("false") {
            Some(false)
        } else {
            None
        }
    }
    fn render(&self) -> String {
        self.to_string()
    }
}
impl Field for i32 {
    fn parse(s: &str) -> Option<Self> {
        s.trim().parse().ok()
    }
    fn render(&self) -> String {
        self.to_string()
    }
}
impl Field for u32 {
    fn parse(s: &str) -> Option<Self> {
        let s = s.trim();
        let s = s
            .strip_prefix("0x")
            .or_else(|| s.strip_prefix("0X"))
            .unwrap_or(s);
        if s.starts_with(['+', '-']) {
            s.parse::<i64>().ok().and_then(|n| u32::try_from(n).ok())
        } else {
            u32::from_str_radix(s, 16).ok().or_else(|| s.parse().ok())
        }
    }
    fn render(&self) -> String {
        format!("0x{self:08X}")
    }
}
// One schema row owns all parse/default/render edit points.
macro_rules! settings{($($field:ident:$ty:ty=$default:expr=>$xml:literal),*$(,)?)=>{
#[derive(Debug,Clone,PartialEq,Eq)] pub struct EmuSettings{$(pub $field:$ty),*}
impl Default for EmuSettings{fn default()->Self{Self{$($field:$default),*}}}
impl EmuSettings{
pub const XML_FIELDS:&'static[&'static str]=&[$($xml),*];
pub fn from_xml(text:&str)->Result<Self,SettingsError>{
if text.len() as u64>MAX_BYTES{return Err(SettingsError("emulator XML exceeds 1 MiB".into()));}
let doc=roxmltree::Document::parse_with_options(text.trim_start_matches('\u{feff}'),roxmltree::ParsingOptions{nodes_limit:8192,..Default::default()}).map_err(|e|SettingsError(e.to_string()))?;
let root=doc.root_element();let value=|name:&str|root.children().find(|n|n.is_element()&&n.tag_name().namespace().is_none()&&n.tag_name().name()==name).map(|n|n.descendants().filter(|n|n.is_text()).filter_map(|n|n.text()).collect::<String>());
let defaults=Self::default();Ok(Self{$($field:value($xml).as_deref().and_then(<$ty as Field>::parse).unwrap_or(defaults.$field)),*})}
pub fn to_xml(&self)->String{let mut s=String::from("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<iCemEmule>\n");$(s.push_str(&format!("  <{}>{}</{}>\n",$xml,escape(&self.$field.render()),$xml));)*s.push_str("</iCemEmule>\n");s}
}}}
settings! {
lan_enabled:bool=false=>"LanEnabled",
lan_adapter:String=String::new()=>"LanAdapter",
lan_equipment:u32=0x00A8_6F46=>"LanEquipment",
lan_emule_enabled:bool=false=>"LanEmuleEnabled",
lan_firmware_sv_enabled:bool=false=>"LanFirmwareSvEnabled",
unit_preset:i32=1=>"UnitPreset",
unit_flow_override:String=String::new()=>"UnitFlowOverride",
unit_temperature_override:String=String::new()=>"UnitTemperatureOverride",
unit_density_override:String=String::new()=>"UnitDensityOverride",
unit_volume_override:String=String::new()=>"UnitVolumeOverride",
unit_level_override:String=String::new()=>"UnitLevelOverride",
unit_pressure_override:String=String::new()=>"UnitPressureOverride",
com_enabled:bool=false=>"ComEnabled",
com_emule_enabled:bool=false=>"ComEmuleEnabled",
com_port:String=String::new()=>"ComPort",
com_device_type:String=String::new()=>"ComDeviceType",
com_template_name:String=String::new()=>"ComTemplateName",
window_x:i32=-1=>"WindowX",
window_y:i32=-1=>"WindowY",
window_width:i32=0=>"WindowWidth",
window_height:i32=0=>"WindowHeight",
}
fn escape(s: &str) -> String {
    s.replace("\r\n", "\n")
        .replace('\r', "\n")
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}
#[derive(Debug, Clone)]
pub struct SettingsPaths {
    pub canonical: PathBuf,
    pub legacy: Option<PathBuf>,
}
impl SettingsPaths {
    pub fn explicit(path: impl Into<PathBuf>) -> Self {
        Self {
            canonical: path.into(),
            legacy: None,
        }
    }
    pub fn station(program_data: impl AsRef<Path>, local_app_data: impl AsRef<Path>) -> Self {
        Self {
            canonical: program_data
                .as_ref()
                .join("Halliburton/iCem/DataMonitoring/emule_config.xml"),
            legacy: Some(
                local_app_data
                    .as_ref()
                    .join("Halliburton/iCem/iCemEmule/settings.xml"),
            ),
        }
    }
}
#[derive(Debug)]
pub struct LoadSettings {
    pub settings: EmuSettings,
    pub diagnostics: Vec<String>,
}
impl EmuSettings {
    /// Missing/corrupt file yields defaults. Explicit paths skip legacy migration.
    pub fn load(paths: &SettingsPaths) -> LoadSettings {
        let _guard = IO_LOCK
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let mut diagnostics = Vec::new();
        if !paths.canonical.is_file()
            && let Some(legacy) = paths.legacy.as_ref().filter(|p| p.is_file())
        {
            let migrate = || -> io::Result<()> {
                parent(&paths.canonical)?;
                let mut source = fs::File::open(legacy)?;
                let mut target = fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&paths.canonical)?;
                io::copy(&mut source, &mut target)?;
                Ok(())
            };
            if let Err(e) = migrate() {
                diagnostics.push(format!("emulator legacy copy: {e}"));
            }
        }
        let read = || -> Result<Self, SettingsError> {
            let f = fs::File::open(&paths.canonical).map_err(|e| SettingsError(e.to_string()))?;
            let mut bytes = Vec::new();
            f.take(MAX_BYTES + 1)
                .read_to_end(&mut bytes)
                .map_err(|e| SettingsError(e.to_string()))?;
            if bytes.len() as u64 > MAX_BYTES {
                return Err(SettingsError("emulator XML exceeds 1 MiB".into()));
            }
            Self::from_xml(&decode(&bytes)?)
        };
        let settings = if paths.canonical.exists() {
            match read() {
                Ok(s) => s,
                Err(e) => {
                    diagnostics.push(e.to_string());
                    Self::default()
                }
            }
        } else {
            Self::default()
        };
        LoadSettings {
            settings,
            diagnostics,
        }
    }
    /// Windows composition owns the original directory Users Modify ACL policy.
    pub fn save(&self, paths: &SettingsPaths) -> io::Result<()> {
        let _guard = IO_LOCK
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        parent(&paths.canonical)?;
        let xml = self.to_xml();
        Self::from_xml(&xml).map_err(|e| io::Error::new(io::ErrorKind::InvalidInput, e))?;
        fs::write(&paths.canonical, xml)
    }
}
fn parent(p: &Path) -> io::Result<()> {
    if let Some(p) = p.parent().filter(|p| !p.as_os_str().is_empty()) {
        fs::create_dir_all(p)?;
    }
    Ok(())
}
fn decode(bytes: &[u8]) -> Result<String, SettingsError> {
    if bytes.starts_with(&[255, 254, 0, 0]) || bytes.starts_with(&[0, 0, 254, 255]) {
        let little = bytes[0] == 255;
        let (groups, rest) = bytes[4..].as_chunks::<4>();
        if !rest.is_empty() {
            return Err(SettingsError("truncated UTF32".into()));
        }
        return groups
            .iter()
            .map(|g| {
                char::from_u32(if little {
                    u32::from_le_bytes(*g)
                } else {
                    u32::from_be_bytes(*g)
                })
                .ok_or_else(|| SettingsError("invalid UTF32".into()))
            })
            .collect();
    }
    if bytes.starts_with(&[255, 254]) || bytes.starts_with(&[254, 255]) {
        let little = bytes[0] == 255;
        let (pairs, rest) = bytes[2..].as_chunks::<2>();
        if !rest.is_empty() {
            return Err(SettingsError("truncated UTF16".into()));
        }
        let words: Vec<_> = pairs
            .iter()
            .map(|g| {
                if little {
                    u16::from_le_bytes(*g)
                } else {
                    u16::from_be_bytes(*g)
                }
            })
            .collect();
        return String::from_utf16(&words).map_err(|e| SettingsError(e.to_string()));
    }
    String::from_utf8(
        bytes
            .strip_prefix(&[239, 187, 191])
            .unwrap_or(bytes)
            .to_vec(),
    )
    .map_err(|e| SettingsError(e.to_string()))
}
