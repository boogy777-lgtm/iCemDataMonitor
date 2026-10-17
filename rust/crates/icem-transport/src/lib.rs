#![forbid(unsafe_code)]
//! Frozen external API registry. HTTP/gRPC handlers are M4 work.
pub const HOST_PIPE: &str = "iCemDataMonitor.Host";
pub const TELEMETRY_PROTO: &str = include_str!("../../../contracts/proto/telemetry.proto");
pub const REST_GET_PATHS: &[&str] = &[
    "/api/status",
    "/api/status/summary",
    "/api/regs",
    "/api/regs/history",
];

pub mod emulator_control;
pub mod modbus_tcp;
