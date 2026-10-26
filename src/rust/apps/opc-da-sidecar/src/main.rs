#![forbid(unsafe_code)]
#[cfg(windows)]
mod windows;
fn main() {
    #[cfg(windows)]
    let result = windows::run();
    #[cfg(not(windows))]
    let result: Result<(), String> = Err("OPC DA sidecar requires Windows".into());
    if let Err(error) = result {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
