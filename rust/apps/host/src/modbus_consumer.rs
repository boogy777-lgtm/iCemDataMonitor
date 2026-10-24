//! Typed Modbus composition; the writable mirror remains protocol-local.
use crate::consumers::{Server, ServerHandle, Slot};
use icem_protocols::modbus::RegisterBank;
use icem_transport::modbus_tcp::{ModbusServer, ServerOptions};
use std::{
    net::SocketAddr,
    sync::{Arc, Mutex},
    time::Instant,
};
pub struct Config {
    pub address: SocketAddr,
    pub enabled: bool,
    pub options: ServerOptions,
}
pub fn start(
    config: Config,
    bank: Arc<Mutex<RegisterBank>>,
) -> Result<(Slot<ModbusServer>, ServerHandle<ModbusServer>), String> {
    let server = if config.enabled {
        Some(ModbusServer::bind(config.address, bank, config.options).map_err(|e| e.to_string())?)
    } else {
        None
    };
    Ok(ServerHandle::new(server))
}
impl Server for ModbusServer {
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
