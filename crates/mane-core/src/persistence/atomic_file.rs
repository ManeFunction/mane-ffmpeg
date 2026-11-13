//! Writes a file through a temporary sibling and a rename, so a crash never leaves it half written.

use std::path::Path;

pub fn write_all_text(path: &Path, contents: &str) -> std::io::Result<()> {
    let full = std::path::absolute(path)?;
    if let Some(dir) = full.parent() {
        std::fs::create_dir_all(dir)?;
    }
    let mut temp = full.clone().into_os_string();
    temp.push(".tmp");
    std::fs::write(&temp, contents)?;
    std::fs::rename(&temp, &full)
}
