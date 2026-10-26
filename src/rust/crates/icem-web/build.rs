#![forbid(unsafe_code)]
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut prost = tonic_prost_build::Config::new();
    prost.protoc_executable(protoc_bin_vendored::protoc_bin_path()?);
    tonic_prost_build::configure()
        .build_client(true)
        .generate_default_stubs(true)
        .compile_with_config(
            prost,
            &["../../../iCemDataMonitor.Contracts/Protos/telemetry.proto"],
            &[
                "../../../iCemDataMonitor.Contracts/Protos",
                &protoc_bin_vendored::include_path()?.to_string_lossy(),
            ],
        )?;
    println!("cargo:rerun-if-changed=../../../iCemDataMonitor.Contracts/Protos/telemetry.proto");
    Ok(())
}
