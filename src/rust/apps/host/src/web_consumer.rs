//! Typed Web composition. Legacy web_on remains a next-start SPA/history plan.
use crate::consumers::{Server, ServerHandle, Slot};
use icem_web::{WebServer, WebState};
use std::{net::SocketAddr, time::Instant};
pub struct Config {
    pub address: Option<SocketAddr>,
}
pub fn start(
    config: Config,
    state: WebState,
) -> Result<(Slot<WebServer>, ServerHandle<WebServer>), String> {
    let server = config
        .address
        .map(|address| WebServer::bind(address, state))
        .transpose()
        .map_err(|e| e.to_string())?;
    Ok(ServerHandle::new(server))
}
impl Server for WebServer {
    fn running(&self) -> bool {
        self.is_running()
    }
    fn cancel(&self) {
        self.request_stop();
    }
    fn stop_until(&mut self, deadline: Instant) -> Result<(), String> {
        self.stop_until(deadline).map_err(|e| e.to_string())
    }
}
