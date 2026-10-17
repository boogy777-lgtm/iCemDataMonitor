//! ADR 0004: dynamically loaded Npcap ABI, owned packet copies only.
use std::{ffi::{CStr, CString, c_char, c_int, c_void}, ptr, sync::Arc};
use windows_sys::Win32::{Foundation::{FreeLibrary, HMODULE}, System::{LibraryLoader::{GetProcAddress, LoadLibraryExW, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32}, SystemInformation::GetSystemDirectoryW}, NetworkManagement::IpHelper::{GetAdaptersInfo, IP_ADAPTER_INFO}};
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Adapter { pub name: String, pub description: String }
#[repr(C)]
#[derive(Clone, Copy)]
struct PcapIf { next: *mut Self, name: *const c_char, description: *const c_char, addresses: *mut c_void, flags: u32 }
#[repr(C)]
#[derive(Clone, Copy)]
struct PacketHeader { seconds: i32, microseconds: i32, caplen: u32, len: u32 }
type FindAll = unsafe extern "C" fn(*mut *mut PcapIf, *mut c_char) -> c_int;
type FreeAll = unsafe extern "C" fn(*mut PcapIf);
type OpenLive = unsafe extern "C" fn(*const c_char, c_int, c_int, c_int, *mut c_char) -> *mut c_void;
type Close = unsafe extern "C" fn(*mut c_void);
type SendPacket = unsafe extern "C" fn(*mut c_void, *const u8, c_int) -> c_int;
type NextEx = unsafe extern "C" fn(*mut c_void, *mut *const PacketHeader, *mut *const u8) -> c_int;
type GetError = unsafe extern "C" fn(*mut c_void) -> *const c_char;
type DataLink = unsafe extern "C" fn(*mut c_void) -> c_int;
struct Library(usize);
impl Drop for Library {
    fn drop(&mut self) {
        // SAFETY: unique LoadLibraryExW reference; all sessions retain Arc<Pcap>
        // and therefore prevent unloading while any ABI calls are possible.
        unsafe { FreeLibrary(self.0 as HMODULE); }
    }
}
pub struct Pcap { _library: Library, find_all: FindAll, free_all: FreeAll, open_live: OpenLive, close: Close, send_packet: SendPacket, next_ex: NextEx, get_error: GetError, datalink: DataLink }
fn dll_paths() -> Result<Vec<Vec<u16>>, String> {
    let mut path = vec![0u16; 32768];
    // SAFETY: owned UTF-16 buffer of stated capacity, Win32 retains no pointer.
    let count = unsafe { GetSystemDirectoryW(path.as_mut_ptr(), path.len() as u32) } as usize;
    if count == 0 || count >= path.len() { return Err("Cannot locate Windows system directory".into()); }
    path.truncate(count);
    Ok([r"\Npcap\wpcap.dll", r"\wpcap.dll"].into_iter().map(|s| path.iter().copied().chain(s.encode_utf16()).chain([0]).collect()).collect())
}
pub fn npcap_available() -> bool {
    dll_paths().is_ok_and(|paths| paths.into_iter().any(|p| std::path::Path::new(&String::from_utf16_lossy(&p[..p.len()-1])).is_file()))
}
impl Pcap {
    pub fn load() -> Result<Arc<Self>, String> {
        let mut loaded = ptr::null_mut();
        for path in dll_paths()? {
            // SAFETY: absolute System32 path, dependency search restricted to DLL
            // directory and System32; no application/CWD/PATH DLL loading.
            loaded = unsafe { LoadLibraryExW(path.as_ptr(), ptr::null_mut(), LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32) };
            if !loaded.is_null() { break; }
        }
        if loaded.is_null() { return Err(format!("Npcap not found or unavailable: {}", std::io::Error::last_os_error())); }
        let library = Library(loaded as usize);
        macro_rules! symbol {
            ($name:literal, $ty:ty) => {{
                // SAFETY: loaded module retained by owner; static NUL-terminated
                // ASCII name denotes this exact documented Npcap C ABI symbol.
                let proc = unsafe { GetProcAddress(loaded, concat!($name, "\0").as_ptr()) }.ok_or_else(|| format!("Npcap missing export {}", $name))?;
                // SAFETY: concrete function signature exactly matches named Npcap
                // export; no generic/untyped code pointer escapes native module.
                unsafe { std::mem::transmute::<unsafe extern "system" fn() -> isize, $ty>(proc) }
            }};
        }
        let find_all = symbol!("pcap_findalldevs", FindAll); let free_all = symbol!("pcap_freealldevs", FreeAll); let open_live = symbol!("pcap_open_live", OpenLive); let close = symbol!("pcap_close", Close); let send_packet = symbol!("pcap_sendpacket", SendPacket); let next_ex = symbol!("pcap_next_ex", NextEx); let get_error = symbol!("pcap_geterr", GetError); let datalink = symbol!("pcap_datalink", DataLink);
        Ok(Arc::new(Self { _library: library, find_all, free_all, open_live, close, send_packet, next_ex, get_error, datalink }))
    }
    pub fn adapters(&self) -> Result<Vec<Adapter>, String> {
        let mut list = ptr::null_mut(); let mut error = [0i8; 256];
        // SAFETY: local outputs; successful list stays allocated until guarded free.
        if unsafe { (self.find_all)(&mut list, error.as_mut_ptr()) } != 0 { return Err(error_buffer(&error)); }
        struct Devices<'a> { pointer: *mut PcapIf, owner: &'a Pcap }
        impl Drop for Devices<'_> { fn drop(&mut self) {
            // SAFETY: exactly one free for successful findalldevs-owned list.
            unsafe { (self.owner.free_all)(self.pointer); }
        } }
        let _devices = Devices { pointer: list, owner: self }; let mut result = Vec::new();
        while !list.is_null() && result.len() < 4096 {
            // SAFETY: library-produced immutable list retained by local guard;
            // copy metadata, never form Rust references into foreign allocation.
            let item = unsafe { list.read() };
            // SAFETY: documented Npcap optional NUL-terminated list strings stay
            // valid until freealldevs; both strings copied before free.
            let (name, description) = unsafe { (c_string(item.name), c_string(item.description)) };
            result.push(Adapter { name, description }); list = item.next;
        }
        if !list.is_null() { return Err("Npcap adapter list exceeds 4096 entries".into()); } Ok(result)
    }
    pub fn open(self: &Arc<Self>, name: &str, promiscuous: bool) -> Result<PacketSession, String> {
        let name = CString::new(name).map_err(|_| "Adapter name contains NUL")?; let mut error = [0i8; 256];
        // SAFETY: stable CString/local error buffer, bounded snaplen, finite100ms
        // read timeout; returned pcap_t uniquely owned on success.
        let raw = unsafe { (self.open_live)(name.as_ptr(), 65535, i32::from(promiscuous), 100, error.as_mut_ptr()) };
        if raw.is_null() { return Err(error_buffer(&error)); }
        let session = PacketSession { handle: raw as usize, owner: Arc::clone(self) };
        // SAFETY: live uniquely owned pcap_t used only for read-only ABI query.
        if unsafe { (self.datalink)(raw) } != 1 { return Err("Selected adapter is not Ethernet (DLT_EN10MB required)".into()); } Ok(session)
    }
}
/// Uniquely owned session, movable without unsafe Send/Sync implementations.
/// All mutating ABI calls require exclusive borrow; no raw pointers escape.
pub struct PacketSession { handle: usize, owner: Arc<Pcap> }
impl PacketSession {
    pub fn send(&mut self, bytes: &[u8]) -> Result<(), String> {
        if bytes.is_empty() || bytes.len() > 65535 { return Err("Packet size must be 1..65535".into()); }
        // SAFETY: exclusive live session, borrowed immutable buffer lives until
        // synchronous send returns; its bounded length fits c_int.
        if unsafe { (self.owner.send_packet)(self.handle as *mut c_void, bytes.as_ptr(), bytes.len() as i32) } != 0 { return Err(self.error()); } Ok(())
    }
    pub fn next_packet(&mut self) -> Result<Option<Vec<u8>>, String> {
        let mut header = ptr::null(); let mut packet = ptr::null();
        // SAFETY: exclusive live session, valid local output pointers; returned
        // packet/header remain readable until next read, copied before then.
        match unsafe { (self.owner.next_ex)(self.handle as *mut c_void, &mut header, &mut packet) } {
            0 => Ok(None), 1 => {
                if header.is_null() || packet.is_null() { return Err("Npcap returned null packet".into()); }
                // SAFETY: successful next_ex promises readable packet metadata.
                let header = unsafe { header.read() };
                if header.caplen > 65535 || header.caplen > header.len { return Err("Npcap returned invalid captured size".into()); }
                let mut owned = vec![0u8; header.caplen as usize];
                // SAFETY: library promises caplen readable bytes; uniquely owned
                // destination separate allocation, no borrowed packet escapes.
                unsafe { ptr::copy_nonoverlapping(packet, owned.as_mut_ptr(), owned.len()); } Ok(Some(owned))
            }, -2 => Err("Npcap capture stopped".into()), _ => Err(self.error()),
        }
    }
    fn error(&self) -> String {
        // SAFETY: live session provides NUL-terminated error valid until next
        // operation; copied immediately, only called under exclusive operation.
        unsafe { c_string((self.owner.get_error)(self.handle as *mut c_void)) }
    }
}
impl Drop for PacketSession { fn drop(&mut self) {
    // SAFETY: unique successful open handle; Arc keeps DLL loaded through close.
    unsafe { (self.owner.close)(self.handle as *mut c_void); }
} }
unsafe fn c_string(pointer: *const c_char) -> String {
    if pointer.is_null() { return String::new(); }
    // SAFETY: caller guarantees a live, NUL-terminated foreign string.
    unsafe { CStr::from_ptr(pointer) }.to_string_lossy().into_owned()
}
fn error_buffer(buffer: &[i8; 256]) -> String {
    let bytes: Vec<u8> = buffer.iter().take_while(|b| **b != 0).map(|b| b.cast_unsigned()).collect(); String::from_utf8_lossy(&bytes).into_owned()
}
pub fn adapter_mac(name: &str) -> Option<[u8; 6]> {
    let mut size = 0;
    // SAFETY: null first-call query with initialized size output.
    unsafe { GetAdaptersInfo(ptr::null_mut(), &mut size); }
    if size == 0 || size > 1_048_576 { return None; }
    let count = (size as usize).div_ceil(size_of::<IP_ADAPTER_INFO>()); let mut buffer = vec![IP_ADAPTER_INFO::default(); count];
    // SAFETY: aligned initialized allocation >= requested bytes, exclusively
    // writable during call, OS links retained only inside this allocation.
    if unsafe { GetAdaptersInfo(buffer.as_mut_ptr(), &mut size) } != 0 { return None; }
    let start = buffer.as_ptr() as usize; let end = start + buffer.len() * size_of::<IP_ADAPTER_INFO>(); let mut next = buffer.as_ptr(); let wanted = name.to_ascii_lowercase();
    for _ in 0..4096 {
        let address = next as usize;
        if address < start || address.checked_add(size_of::<IP_ADAPTER_INFO>())? > end || !address.is_multiple_of(align_of::<IP_ADAPTER_INFO>()) { return None; }
        // SAFETY: checked range/alignment within retained initialized allocation;
        // copying metadata does not create Rust aliases into foreign list.
        let adapter = unsafe { next.read() };
        let bytes: Vec<u8> = adapter.AdapterName.iter().take_while(|b| **b != 0).map(|b| b.cast_unsigned()).collect(); let id = String::from_utf8_lossy(&bytes).to_ascii_lowercase();
        if !id.is_empty() && wanted.contains(&id) && adapter.AddressLength == 6 { return adapter.Address[..6].try_into().ok(); }
        if adapter.Next.is_null() { break; } next = adapter.Next;
    } None
}
