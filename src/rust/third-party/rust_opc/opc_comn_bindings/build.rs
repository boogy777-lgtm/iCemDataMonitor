fn main() -> Result<(), Box<dyn std::error::Error>> {
    let output = format!("{}/bindings.rs", std::env::var("OUT_DIR")?);
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=.windows/winmd/OPCCOMN.winmd");

    windows_bindgen::bindgen([
        "--in",
        ".windows/winmd/OPCCOMN.winmd",
        "default",
        "--out",
        output.as_str(),
        "--reference",
        "windows,skip-root,Windows",
        "--filter",
        "OPCCOMN",
        "--flat",
    ])
    .unwrap();
    std::fs::write(
        format!("{}/bindings-module.rs", std::env::var("OUT_DIR")?),
        format!("#[path = {output:?}] mod bindings;"),
    )?;
    Ok(())
}
