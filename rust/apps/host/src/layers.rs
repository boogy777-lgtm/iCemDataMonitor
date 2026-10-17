//! Layer actions run only on the Host owner thread. Web is a next-start plan;
//! OPC DA is the legacy external no-op, not an in-process server.
use icem_config::IniOptions;
use icem_core::LayerKind;
use icem_protocols::modbus::RegisterBank;
use icem_transport::modbus_tcp::{ModbusServer, ServerOptions};
use icem_web::WebState;
use std::{
    net::SocketAddr,
    path::PathBuf,
    sync::{Arc, Mutex},
};

pub fn toggle(
    layer: LayerKind,
    server: &mut Option<ModbusServer>,
    bind: SocketAddr,
    bank: &Arc<Mutex<RegisterBank>>,
    web: &WebState,
    station: &mut Option<(PathBuf, IniOptions)>,
) -> Result<(), &'static str> {
    // As in LayerRuntimeController, operation failures are LayerState.last_error,
    // not an RPC transport failure; the next operation clears the previous error.
    let result = apply(layer, server, bind, bank, web, station);
    web.set_layer_error(result.err().map(|error| error.to_string()))
}
fn apply(
    layer: LayerKind,
    server: &mut Option<ModbusServer>,
    bind: SocketAddr,
    bank: &Arc<Mutex<RegisterBank>>,
    web: &WebState,
    station: &mut Option<(PathBuf, IniOptions)>,
) -> Result<(), Box<dyn std::error::Error>> {
    match layer {
        LayerKind::Modbus => {
            let operation = if let Some(listener) = server.take() {
                listener.stop().map(|_| ())
            } else {
                ModbusServer::bind(bind, Arc::clone(bank), ServerOptions::default())
                    .map(|listener| *server = Some(listener))
            };
            // Always publish actual socket ownership, including failed bind/stop.
            web.set_modbus_bound(server.is_some())?;
            operation?;
        }
        LayerKind::Web => web.toggle_web_plan()?,
        LayerKind::OpcDa => return Ok(()),
        LayerKind::LanSniffer => return Err("sniffer capture owner is not implemented".into()),
    }
    if let Some((path, settings)) = station
        && settings.ui_set_as_default == 1
    {
        let plan = web.layers()?;
        let mut updated = settings.clone();
        updated.modbus_on = i32::from(plan.modbus_bound);
        updated.web_on = i32::from(plan.web);
        // OPC and sniffer flags are startup values until their owners exist.
        std::fs::write(path, updated.render())?;
        *settings = updated;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use icem_web::HostLayers;
    use std::{
        collections::BTreeMap,
        net::{TcpListener, TcpStream},
    };
    type TestResult = Result<(), Box<dyn std::error::Error>>;
    struct Fixture {
        root: PathBuf,
        web: WebState,
        station: Option<(PathBuf, IniOptions)>,
        bank: Arc<Mutex<RegisterBank>>,
        server: Option<ModbusServer>,
        bind: SocketAddr,
    }
    impl Fixture {
        fn new(save: bool) -> Result<Self, Box<dyn std::error::Error>> {
            static NEXT: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
            let root = std::env::temp_dir().join(format!(
                "icem-layer-owner-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
            ));
            std::fs::create_dir(&root)?;
            let settings = IniOptions {
                modbus_on: 0,
                opcda_on: 1,
                ui_set_as_default: i32::from(save),
                ..IniOptions::default()
            };
            let path = root.join("config.ini");
            std::fs::write(&path, settings.render())?;
            let (web, _) = WebState::new(
                HostLayers {
                    web: true,
                    opc_da: true,
                    set_as_default: save,
                    ..HostLayers::default()
                },
                root.join("ui-theme"),
                BTreeMap::new(),
            );
            Ok(Self {
                root,
                web,
                station: Some((path, settings)),
                bank: Arc::new(Mutex::new(
                    RegisterBank::new(650).map_err(|e| format!("{e:?}"))?,
                )),
                server: None,
                bind: "127.0.0.1:0".parse()?,
            })
        }
        fn toggle(&mut self, layer: LayerKind) -> TestResult {
            toggle(
                layer,
                &mut self.server,
                self.bind,
                &self.bank,
                &self.web,
                &mut self.station,
            )?;
            Ok(())
        }
        fn saved(&self) -> Result<IniOptions, Box<dyn std::error::Error>> {
            IniOptions::read_existing(&self.root.join("config.ini"))
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.root);
        }
    }
    #[test]
    fn web_session_plan_persistence_gate_and_opc_noop() -> TestResult {
        for save in [false, true] {
            let mut f = Fixture::new(save)?;
            let original = std::fs::read(f.root.join("config.ini"))?;
            f.toggle(LayerKind::Web)?;
            assert!(!f.web.layers()?.web);
            assert!(f.web.layers()?.last_error.is_empty());
            assert_eq!(f.saved()?.web_on, i32::from(!save));
            f.toggle(LayerKind::OpcDa)?;
            assert!(f.web.layers()?.opc_da);
            assert_eq!(f.saved()?.opcda_on, 1);
            assert_eq!(f.saved()?.lansniffer_on, 1);
            if !save {
                assert_eq!(std::fs::read(f.root.join("config.ini"))?, original);
            }
            f.toggle(LayerKind::Web)?;
            assert!(f.web.layers()?.web);
            assert_eq!(f.saved()?.web_on, 1);
        }
        Ok(())
    }
    #[test]
    fn failed_save_keeps_session_plan_and_last_saved_projection() -> TestResult {
        let mut f = Fixture::new(true)?;
        // A directory at the target path fails on both elevated Windows and root Linux.
        let path = f.root.join("config.ini");
        std::fs::remove_file(&path)?;
        std::fs::create_dir(&path)?;
        f.toggle(LayerKind::Web)?;
        assert!(!f.web.layers()?.web);
        assert!(!f.web.layers()?.last_error.is_empty());
        assert_eq!(f.station.as_ref().ok_or("missing station")?.1.web_on, 1);
        std::fs::remove_dir(&path)?;
        f.toggle(LayerKind::OpcDa)?;
        assert!(f.web.layers()?.last_error.is_empty());
        assert!(!path.exists()); // external no-op must not persist another layer's plan
        f.toggle(LayerKind::Modbus)?;
        let address = f.server.as_ref().ok_or("no listener")?.local_addr();
        assert!(TcpStream::connect(address).is_ok());
        assert!(f.web.layers()?.modbus_bound);
        assert_eq!(f.saved()?.web_on, 0); // aggregate plan, not just the latest arm
        assert_eq!(f.saved()?.modbus_on, 1);
        f.toggle(LayerKind::Modbus)?;
        assert!(!f.web.layers()?.modbus_bound);
        assert_eq!(f.saved()?.modbus_on, 0);
        assert!(TcpListener::bind(address).is_ok());
        Ok(())
    }
    #[test]
    fn failed_bind_reports_actual_state_without_persistence() -> TestResult {
        let mut f = Fixture::new(true)?;
        let occupied = TcpListener::bind("127.0.0.1:0")?;
        f.bind = occupied.local_addr()?;
        f.toggle(LayerKind::Modbus)?;
        assert!(!f.web.layers()?.modbus_bound);
        assert!(!f.web.layers()?.last_error.is_empty());
        assert_eq!(f.saved()?.modbus_on, 0);
        drop(occupied);
        f.toggle(LayerKind::Modbus)?;
        assert!(f.web.layers()?.modbus_bound);
        assert!(f.web.layers()?.last_error.is_empty());
        Ok(())
    }
}
