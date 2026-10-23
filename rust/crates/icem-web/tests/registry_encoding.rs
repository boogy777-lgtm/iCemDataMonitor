#[path = "../../../tests/support/text_encodings.rs"]
mod encodings;
use icem_web::registry::load_names;

#[test]
fn host_variable_names_use_text_file_decoding_once() -> Result<(), Box<dyn std::error::Error>> {
    struct Temp(std::path::PathBuf);
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }
    let file = Temp(
        std::env::temp_dir().join(format!("icem-web-registry-encoding-{}", std::process::id())),
    );
    let json = r#"{"version":1,"variables":[{"name":"Давление ° 😀","families":{"flecs":{"index":2},"lan":{"index":3}}}]}"#;
    for bytes in encodings::encoded_texts(json) {
        std::fs::write(&file.0, bytes)?;
        let names = load_names(&file.0)?;
        assert_eq!(names.len(), 2);
        assert_eq!(names.get(&20).map(String::as_str), Some("Давление ° 😀"));
        assert_eq!(names.get(&206).map(String::as_str), Some("Давление ° 😀"));
    }
    // Files with a BOM plus another U+FEFF retain the second as invalid JSON.
    for bytes in encodings::encoded_texts(&format!("\u{feff}{json}"))
        .into_iter()
        .skip(1)
    {
        std::fs::write(&file.0, bytes)?;
        assert!(load_names(&file.0).is_err());
    }
    Ok(())
}
