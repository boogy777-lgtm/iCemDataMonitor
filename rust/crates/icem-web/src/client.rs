//! Windows local gRPC client. Connect timeout is finite; mutating RPCs are never retried.
use crate::proto::telemetry_service_client::TelemetryServiceClient;
use std::time::Duration;
use tonic::transport::{Channel, Endpoint};
pub async fn connect(
    name: &str,
) -> Result<TelemetryServiceClient<Channel>, Box<dyn std::error::Error + Send + Sync>> {
    if name.is_empty() || name.contains(['\\', '/', '\0']) {
        return Err("invalid local pipe name".into());
    }
    let path = format!(r"\\.\pipe\{name}");
    let channel = Endpoint::from_static("http://localhost")
        .connect_timeout(Duration::from_secs(3))
        .timeout(Duration::from_secs(5))
        .connect_with_connector(tower::service_fn(move |_: tonic::codegen::http::Uri| {
            let path = path.clone();
            async move {
                let pipe = tokio::net::windows::named_pipe::ClientOptions::new().open(path)?;
                Ok::<_, std::io::Error>(hyper_util::rt::TokioIo::new(pipe))
            }
        }))
        .await?;
    Ok(TelemetryServiceClient::new(channel))
}
