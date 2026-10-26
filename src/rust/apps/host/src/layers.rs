//! Layer actions run only on the Host owner thread. Web is a next-start plan;
//! OPC DA is the legacy external no-op, not an in-process server.
use icem_config::IniOptions;
use icem_core::LayerKind;
use icem_platform_windows::sniffer::{Capture, CaptureDevice, State};
use icem_protocols::modbus::RegisterBank;
use icem_transport::modbus_tcp::{ModbusServer, ServerOptions};
use icem_web::WebState;
use std::{
    net::SocketAddr,
    path::PathBuf,
    sync::{Arc, Mutex},
};

pub fn toggle<D: CaptureDevice>(
    layer: LayerKind,
    server: &mut Option<ModbusServer>,
    bind: SocketAddr,
    bank: &Arc<Mutex<RegisterBank>>,
    web: &WebState,
    station: &mut Option<(PathBuf, IniOptions)>,
    sniffer: &mut Option<Capture<D>>,
) -> Result<(), &'static str> {
    // As in LayerRuntimeController, operation failures are LayerState.last_error,
    // not an RPC transport failure; the next operation clears the previous error.
    let result = apply(layer, server, bind, bank, web, station, sniffer);
    web.set_layer_error(result.err().map(|error| error.to_string()))
}
fn apply<D: CaptureDevice>(
    layer: LayerKind,
    server: &mut Option<ModbusServer>,
    bind: SocketAddr,
    bank: &Arc<Mutex<RegisterBank>>,
    web: &WebState,
    station: &mut Option<(PathBuf, IniOptions)>,
    sniffer: &mut Option<Capture<D>>,
) -> Result<(), Box<dyn std::error::Error>> {
    match layer {
        LayerKind::Modbus => {
            let operation = if let Some(listener) = server.as_mut() {
                listener
                    .stop_until(std::time::Instant::now() + std::time::Duration::from_secs(2))
                    .map(|()| {
                        *server = None;
                    })
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
        LayerKind::LanSniffer => {
            let operation = sniffer
                .as_mut()
                .ok_or_else(|| "sniffer capture owner is unavailable".to_owned())
                .and_then(Capture::toggle);
            publish_sniffer(sniffer, web)?;
            operation?;
        }
    }
    if let Some((path, settings)) = station
        && settings.ui_set_as_default == 1
    {
        let plan = web.layers()?;
        let mut updated = settings.clone();
        updated.modbus_on = i32::from(plan.modbus_bound);
        updated.web_on = i32::from(plan.web);
        if let Some(sniffer) = plan.sniffer {
            updated.lansniffer_on = i32::from(sniffer.enabled);
        }
        std::fs::write(path, updated.render())?;
        *settings = updated;
    }
    Ok(())
}
pub fn publish_sniffer<D: CaptureDevice>(
    sniffer: &Option<Capture<D>>,
    web: &WebState,
) -> Result<(), &'static str> {
    web.set_sniffer(sniffer.as_ref().map(|s| {
        let status = s.status();
        icem_web::HostSniffer {
            available: status.available,
            running: status.state == State::Running,
            enabled: status.enabled,
            state: status.state.name().into(),
            adapter: status.adapter,
            last_error: status.last_error,
            total_frames: status.total_frames,
            ace_frames: status.ace_frames,
            distinct_macs: status.distinct_macs,
            mac_limit_reached: status.mac_limit_reached,
            frames_per_sec: status.frames_per_sec,
            last_frame_age_sec: status.last_frame_age_sec,
        }
    }))
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
        sniffer: Option<Capture<icem_platform_windows::sniffer::SystemCapture>>,
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
                sniffer: None,
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
                &mut self.sniffer,
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
    struct Frames {
        fail: bool,
        frame: Option<Vec<u8>>,
    }
    impl CaptureDevice for Frames {
        fn open(&mut self, _: Option<&str>) -> Result<String, String> {
            Ok("test-nic".into())
        }
        fn next_packet(&mut self) -> Result<Option<Vec<u8>>, String> {
            if self.fail {
                Err("device unplugged".into())
            } else {
                Ok(self.frame.take())
            }
        }
        fn close(&mut self) {}
    }
    #[test]
    fn sniffer_capture_layer_persistence_and_actual_failure_projection() -> TestResult {
        for save in [false, true] {
            let mut f = Fixture::new(save)?;
            let mut capture = Some(Capture::new(
                Frames {
                    fail: false,
                    frame: Some(icem_protocols::ace::build([1; 6], 17, 100, &[0; 124]).to_vec()),
                },
                None,
                true,
            ));
            capture
                .as_mut()
                .ok_or("capture")?
                .poll(std::time::Duration::ZERO);
            publish_sniffer(&capture, &f.web)?;
            let s = f.web.layers()?.sniffer.ok_or("missing projection")?;
            assert!(s.available && s.running);
            assert_eq!((s.total_frames, s.ace_frames), (1, 1));
            for enabled in [false, true] {
                toggle(
                    LayerKind::LanSniffer,
                    &mut f.server,
                    f.bind,
                    &f.bank,
                    &f.web,
                    &mut f.station,
                    &mut capture,
                )?;
                let s = f.web.layers()?.sniffer.ok_or("missing projection")?;
                assert_eq!(s.running, enabled);
                assert!(s.available);
                assert_eq!(f.saved()?.lansniffer_on, i32::from(!save || enabled));
            }
            // Persistence failure preserves runtime state and the last-saved view.
            let path = f.root.join("config.ini");
            std::fs::remove_file(&path)?;
            std::fs::create_dir(&path)?;
            toggle(
                LayerKind::LanSniffer,
                &mut f.server,
                f.bind,
                &f.bank,
                &f.web,
                &mut f.station,
                &mut capture,
            )?;
            assert!(!f.web.layers()?.sniffer.ok_or("sniffer")?.running);
            assert_eq!(!f.web.layers()?.last_error.is_empty(), save);
            assert_eq!(f.station.as_ref().ok_or("station")?.1.lansniffer_on, 1);
            // Missing owner does not flip a flag or create producer-owned Data.dat.
            toggle(
                LayerKind::LanSniffer,
                &mut f.server,
                f.bind,
                &f.bank,
                &f.web,
                &mut f.station,
                &mut f.sniffer,
            )?;
            assert!(!f.web.layers()?.last_error.is_empty());
            assert!(!f.root.join("Data.dat").exists());
            assert!(path.is_dir());
        }
        let mut f = Fixture::new(false)?;
        let mut capture = Some(Capture::new(
            Frames {
                fail: true,
                frame: None,
            },
            None,
            true,
        ));
        capture
            .as_mut()
            .ok_or("capture")?
            .poll(std::time::Duration::ZERO);
        publish_sniffer(&capture, &f.web)?;
        let s = f.web.layers()?.sniffer.ok_or("sniffer")?;
        assert!(!s.running && !s.available);
        assert_eq!(s.last_error, "device unplugged");
        toggle(
            LayerKind::LanSniffer,
            &mut f.server,
            f.bind,
            &f.bank,
            &f.web,
            &mut f.station,
            &mut capture,
        )?;
        assert!(!f.web.layers()?.sniffer.ok_or("sniffer")?.running);
        assert_eq!(f.saved()?.lansniffer_on, 1);
        Ok(())
    }
}
