//! ADR 0003: dynamically loaded Npcap ABI, owned packet copies only.
use std::{
    ffi::{CStr, CString, c_char, c_int, c_void},
    ptr,
    sync::Arc,
};
use windows_sys::Win32::{
    Foundation::{FreeLibrary, HMODULE},
    NetworkManagement::IpHelper::{GetAdaptersInfo, IP_ADAPTER_INFO},
    System::{
        LibraryLoader::{
            GetProcAddress, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32,
            LoadLibraryExW,
        },
        SystemInformation::GetSystemDirectoryW,
    },
};
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Adapter {
    pub name: String,
    pub description: String,
}
#[repr(C)]
#[derive(Clone, Copy)]
struct PcapIf {
    next: *mut Self,
    name: *const c_char,
    description: *const c_char,
    addresses: *mut c_void,
    flags: u32,
}
#[repr(C)]
#[derive(Clone, Copy)]
struct PacketHeader {
    seconds: i32,
    microseconds: i32,
    caplen: u32,
    len: u32,
}
type FindAll = unsafe extern "C" fn(*mut *mut PcapIf, *mut c_char) -> c_int;
type FreeAll = unsafe extern "C" fn(*mut PcapIf);
type OpenLive =
    unsafe extern "C" fn(*const c_char, c_int, c_int, c_int, *mut c_char) -> *mut c_void;
type Close = unsafe extern "C" fn(*mut c_void);
type SendPacket = unsafe extern "C" fn(*mut c_void, *const u8, c_int) -> c_int;
type NextEx = unsafe extern "C" fn(*mut c_void, *mut *const PacketHeader, *mut *const u8) -> c_int;
type GetError = unsafe extern "C" fn(*mut c_void) -> *const c_char;
type DataLink = unsafe extern "C" fn(*mut c_void) -> c_int;
#[repr(C)]
struct BpfProgram {
    len: u32,
    instructions: *mut c_void,
}
type SetNonblock = unsafe extern "C" fn(*mut c_void, c_int, *mut c_char) -> c_int;
type Compile =
    unsafe extern "C" fn(*mut c_void, *mut BpfProgram, *const c_char, c_int, u32) -> c_int;
