//! Per-user folders, following each platform's conventions.

use std::path::{Path, PathBuf};
use std::sync::RwLock;

pub const APP_NAME: &str = "Mane FFmpeg";
const LINUX_NAME: &str = "mane-ffmpeg";

static ROOT_OVERRIDE: RwLock<Option<PathBuf>> = RwLock::new(None);

/// Overrides both folders (tests use a temp directory).
pub fn set_root_override(root: Option<PathBuf>) {
    *ROOT_OVERRIDE.write().unwrap() = root;
}

pub fn root_override() -> Option<PathBuf> {
    ROOT_OVERRIDE.read().unwrap().clone()
}

pub fn home() -> PathBuf {
    std::env::var_os("HOME").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("/"))
}

pub fn config_directory() -> PathBuf {
    if let Some(root) = root_override() {
        root.join("config")
    } else if cfg!(target_os = "macos") {
        home().join("Library").join("Application Support").join(APP_NAME)
    } else {
        xdg("XDG_CONFIG_HOME", ".config").join(LINUX_NAME)
    }
}

pub fn cache_directory() -> PathBuf {
    if let Some(root) = root_override() {
        root.join("cache")
    } else if cfg!(target_os = "macos") {
        home().join("Library").join("Caches").join(APP_NAME)
    } else {
        xdg("XDG_CACHE_HOME", ".cache").join(LINUX_NAME)
    }
}

pub fn temp_directory() -> PathBuf {
    std::env::temp_dir().join(LINUX_NAME)
}

/// An XDG base directory: the variable when it holds an absolute path, else ~/`fallback`.
pub fn xdg(variable: &str, fallback: &str) -> PathBuf {
    match std::env::var(variable) {
        Ok(v) if !v.is_empty() && Path::new(&v).is_absolute() => PathBuf::from(v),
        _ => home().join(fallback),
    }
}
