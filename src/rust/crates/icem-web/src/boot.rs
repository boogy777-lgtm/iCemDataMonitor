//! Process boot recorder for the original SubscribeBootStages contract.
//! Snapshot + broadcast subscription are captured under one lock, so replay has no gap.
use crate::proto::BootStage;
use std::sync::{Arc, Mutex};
use tokio::sync::broadcast;
const MAX_STAGES: usize = 64;
const LIVE_CAPACITY: usize = 128;
struct Inner {
    stages: Vec<BootStage>,
    updates: broadcast::Sender<BootStage>,
}
#[derive(Clone)]
pub struct BootStages(Arc<Mutex<Inner>>);
impl Default for BootStages {
    fn default() -> Self {
        let (updates, _) = broadcast::channel(LIVE_CAPACITY);
        Self(Arc::new(Mutex::new(Inner {
            stages: Vec::new(),
            updates,
        })))
    }
}
impl BootStages {
    pub fn begin(&self, id: &str, title: &str) -> Result<(), String> {
        if id.trim().is_empty() {
            return Err("boot stage id is empty".into());
        }
        let mut inner = self.0.lock().map_err(|_| "boot recorder poisoned")?;
        let index = match inner.stages.iter().position(|s| s.id == id) {
            Some(index) => index,
            None => {
                if inner.stages.len() == MAX_STAGES {
                    return Err("boot stage capacity exceeded".into());
                }
                let ordinal = inner.stages.len() as u32 + 1;
                inner.stages.push(BootStage {
                    ordinal,
                    id: id.into(),
                    ..Default::default()
                });
                inner.stages.len() - 1
            }
        };
        let stage = &mut inner.stages[index];
        stage.title = title.into();
        stage.completed = false;
        let stage = stage.clone();
        let _ = inner.updates.send(stage);
        Ok(())
    }
    pub fn complete(&self, id: &str, detail: Option<&str>) -> Result<(), String> {
        let mut inner = self.0.lock().map_err(|_| "boot recorder poisoned")?;
        let stage = inner
            .stages
            .iter_mut()
            .find(|s| s.id == id)
            .ok_or_else(|| format!("boot stage '{id}' was never begun"))?;
        stage.completed = true;
        if let Some(detail) = detail {
            stage.detail = detail.into();
        }
        let stage = stage.clone();
        let _ = inner.updates.send(stage);
        Ok(())
    }
    pub fn snapshot(&self) -> Result<Vec<BootStage>, String> {
        self.0
            .lock()
            .map(|i| i.stages.clone())
            .map_err(|_| "boot recorder poisoned".into())
    }
    pub(crate) fn subscribe(
        &self,
    ) -> Result<(Vec<BootStage>, broadcast::Receiver<BootStage>), String> {
        let inner = self.0.lock().map_err(|_| "boot recorder poisoned")?;
        Ok((inner.stages.clone(), inner.updates.subscribe()))
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn subscription_replays_then_delivers_every_begin_complete_without_gap()
    -> Result<(), String> {
        let b = BootStages::default();
        b.begin("ini", "конфигурация")?;
        b.complete("ini", None)?;
        let (snapshot, mut updates) = b.subscribe()?;
        assert_eq!(snapshot.len(), 1);
        assert!(snapshot[0].completed);
        b.begin("web", "веб-сервер")?;
        b.complete("web", Some("bound"))?;
        let first = updates.recv().await.map_err(|e| e.to_string())?;
        let second = updates.recv().await.map_err(|e| e.to_string())?;
        assert!(!first.completed);
        assert!(second.completed);
        assert_eq!(first.ordinal, second.ordinal);
        assert_eq!(second.detail, "bound");
        Ok(())
    }
    #[test]
    fn lag_is_explicit_instead_of_silent_progress_loss() -> Result<(), String> {
        let b = BootStages::default();
        let (_, mut updates) = b.subscribe()?;
        for _ in 0..129 {
            b.begin("retry", "повтор")?;
        }
        assert!(matches!(
            updates.try_recv(),
            Err(broadcast::error::TryRecvError::Lagged(_))
        ));
        Ok(())
    }
}
