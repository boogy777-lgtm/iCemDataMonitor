//! One bounded ensure policy for Shell and CLI; the Host owns the existing singleton.
#[cfg(windows)]
use crate::{
    client,
    proto::{BootStreamRequest, HostStatusRequest},
};
use std::{
    future::Future,
    path::PathBuf,
    process::Child,
    time::{Duration, Instant},
};

pub const READY_TIMEOUT: Duration = Duration::from_secs(30);
pub const ALREADY_RUNNING: u8 = 73;
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Presence {
    Absent,
    Starting,
    Ready,
}
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum StartPolicy {
    IfAbsent,
    RequireRunning,
    Wait,
}
#[derive(Debug, Clone)]
pub struct Launch {
    pub executable: PathBuf,
    pub arguments: Vec<String>,
}
impl Launch {
    pub fn installed() -> Result<Self, String> {
        Ok(Self {
            executable: std::env::current_exe()
                .map_err(|e| e.to_string())?
                .with_file_name("icem-host-dev.exe"),
            arguments: vec!["--autostart".into()],
        })
    }
    #[cfg(windows)]
    fn spawn(&self) -> Result<Option<Child>, String> {
        use std::{
            os::windows::process::CommandExt,
            process::{Command, Stdio},
        };
        let directory = self.executable.parent().ok_or("Не найден каталог Host")?;
        Command::new(&self.executable)
            .args(&self.arguments)
            .current_dir(directory)
            .creation_flags(0x0800_0000)
            // The persistent Host must not keep a CLI caller's redirected pipes
            // open after the JSON command exits. Startup errors return via ensure.
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .map(Some)
            .map_err(|e| {
                format!(
                    "Не удалось запустить {}: {e}. Повторите установку и выберите Repair.",
                    self.executable.display()
                )
            })
    }
}
/// GetStatus is authoritative for presence. A serving-but-not-ready owner is never respawned.
#[cfg(windows)]
pub async fn probe(pipe: &str) -> Presence {
    let Ok(mut rpc) = client::connect(pipe).await else {
        return Presence::Absent;
    };
    if rpc.get_status(HostStatusRequest {}).await.is_err() {
        return Presence::Absent;
    }
    let Ok(reply) = rpc.subscribe_boot_stages(BootStreamRequest {}).await else {
        return Presence::Starting;
    };
    let mut stream = reply.into_inner();
    // A serving Host must not become Absent merely because READY replay is delayed.
    let ready = tokio::time::timeout(Duration::from_millis(250), async {
        for _ in 0..128 {
            match stream.message().await {
                Ok(Some(stage)) if stage.id == "ready" && stage.completed => return true,
                Ok(Some(_)) => {}
                _ => return false,
            }
        }
        false
    })
    .await
    .unwrap_or(false);
    if ready {
        Presence::Ready
    } else {
        Presence::Starting
    }
}
#[cfg(windows)]
pub async fn ensure(pipe: &str, launch: Option<&Launch>, budget: Duration) -> Result<(), String> {
    ensure_with(
        || probe(pipe),
        || launch.map_or(Ok(None), Launch::spawn),
        if launch.is_some() {
            StartPolicy::IfAbsent
        } else {
            StartPolicy::RequireRunning
        },
        budget,
    )
    .await
}
/// The seam injects only read probes and the single spawn; no alternate singleton or retry owner.
pub async fn ensure_with<P, F, S>(
    mut probe: P,
    spawn: S,
    policy: StartPolicy,
    budget: Duration,
) -> Result<(), String>
where
    P: FnMut() -> F,
    F: Future<Output = Presence>,
    S: FnOnce() -> Result<Option<Child>, String>,
{
    if budget.is_zero() || budget > READY_TIMEOUT {
        return Err("Недопустимый срок ожидания Host".into());
    }
    let deadline = Instant::now() + budget;
    let mut child = None;
    let mut spawn = Some(spawn);
    let mut first = true;
    loop {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err("Host не готов за отведённое время. Проверьте конфигурацию и конфликт портов; автоматический повтор запуска не выполнялся.".into());
        }
        let presence = tokio::time::timeout(remaining.min(Duration::from_secs(1)), probe())
            .await
            .unwrap_or(Presence::Absent);
        if Instant::now() >= deadline {
            return Err("Истёк срок ожидания Host; запуск после deadline не выполняется".into());
        }
        if presence == Presence::Ready {
            return Ok(());
        }
        if first && presence == Presence::Absent {
            if policy == StartPolicy::RequireRunning {
                return Err(
                    "Host не запущен. Повторите команду с --start или откройте приложение.".into(),
                );
            }
            if policy == StartPolicy::IfAbsent {
                child = spawn.take().ok_or("попытка запуска уже использована")?()?;
            }
        }
        first = false;
        if let Some(process) = &mut child
            && let Some(status) = process.try_wait().map_err(|e| e.to_string())?
        {
            if status.code() != Some(i32::from(ALREADY_RUNNING)) {
                return Err(format!(
                    "Host завершился до готовности ({status}). Проверьте config.ini и занятые порты. Подробности: запустите icem-host-dev.exe в консоли."
                ));
            }
            // Another simultaneous caller won the existing Host mutex. Wait for its pipe.
            child = None;
        }
        tokio::time::sleep(
            deadline
                .saturating_duration_since(Instant::now())
                .min(Duration::from_millis(100)),
        )
        .await;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;
    fn runtime() -> std::io::Result<tokio::runtime::Runtime> {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
    }
    #[test]
    fn running_or_starting_owner_never_spawns_and_absence_spawns_once() -> Result<(), String> {
        let runtime = runtime().map_err(|e| e.to_string())?;
        for initial in [Presence::Absent, Presence::Starting, Presence::Ready] {
            let probes = Cell::new(0);
            let spawns = Cell::new(0);
            runtime.block_on(ensure_with(
                || {
                    let n = probes.get();
                    probes.set(n + 1);
                    std::future::ready(if n == 0 { initial } else { Presence::Ready })
                },
                || {
                    spawns.set(spawns.get() + 1);
                    Ok(None)
                },
                StartPolicy::IfAbsent,
                Duration::from_secs(1),
            ))?;
            assert_eq!(spawns.get(), usize::from(initial == Presence::Absent));
        }
        Ok(())
    }
    #[test]
    fn failures_and_hung_probe_are_bounded_without_spawn_retries() -> Result<(), String> {
        let runtime = runtime().map_err(|e| e.to_string())?;
        let spawns = Cell::new(0);
        let result = runtime.block_on(ensure_with(
            || std::future::ready(Presence::Absent),
            || {
                spawns.set(spawns.get() + 1);
                Ok(None)
            },
            StartPolicy::IfAbsent,
            Duration::from_millis(20),
        ));
        assert!(result.is_err());
        assert_eq!(spawns.get(), 1);
        let before = Instant::now();
        assert!(
            runtime
                .block_on(ensure_with(
                    || std::future::pending(),
                    || Err("must not spawn".into()),
                    StartPolicy::RequireRunning,
                    Duration::from_millis(20)
                ))
                .is_err()
        );
        assert!(before.elapsed() < Duration::from_secs(2));
        assert_eq!(
            runtime
                .block_on(ensure_with(
                    || std::future::ready(Presence::Absent),
                    || Err("spawn denied".into()),
                    StartPolicy::IfAbsent,
                    Duration::from_secs(1)
                ))
                .err()
                .as_deref(),
            Some("spawn denied")
        );
        Ok(())
    }
}
