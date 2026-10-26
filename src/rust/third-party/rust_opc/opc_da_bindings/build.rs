fn main() -> Result<(), Box<dyn std::error::Error>> {
    let output = format!("{}/bindings.rs", std::env::var("OUT_DIR")?);
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=.windows/winmd/OPCDA.winmd");

    windows_bindgen::bindgen([
        "--in",
        ".windows/winmd/OPCDA.winmd",
        "default",
        "--out",
        output.as_str(),
        "--reference",
        "windows,skip-root,Windows",
        "--filter",
        "OPCDA",
        "--flat",
    ])
    .unwrap();

    remove_shallow_clones(
        &output,
        &[
            "tagOPCITEMATTRIBUTES",
            "tagOPCITEMPROPERTY",
            "tagOPCITEMSTATE",
            "tagOPCITEMVQT",
        ],
    );
    std::fs::write(
        format!("{}/bindings-module.rs", std::env::var("OUT_DIR")?),
        format!("#[path = {output:?}] mod bindings;"),
    )?;
    Ok(())
}

/// `windows-bindgen` currently emits `transmute_copy` Clone implementations for
/// explicitly laid-out structs containing `VARIANT`. A bitwise clone aliases the
/// BSTR/SAFEARRAY/interface owned by the variant and causes a double release.
fn remove_shallow_clones(path: &str, type_names: &[&str]) {
    let mut source = std::fs::read_to_string(path).unwrap();

    for type_name in type_names {
        let implementation = format!(
            "impl Clone for {type_name} {{\n    fn clone(&self) -> Self {{\n        unsafe {{ core::mem::transmute_copy(self) }}\n    }}\n}}\n"
        );
        source = source.replace(&implementation, "");
        assert!(
            !source.contains(&format!("impl Clone for {type_name}")),
            "unsafe shallow Clone implementation remains for {type_name}"
        );
    }

    std::fs::write(path, source).unwrap();
}