type SetFilter = unsafe extern "C" fn(*mut c_void, *mut BpfProgram) -> c_int;
type FreeCode = unsafe extern "C" fn(*mut BpfProgram);
type SetInt = unsafe extern "C" fn(*mut c_void, c_int) -> c_int;
struct CaptureApi {
    set_nonblock: SetNonblock,
    compile: Compile,
    set_filter: SetFilter,
    free_code: FreeCode,
    set_buffer: SetInt,
    set_user_buffer: SetInt,
    set_min_to_copy: SetInt,
}
impl CaptureApi {
    fn load(library: &Library) -> Result<Self, String> {
        let module = library.0 as HMODULE;
        macro_rules! symbol {
            ($name:literal, $ty:ty) => {{
                // SAFETY: owned loaded module and exact NUL-terminated ABI symbol.
                let proc = unsafe { GetProcAddress(module, concat!($name, "\0").as_ptr()) }
                    .ok_or_else(|| format!("Npcap missing export {}", $name))?;
                // SAFETY: concrete signature matches the named Npcap C ABI export.
                unsafe { std::mem::transmute::<unsafe extern "system" fn() -> isize, $ty>(proc) }
            }};
        }
        Ok(Self {
            set_nonblock: symbol!("pcap_setnonblock", SetNonblock),
            compile: symbol!("pcap_compile", Compile),
            set_filter: symbol!("pcap_setfilter", SetFilter),
            free_code: symbol!("pcap_freecode", FreeCode),
            set_buffer: symbol!("pcap_setbuff", SetInt),
            set_user_buffer: symbol!("pcap_setuserbuffer", SetInt),
            set_min_to_copy: symbol!("pcap_setmintocopy", SetInt),
        })
    }
}
struct Library(usize);
impl Drop for Library {
    fn drop(&mut self) {
        // SAFETY: unique LoadLibraryExW reference; all sessions retain Arc<Pcap>
        // and therefore prevent unloading while any ABI calls are possible.
        unsafe {
            FreeLibrary(self.0 as HMODULE);
        }
    }
}
pub struct Pcap {
    _library: Option<Library>,
    find_all: FindAll,
    free_all: FreeAll,
    open_live: OpenLive,
    close: Close,
    send_packet: SendPacket,
    next_ex: NextEx,
    get_error: GetError,
    datalink: DataLink,
}
fn dll_paths() -> Result<Vec<Vec<u16>>, String> {
    let mut path = vec![0u16; 32768];
    // SAFETY: owned UTF-16 buffer of stated capacity, Win32 retains no pointer.
    let count = unsafe { GetSystemDirectoryW(path.as_mut_ptr(), path.len() as u32) } as usize;
    if count == 0 || count >= path.len() {
        return Err("Cannot locate Windows system directory".into());
    }
    path.truncate(count);
    Ok([r"\Npcap\wpcap.dll", r"\wpcap.dll"]
        .into_iter()
        .map(|s| {
            path.iter()
                .copied()
                .chain(s.encode_utf16())
                .chain([0])
                .collect()
        })
        .collect())
}
pub fn npcap_available() -> bool {
    dll_paths().is_ok_and(|paths| {
        paths
            .into_iter()
            .any(|p| std::path::Path::new(&String::from_utf16_lossy(&p[..p.len() - 1])).is_file())
    })
}
impl Pcap {
    pub fn load() -> Result<Arc<Self>, String> {
        let mut loaded = ptr::null_mut();
        for path in dll_paths()? {
            // SAFETY: absolute System32 path, dependency search restricted to DLL
            // directory and System32; no application/CWD/PATH DLL loading.
            loaded = unsafe {
                LoadLibraryExW(
                    path.as_ptr(),
                    ptr::null_mut(),
                    LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32,
                )
            };
            if !loaded.is_null() {
                break;
            }
        }
        if loaded.is_null() {
            return Err(format!(
                "Npcap not found or unavailable: {}",
                std::io::Error::last_os_error()
            ));
        }
        let library = Library(loaded as usize);
        macro_rules! symbol {
            ($name:literal, $ty:ty) => {{
                // SAFETY: loaded module retained by owner; static NUL-terminated
                // ASCII name denotes this exact documented Npcap C ABI symbol.
                let proc = unsafe { GetProcAddress(loaded, concat!($name, "\0").as_ptr()) }
                    .ok_or_else(|| format!("Npcap missing export {}", $name))?;
                // SAFETY: concrete function signature exactly matches named Npcap
                // export; no generic/untyped code pointer escapes native module.
                unsafe { std::mem::transmute::<unsafe extern "system" fn() -> isize, $ty>(proc) }
            }};
        }
        let find_all = symbol!("pcap_findalldevs", FindAll);
        let free_all = symbol!("pcap_freealldevs", FreeAll);
        let open_live = symbol!("pcap_open_live", OpenLive);
        let close = symbol!("pcap_close", Close);
        let send_packet = symbol!("pcap_sendpacket", SendPacket);
        let next_ex = symbol!("pcap_next_ex", NextEx);
        let get_error = symbol!("pcap_geterr", GetError);
        let datalink = symbol!("pcap_datalink", DataLink);
        Ok(Arc::new(Self {
            _library: Some(library),
            find_all,
            free_all,
            open_live,
            close,
            send_packet,
            next_ex,
            get_error,
            datalink,
        }))
    }
    pub fn adapters(&self) -> Result<Vec<Adapter>, String> {
        let mut list = ptr::null_mut();
        let mut error = [0i8; 256];
        // SAFETY: local outputs; successful list stays allocated until guarded free.
        if unsafe { (self.find_all)(&mut list, error.as_mut_ptr()) } != 0 {
            return Err(error_buffer(&error));
        }
        struct Devices<'a> {
            pointer: *mut PcapIf,
            owner: &'a Pcap,
        }
        impl Drop for Devices<'_> {
            fn drop(&mut self) {
                // SAFETY: exactly one free for successful findalldevs-owned list.
                unsafe {
                    (self.owner.free_all)(self.pointer);
                }
            }
        }
        let _devices = Devices {
            pointer: list,
            owner: self,
        };
        let mut result = Vec::new();
        while !list.is_null() && result.len() < 4096 {
            // SAFETY: library-produced immutable list retained by local guard;
            // copy metadata, never form Rust references into foreign allocation.
            let item = unsafe { list.read() };
            // SAFETY: documented Npcap optional NUL-terminated list strings stay
            // valid until freealldevs; both strings copied before free.
            let (name, description) = unsafe { (c_string(item.name), c_string(item.description)) };
            result.push(Adapter { name, description });
            list = item.next;
        }
        if !list.is_null() {
            return Err("Npcap adapter list exceeds 4096 entries".into());
        }
        Ok(result)
    }
    /// Capture-only API: nonblocking next_ex, kernel multicast filter, no send method.
    /// ADR 0007. All setup failures close the unique session through RAII.
    pub fn open_capture(self: &Arc<Self>, name: &str) -> Result<CaptureSession, String> {
        let api = CaptureApi::load(self._library.as_ref().ok_or("Npcap module unavailable")?)?;
        self.open_capture_with(name, api, 65535, 100)
    }
    /// Standalone emulator diagnostic; Host capture configuration is unchanged.
    pub fn open_diagnostic_capture(self: &Arc<Self>, name: &str) -> Result<CaptureSession, String> {
        let api = CaptureApi::load(self._library.as_ref().ok_or("Npcap module unavailable")?)?;
        self.open_capture_with(name, api, 200, 500)
    }
    fn open_capture_with(
        self: &Arc<Self>,
        name: &str,
        api: CaptureApi,
        snaplen: i32,
        timeout: i32,
    ) -> Result<CaptureSession, String> {
        let session = self.open_with(name, true, snaplen, timeout)?;
        for (set, value) in [
            (api.set_buffer, 64 * 1024),
            (api.set_user_buffer, 64 * 1024),
            (api.set_min_to_copy, 1),
        ] {
            // SAFETY: uniquely owned active pcap_t and documented bounded integer option.
            if unsafe { set(session.handle as *mut c_void, value) } != 0 {
                return Err(session.error());
            }
        }
        let mut error = [0i8; 256];
        // SAFETY: unique live pcap_t, writable PCAP_ERRBUF_SIZE buffer; no pointer retained.
        if unsafe { (api.set_nonblock)(session.handle as *mut c_void, 1, error.as_mut_ptr()) } != 0
        {
            return Err(format!("pcap_setnonblock failed: {}", error_buffer(&error)));
        }
        let mut program = BpfProgram {
            len: 0,
            instructions: ptr::null_mut(),
        };
        // SAFETY: live handle and initialized C-layout output; static NUL string,
        // PCAP_NETMASK_UNKNOWN (filter does not inspect IPv4 netmask).
        if unsafe {
            (api.compile)(
                session.handle as *mut c_void,
                &mut program,
                c"ether multicast".as_ptr(),
                1,
                u32::MAX,
            )
        } != 0
        {
            return Err(session.error());
        }
        // SAFETY: successful compile owns program until freecode; setfilter copies
        // the program into the session. Both calls are synchronous on its owner.
        let result = unsafe { (api.set_filter)(session.handle as *mut c_void, &mut program) };
        // SAFETY: exactly one free for the successful pcap_compile allocation.
        unsafe {
            (api.free_code)(&mut program);
        }
        if result != 0 {
            return Err(session.error());
        }
        Ok(CaptureSession(session))
    }
    pub fn open(self: &Arc<Self>, name: &str, promiscuous: bool) -> Result<PacketSession, String> {
        self.open_with(name, promiscuous, 65535, 100)
    }
    fn open_with(
        self: &Arc<Self>,
        name: &str,
        promiscuous: bool,
        snaplen: i32,
        timeout: i32,
    ) -> Result<PacketSession, String> {
        let name = CString::new(name).map_err(|_| "Adapter name contains NUL")?;
        let mut error = [0i8; 256];
        // SAFETY: stable CString/local error buffer, bounded internal snaplen and finite
        // read timeout; returned pcap_t uniquely owned on success.
        let raw = unsafe {
            (self.open_live)(
                name.as_ptr(),
                snaplen,
                i32::from(promiscuous),
                timeout,
                error.as_mut_ptr(),
            )
        };
        if raw.is_null() {
            return Err(error_buffer(&error));
        }
        let session = PacketSession {
            handle: raw as usize,
            owner: Arc::clone(self),
        };
        // SAFETY: live uniquely owned pcap_t used only for read-only ABI query.
        if unsafe { (self.datalink)(raw) } != 1 {
            return Err("Selected adapter is not Ethernet (DLT_EN10MB required)".into());
        }
        Ok(session)
    }
}
/// Uniquely owned session, movable without unsafe Send/Sync implementations.
/// All mutating ABI calls require exclusive borrow; no raw pointers escape.
pub struct PacketSession {
    handle: usize,
    owner: Arc<Pcap>,
}
pub struct CaptureSession(PacketSession);
impl CaptureSession {
    pub fn next_packet(&mut self) -> Result<Option<Vec<u8>>, String> {
        self.0.next_packet()
    }
}
impl PacketSession {
    pub fn send(&mut self, bytes: &[u8]) -> Result<(), String> {
        if bytes.is_empty() || bytes.len() > 65535 {
            return Err("Packet size must be 1..65535".into());
        }
        // SAFETY: exclusive live session, borrowed immutable buffer lives until
        // synchronous send returns; its bounded length fits c_int.
        if unsafe {
            (self.owner.send_packet)(
                self.handle as *mut c_void,
                bytes.as_ptr(),
                bytes.len() as i32,
            )
        } != 0
        {
            return Err(self.error());
        }
        Ok(())
    }
    pub fn next_packet(&mut self) -> Result<Option<Vec<u8>>, String> {
        let mut header = ptr::null();
        let mut packet = ptr::null();
        // SAFETY: exclusive live session, valid local output pointers; returned
        // packet/header remain readable until next read, copied before then.
        match unsafe { (self.owner.next_ex)(self.handle as *mut c_void, &mut header, &mut packet) }
        {
            0 => Ok(None),
            1 => {
                if header.is_null() || packet.is_null() {
                    return Err("Npcap returned null packet".into());
                }
                // SAFETY: successful next_ex promises readable packet metadata.
                let header = unsafe { header.read() };
                if header.caplen > 65535 || header.caplen > header.len {
                    return Err("Npcap returned invalid captured size".into());
                }
                let mut owned = vec![0u8; header.caplen as usize];
                // SAFETY: library promises caplen readable bytes; uniquely owned
                // destination separate allocation, no borrowed packet escapes.
                unsafe {
                    ptr::copy_nonoverlapping(packet, owned.as_mut_ptr(), owned.len());
                }
                Ok(Some(owned))
            }
            -2 => Err("Npcap capture stopped".into()),
            _ => Err(self.error()),
        }
    }
    fn error(&self) -> String {
        // SAFETY: live session provides NUL-terminated error valid until next
        // operation; copied immediately, only called under exclusive operation.
        unsafe { c_string((self.owner.get_error)(self.handle as *mut c_void)) }
    }
}
impl Drop for PacketSession {
    fn drop(&mut self) {
        // SAFETY: unique successful open handle; Arc keeps DLL loaded through close.
        unsafe {
            (self.owner.close)(self.handle as *mut c_void);
        }
    }
}
unsafe fn c_string(pointer: *const c_char) -> String {
    if pointer.is_null() {
        return String::new();
    }
    // SAFETY: caller guarantees a live, NUL-terminated foreign string.
    unsafe { CStr::from_ptr(pointer) }
        .to_string_lossy()
        .into_owned()
}
fn error_buffer(buffer: &[i8; 256]) -> String {
    let bytes: Vec<u8> = buffer
        .iter()
        .take_while(|b| **b != 0)
        .map(|b| b.cast_unsigned())
        .collect();
    String::from_utf8_lossy(&bytes).into_owned()
}
pub fn adapter_mac(name: &str) -> Option<[u8; 6]> {
    let mut size = 0;
    // SAFETY: null first-call query with initialized size output.
    unsafe {
        GetAdaptersInfo(ptr::null_mut(), &mut size);
    }
    if size == 0 || size > 1_048_576 {
        return None;
    }
    let count = (size as usize).div_ceil(size_of::<IP_ADAPTER_INFO>());
    let mut buffer = vec![IP_ADAPTER_INFO::default(); count];
    // SAFETY: aligned initialized allocation >= requested bytes, exclusively
    // writable during call, OS links retained only inside this allocation.
    if unsafe { GetAdaptersInfo(buffer.as_mut_ptr(), &mut size) } != 0 {
        return None;
    }
    let start = buffer.as_ptr() as usize;
    let end = start + buffer.len() * size_of::<IP_ADAPTER_INFO>();
    let mut next = buffer.as_ptr();
    let wanted = name.to_ascii_lowercase();
    for _ in 0..4096 {
        let address = next as usize;
        if address < start
            || address.checked_add(size_of::<IP_ADAPTER_INFO>())? > end
            || !address.is_multiple_of(align_of::<IP_ADAPTER_INFO>())
        {
            return None;
        }
        // SAFETY: checked range/alignment within retained initialized allocation;
        // copying metadata does not create Rust aliases into foreign list.
        let adapter = unsafe { next.read() };
        let bytes: Vec<u8> = adapter
            .AdapterName
            .iter()
            .take_while(|b| **b != 0)
            .map(|b| b.cast_unsigned())
            .collect();
        let id = String::from_utf8_lossy(&bytes).to_ascii_lowercase();
        if !id.is_empty() && wanted.contains(&id) && adapter.AddressLength == 6 {
            return adapter.Address[..6].try_into().ok();
        }
        if adapter.Next.is_null() {
            break;
        }
        next = adapter.Next;
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    #[derive(Default)]
    struct Trace {
        fail: u8,
        close: usize,
        free: usize,
        nonblock: i32,
        promiscuous: i32,
        filter: String,
        buffers: [i32; 3],
        snaplen: i32,
        timeout: i32,
    }
    thread_local! { static TRACE: RefCell<Trace> = RefCell::new(Trace::default()); }
    unsafe extern "C" fn open(
        _: *const c_char,
        snaplen: i32,
        promiscuous: i32,
        timeout: i32,
        _: *mut c_char,
    ) -> *mut c_void {
        TRACE.with_borrow_mut(|t| {
            t.promiscuous = promiscuous;
            t.snaplen = snaplen;
            t.timeout = timeout;
        });
        // Opaque sentinel is never dereferenced by these ABI shims.
        ptr::NonNull::<u8>::dangling().as_ptr().cast()
    }
    unsafe extern "C" fn close(_: *mut c_void) {
        TRACE.with_borrow_mut(|t| t.close += 1);
    }
    unsafe extern "C" fn buffer(_: *mut c_void, size: i32) -> i32 {
        TRACE.with_borrow_mut(|t| {
            t.buffers[0] = size;
            if t.fail == 4 { -1 } else { 0 }
        })
    }
    unsafe extern "C" fn user_buffer(_: *mut c_void, size: i32) -> i32 {
        TRACE.with_borrow_mut(|t| {
            t.buffers[1] = size;
            if t.fail == 5 { -1 } else { 0 }
        })
    }
    unsafe extern "C" fn min_to_copy(_: *mut c_void, size: i32) -> i32 {
        TRACE.with_borrow_mut(|t| {
            t.buffers[2] = size;
            if t.fail == 6 { -1 } else { 0 }
        })
    }
    unsafe extern "C" fn datalink(_: *mut c_void) -> i32 {
        1
    }
    unsafe extern "C" fn nonblock(_: *mut c_void, mode: i32, _: *mut c_char) -> i32 {
        TRACE.with_borrow_mut(|t| {
            t.nonblock = mode;
            if t.fail == 1 { -1 } else { 0 }
        })
    }
    unsafe extern "C" fn compile(
        _: *mut c_void,
        _: *mut BpfProgram,
        filter: *const c_char,
        _: i32,
        _: u32,
    ) -> i32 {
        // SAFETY: production open_capture passes the static NUL-terminated filter.
        let filter = unsafe { CStr::from_ptr(filter) }
            .to_string_lossy()
            .into_owned();
        TRACE.with_borrow_mut(|t| {
            t.filter = filter;
            if t.fail == 2 { -1 } else { 0 }
        })
    }
    unsafe extern "C" fn filter(_: *mut c_void, _: *mut BpfProgram) -> i32 {
        TRACE.with_borrow(|t| if t.fail == 3 { -1 } else { 0 })
    }
    unsafe extern "C" fn free(_: *mut BpfProgram) {
        TRACE.with_borrow_mut(|t| t.free += 1);
    }
    unsafe extern "C" fn error(_: *mut c_void) -> *const c_char {
        c"driver fault".as_ptr()
    }
    unsafe extern "C" fn next(
        _: *mut c_void,
        _: *mut *const PacketHeader,
        _: *mut *const u8,
    ) -> i32 {
        0
    }
    unsafe extern "C" fn send(_: *mut c_void, _: *const u8, _: i32) -> i32 {
        -1
    }
    unsafe extern "C" fn find(_: *mut *mut PcapIf, _: *mut c_char) -> i32 {
        -1
    }
    unsafe extern "C" fn free_all(_: *mut PcapIf) {}
    #[test]
    fn capture_setup_fails_closed_and_frees_filter_exactly_once() -> Result<(), String> {
        for (snaplen, timeout) in [(65535, 100), (200, 500)] {
            for fail in 0..=6 {
                TRACE.with_borrow_mut(|t| {
                    *t = Trace {
                        fail,
                        ..Trace::default()
                    }
                });
                let pcap = Arc::new(Pcap {
                    _library: None,
                    find_all: find,
                    free_all,
                    open_live: open,
                    close,
                    send_packet: send,
                    next_ex: next,
                    get_error: error,
                    datalink,
                });
                let result = pcap.open_capture_with(
                    "test",
                    CaptureApi {
                        set_nonblock: nonblock,
                        compile,
                        set_filter: filter,
                        free_code: free,
                        set_buffer: buffer,
                        set_user_buffer: user_buffer,
                        set_min_to_copy: min_to_copy,
                    },
                    snaplen,
                    timeout,
                );
                if fail == 0 {
                    let mut session = result?;
                    assert_eq!(session.next_packet()?, None);
                    TRACE.with_borrow(|t| assert_eq!(t.close, 0));
                    drop(pcap); // session retains the loaded owner
                    drop(session);
                } else {
                    assert!(result.is_err());
                }
                TRACE.with_borrow(|t| {
                    assert_eq!(t.close, 1);
                    assert_eq!(t.free, usize::from(fail == 0 || fail == 3));
                    assert_eq!(t.nonblock, i32::from(fail < 4));
                    assert_eq!(t.buffers[0], 65536);
                    if fail != 4 {
                        assert_eq!(t.buffers[1], 65536);
                    }
                    if !matches!(fail, 4 | 5) {
                        assert_eq!(t.buffers[2], 1);
                    }
                    assert_eq!(t.promiscuous, 1);
                    assert_eq!((t.snaplen, t.timeout), (snaplen, timeout));
                    if fail != 1 && fail < 4 {
                        assert_eq!(t.filter, "ether multicast");
                    }
                });
            }
        }
        Ok(())
    }
}
