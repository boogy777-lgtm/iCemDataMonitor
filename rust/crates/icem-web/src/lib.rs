#![forbid(unsafe_code)]
//! Frozen dashboard, REST and gRPC-Web over the host's single-thread bus owner.
mod boot;
pub mod dashboard_client;
pub mod navigation;
pub use boot::BootStages;
pub mod log_control;
mod model;
pub mod registry;
mod rest;
mod rpc;
mod server;
pub use model::{
    BusReply, ConsoleRequest, HostLayers, HostSniffer, LayerRequest, ReadRequest, WebState,
};
pub use rest::router;
pub use server::WebServer;
// Generated from the unchanged canonical proto; these lint allowances apply only
// to upstream generator output, never handwritten handlers or application code.
#[allow(clippy::unwrap_used, clippy::expect_used)]
pub mod proto {
    tonic::include_proto!("icemdatamonitor.v1");
}

pub fn utc_now_ms() -> i64 {
    model::now_ms()
}

#[cfg(windows)]
mod pipe;
#[cfg(windows)]
pub use pipe::{HOST_PIPE, LocalRpcServer};
#[cfg(windows)]
pub mod client;
pub mod host_client;
