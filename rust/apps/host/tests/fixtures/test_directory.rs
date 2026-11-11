//! Test-owned directories; not a defense against hostile replacement/reparse races.
use std::{
    fs, io,
    path::{Path, PathBuf},
};

pub(super) struct TestDirectory(PathBuf);
impl TestDirectory {
    fn create(path: PathBuf) -> io::Result<Self> {
        fs::create_dir(&path)?;
        Ok(Self(path))
    }

    pub(super) fn for_host(path: PathBuf) -> io::Result<Self> {
        let root = Self::create(path)?;
        fs::create_dir(root.path().join("programdata"))?;
        fs::create_dir(root.path().join("localappdata"))?;
        Ok(root)
    }

    pub(super) fn path(&self) -> &Path {
        &self.0
    }
}
impl Drop for TestDirectory {
    fn drop(&mut self) {
        if let Err(error) = fs::remove_dir_all(&self.0) {
            eprintln!(
                "test-owned directory cleanup failed for {}: {error}",
                self.0.display()
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    fn parent() -> io::Result<TestDirectory> {
        static NEXT: AtomicUsize = AtomicUsize::new(0);
        TestDirectory::create(std::env::temp_dir().join(format!(
            "icem-owned-test-directory-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        )))
    }

    #[test]
    fn failed_create_preserves_existing_directory_and_other_failure_inputs()
    -> Result<(), Box<dyn std::error::Error>> {
        let root = parent()?;
        let existing = root.path().join("existing");
        fs::create_dir(&existing)?;
        let sentinel = existing.join("sentinel.txt");
        fs::write(&sentinel, b"preserve existing contents")?;
        let nested = existing.join("nested");
        fs::create_dir(&nested)?;
        let nested_sentinel = nested.join("sentinel.txt");
        fs::write(&nested_sentinel, b"preserve nested contents")?;
        let error = match TestDirectory::for_host(existing) {
            Ok(_) => return Err("unexpected ownership of an existing directory".into()),
            Err(error) => error,
        };
        assert_eq!(error.kind(), io::ErrorKind::AlreadyExists);
        assert_eq!(fs::read(&sentinel)?, b"preserve existing contents");
        assert_eq!(fs::read(&nested_sentinel)?, b"preserve nested contents");
        let regular = root.path().join("regular-file");
        fs::write(&regular, b"preserve parent file")?;
        assert!(TestDirectory::for_host(regular.join("child")).is_err());
        assert_eq!(fs::read(&regular)?, b"preserve parent file");
        assert_eq!(fs::read(&sentinel)?, b"preserve existing contents");
        assert_eq!(fs::read(&nested_sentinel)?, b"preserve nested contents");
        Ok(())
    }

    #[test]
    fn successful_create_is_cleaned_without_touching_a_sibling()
    -> Result<(), Box<dyn std::error::Error>> {
        let root = parent()?;
        let sibling = root.path().join("sibling.txt");
        fs::write(&sibling, b"preserve sibling")?;
        let child = root.path().join("created");
        {
            let owned = TestDirectory::for_host(child.clone())?;
            assert_eq!(owned.path(), child);
            assert!(owned.path().join("programdata").is_dir());
            assert!(owned.path().join("localappdata").is_dir());
            fs::write(owned.path().join("owned.txt"), b"owned")?;
        }
        assert!(!child.exists());
        assert_eq!(fs::read(sibling)?, b"preserve sibling");
        Ok(())
    }
}
