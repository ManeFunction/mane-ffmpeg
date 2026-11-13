//! A small append-only log for diagnosing problems on users' machines
//! (macOS: ~/Library/Logs/Mane FFmpeg/app.log, Linux: ~/.cache/mane-ffmpeg/app.log).

use super::app_paths;
use std::io::Write;
use std::path::PathBuf;
use std::sync::Mutex;

const MAX_BYTES: u64 = 1024 * 1024;
static GATE: Mutex<()> = Mutex::new(());

pub fn file_path() -> PathBuf {
    if cfg!(target_os = "macos") && app_paths::root_override().is_none() {
        app_paths::home().join("Library").join("Logs").join(app_paths::APP_NAME).join("app.log")
    } else {
        app_paths::cache_directory().join("app.log")
    }
}

/// Local time as "yyyy-MM-dd HH:mm:ss.fff".
pub fn timestamp() -> String {
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    let secs = now.as_secs() as libc::time_t;
    let mut tm: libc::tm = unsafe { std::mem::zeroed() };
    // SAFETY: localtime_r writes into tm.
    unsafe { libc::localtime_r(&secs, &mut tm) };
    format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03}",
        tm.tm_year + 1900,
        tm.tm_mon + 1,
        tm.tm_mday,
        tm.tm_hour,
        tm.tm_min,
        tm.tm_sec,
        now.subsec_millis()
    )
}

pub fn write(message: &str) {
    // Logging must never break the app.
    let _gate = GATE.lock();
    let path = file_path();
    let _ = (|| -> std::io::Result<()> {
        std::fs::create_dir_all(path.parent().unwrap())?;
        if std::fs::metadata(&path).map(|m| m.len() > MAX_BYTES).unwrap_or(false) {
            std::fs::rename(&path, path.with_extension("log.1"))?;
        }
        let mut f = std::fs::OpenOptions::new().create(true).append(true).open(&path)?;
        writeln!(f, "{} {}", timestamp(), message)
    })();
    if cfg!(debug_assertions) {
        eprintln!("[log] {message}");
    }
}
