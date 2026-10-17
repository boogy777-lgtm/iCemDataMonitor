//! GPUI Tray adapter shared by the independently owned Host and emulator trays.
use gpui::{App, Application, MenuItem, actions};
use gpui_tray::{Icon, Tray};
use icem_core::HostCommand;
use std::{
    io,
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::Duration,
};

actions!(
    icem_host_tray,
    [OpenPanel, OpenWeb, ToggleConsole, Restart, Close]
);
actions!(icem_emulator_tray, [OpenEmulator, ExitEmulator]);
fn icon(path: PathBuf) -> io::Result<Icon> {
    let image = image::ImageReader::open(path)?
        .with_guessed_format()?
        .decode()
        .map_err(io::Error::other)?
        .into_rgba8();
    let (width, height) = image.dimensions();
    Icon::from_rgba(image.into_raw(), width, height).map_err(io::Error::other)
}
pub(crate) fn emulator_tray(path: PathBuf, cx: &mut App) -> io::Result<Tray> {
    Tray::builder()
        .icon(icon(path)?)
        .title("iCemEmulator")
        .tooltip("iCemEmulator")
        .on_activate(OpenEmulator)
        .menu(|_| {
            vec![
                MenuItem::action("Открыть iCemEmulator", OpenEmulator),
                MenuItem::action("Выход", ExitEmulator),
            ]
        })
        .build(cx)
        .map_err(io::Error::other)
}
/// A windowless GPUI application owns the Host tray on one foreground thread.
/// The Host's bus reader/mutex and service loop stay on their original thread.
pub struct HostTray {
    stopping: Arc<AtomicBool>,
    commands: mpsc::Receiver<HostCommand>,
    worker: Option<JoinHandle<io::Result<()>>>,
}
impl HostTray {
    pub fn start(path: PathBuf) -> io::Result<Self> {
        let icon = icon(path)?;
        let stopping = Arc::new(AtomicBool::new(false));
        let stop = stopping.clone();
        let (sender, commands) = mpsc::sync_channel(16);
        let (ready, wait) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("host-gpui-tray".into())
            .spawn(move || {
                let result = std::rc::Rc::new(std::cell::RefCell::new(Ok(())));
                let status = result.clone();
                Application::new().run(move |cx: &mut App| {
                    let tray = match Tray::builder()
                        .icon(icon)
                        .title("iCem DataMonitor")
                        .tooltip("iCem DataMonitor")
                        .on_activate(OpenPanel)
                        .menu(|_| {
                            vec![
                                MenuItem::action("Открыть панель", OpenPanel),
                                MenuItem::action("Открыть Web", OpenWeb),
                                MenuItem::action(
                                    if icem_platform_windows::console_visible() {
                                        "Скрыть консоль"
                                    } else {
                                        "Показать консоль"
                                    },
                                    ToggleConsole,
                                ),
                                MenuItem::action("Перезапуск", Restart),
                                MenuItem::action("Закрыть", Close),
                            ]
                        })
                        .build(cx)
                    {
                        Ok(tray) => tray,
                        Err(error) => {
                            let _ = ready.send(Err(error.to_string()));
                            cx.quit();
                            return;
                        }
                    };
                    let panel = sender.clone();
                    cx.on_action(move |_: &OpenPanel, _| dispatch(&panel, HostCommand::OpenPanel));
                    let web = sender.clone();
                    cx.on_action(move |_: &OpenWeb, _| dispatch(&web, HostCommand::OpenWeb));
                    let console = sender.clone();
                    cx.on_action(move |_: &ToggleConsole, _| {
                        dispatch(
                            &console,
                            HostCommand::SetConsoleVisible(
                                !icem_platform_windows::console_visible(),
                            ),
                        )
                    });
                    let restart = sender.clone();
                    cx.on_action(move |_: &Restart, _| dispatch(&restart, HostCommand::Restart));
                    let close = sender.clone();
                    cx.on_action(move |_: &Close, _| dispatch(&close, HostCommand::Close));
                    let _ = ready.send(Ok(()));
                    cx.spawn(async move |cx| {
                        loop {
                            cx.background_executor()
                                .timer(Duration::from_millis(50))
                                .await;
                            if stop.load(Ordering::Acquire) {
                                let result = cx.update(|cx| {
                                    let closed = tray.close(cx).map_err(io::Error::other);
                                    cx.quit();
                                    closed
                                });
                                if let Err(error) =
                                    result.unwrap_or_else(|e| Err(io::Error::other(e)))
                                {
                                    *status.borrow_mut() = Err(error);
                                }
                                break;
                            }
                            match cx.update(|cx| tray.refresh_menu(cx)) {
                                Ok(Ok(())) => {}
                                error => {
                                    *status.borrow_mut() = Err(io::Error::other(format!(
                                        "GPUI tray refresh: {error:?}"
                                    )));
                                    let _ = cx.update(|cx| {
                                        let _ = tray.close(cx);
                                        cx.quit();
                                    });
                                    break;
                                }
                            }
                        }
                    })
                    .detach();
                });
                std::mem::replace(&mut *result.borrow_mut(), Ok(()))
            })?;
        match wait.recv_timeout(Duration::from_secs(10)) {
            Ok(Ok(())) => Ok(Self {
                stopping,
                commands,
                worker: Some(worker),
            }),
            result => {
                stopping.store(true, Ordering::Release);
                let _ = worker.join();
                Err(io::Error::other(match result {
                    Ok(Err(error)) => error,
                    _ => "GPUI tray stopped before readiness".into(),
                }))
            }
        }
    }
    pub fn poll(&self) -> io::Result<Option<HostCommand>> {
        match self.commands.try_recv() {
            Ok(command) => Ok(Some(command)),
            Err(mpsc::TryRecvError::Empty) => Ok(None),
            Err(mpsc::TryRecvError::Disconnected) => {
                Err(io::Error::other("GPUI tray foreground stopped"))
            }
        }
    }
    pub fn stop(mut self) -> io::Result<()> {
        self.join()
    }
    fn join(&mut self) -> io::Result<()> {
        self.stopping.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            worker
                .join()
                .map_err(|_| io::Error::other("GPUI tray panicked"))??;
        }
        Ok(())
    }
}
impl Drop for HostTray {
    fn drop(&mut self) {
        if let Err(error) = self.join() {
            eprintln!("GPUI tray shutdown: {error}");
        }
    }
}
fn dispatch(sender: &mpsc::SyncSender<HostCommand>, command: HostCommand) {
    if let Err(error) = sender.try_send(command) {
        eprintln!("Host tray command: {error}");
    }
}
