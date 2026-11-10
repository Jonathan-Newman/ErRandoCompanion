//! A small log beside the mod, for problems the overlay cannot show (it may never get to draw).

use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};

static PATH: OnceLock<PathBuf> = OnceLock::new();
/// One writer at a time, so lines from different threads never interleave.
static WRITING: Mutex<()> = Mutex::new(());

pub fn set_directory(directory: &Path) {
    let _ = PATH.set(directory.join("ErRandoCompanionMod.log"));
}

pub fn write(message: &str) {
    let Some(path) = PATH.get() else { return };
    let _one_at_a_time = WRITING.lock().unwrap_or_else(|p| p.into_inner());
    if let Ok(mut file) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
        let seconds = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);
        // One write call per line.
        let _ = file.write_all(format!("[{seconds}] {message}\n").as_bytes());
    }
}
