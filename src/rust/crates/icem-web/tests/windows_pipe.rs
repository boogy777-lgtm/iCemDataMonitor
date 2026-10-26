#![cfg(windows)]
use icem_web::{BusReply, HostLayers, LocalRpcServer, WebState, client, proto::*};
use std::{collections::BTreeMap, time::Duration};
#[test]
fn original_grpc_over_pipe_roundtrip_exclusive_bind_and_rebind()
-> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let name = format!("icem-host-grpc-test-{}", std::process::id());
    let theme = std::env::temp_dir().join(format!("icem-pipe-theme-{}", std::process::id()));
    let (state, requests) = WebState::new(
        HostLayers {
            modbus_port: 1502,
            ..Default::default()
        },
        theme.clone(),
        BTreeMap::new(),
    );
    let owner = std::thread::spawn(move || {
        while let Ok(request) = requests.recv_timeout(Duration::from_secs(10)) {
            let _ = request.response.send(BusReply {
                read: None,
                is_open: false,
            });
        }
    });
    for _ in 0..4 {
        let server = LocalRpcServer::bind(&name, state.clone())?;
        assert!(LocalRpcServer::bind(&name, state.clone()).is_err());
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()?;
        runtime.block_on(async {
            let mut client = client::connect(&name).await?;
            let status = client.get_status(HostStatusRequest {}).await?.into_inner();
            assert!(!status.bus_open);
            assert_eq!(status.layers.ok_or("missing layers")?.modbus_port, 1502);
            let prefs = client
                .set_ui_theme(SetUiThemeRequest { theme_light: true })
                .await?
                .into_inner();
            assert!(prefs.theme_light);
            assert_eq!(std::fs::read_to_string(&theme)?, "light");
            let error = client
                .get_registers(RegistersRequest { zone: 0 })
                .await
                .err()
                .ok_or("expected unavailable")?;
            assert_eq!(error.code(), tonic::Code::Unavailable);
            Ok::<(), Box<dyn std::error::Error + Send + Sync>>(())
        })?;
        server
            .stop()
            .map_err(|e| std::io::Error::new(e.kind(), format!("stop first server: {e}")))?;
        // Channel background tasks live on this runtime beyond the RPC future. Close
        // their handles before asserting FIRST_PIPE_INSTANCE on the same kernel name.
        drop(runtime);
        LocalRpcServer::bind(&name, state.clone())
            .map_err(|e| {
                std::io::Error::new(e.kind(), format!("rebind after both owners closed: {e}"))
            })?
            .stop()?;
    }
    drop(state);
    owner.join().map_err(|_| "owner panic")?;
    std::fs::remove_file(theme)?;
    Ok(())
}
