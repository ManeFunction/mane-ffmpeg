//! Finds command-line tools (ffmpeg, notify-send…) the way a shell would, plus the usual install folders.

use std::collections::HashSet;
use std::path::{Path, PathBuf};

/// Directories to search. GUI apps on macOS start with a minimal PATH, so the usual Homebrew/MacPorts/distro
/// locations are always checked as well.
pub fn search_directories() -> Vec<PathBuf> {
    let path = std::env::var("PATH").unwrap_or_default();
    let home = crate::persistence::app_paths::home();
    let mut seen = HashSet::new();
    path.split(':')
        .filter(|d| !d.is_empty())
        .map(PathBuf::from)
        .chain(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/opt/local/bin", "/snap/bin"].iter().map(PathBuf::from))
        .chain([home.join(".local").join("bin"), home.join("bin")])
        .filter(|d| seen.insert(d.clone()))
        .collect()
}

/// `preferred` when given (and it exists), else the first match in [`search_directories`].
pub fn find(name: &str, preferred: Option<&str>) -> Option<String> {
    if let Some(p) = preferred.filter(|p| !p.trim().is_empty()) {
        return Path::new(p).is_file().then(|| p.to_string());
    }
    search_directories().into_iter().map(|d| d.join(name)).find(|c| c.is_file()).map(|c| c.to_string_lossy().into_owned())
}
