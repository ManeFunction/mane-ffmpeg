//! A long silent audio file used to fill audio gaps in preview EDLs.

use crate::ffmpeg::FfmpegTools;
use crate::ffmpeg::tools::QUIET;
use crate::model::Time;
use crate::persistence::app_paths;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::sync::Mutex;

pub const LENGTH: Time = Time::from_secs(600);

static GATE: Mutex<()> = Mutex::new(());

pub fn file_path() -> PathBuf {
    app_paths::cache_directory().join("silence-48k-stereo.mka")
}

/// Makes the file if it isn't there yet; blocks.
pub fn ensure(tools: &FfmpegTools) -> Result<PathBuf, String> {
    let path = file_path();
    if path.is_file() {
        return Ok(path);
    }
    let _gate = GATE.lock().unwrap();
    if path.is_file() {
        return Ok(path);
    }
    std::fs::create_dir_all(path.parent().unwrap()).map_err(|e| e.to_string())?;
    let temp = path.with_extension("tmp.mka");
    let mut args: Vec<String> = QUIET.iter().map(|s| s.to_string()).collect();
    args.extend(
        ["-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", &(LENGTH.total_seconds() as i64).to_string(), "-c:a", "flac"]
            .map(String::from),
    );
    args.push(temp.to_string_lossy().into_owned());
    let status = Command::new(&tools.ffmpeg).args(&args).stdin(Stdio::null()).status().map_err(|e| e.to_string())?;
    if !status.success() {
        return Err(format!("ffmpeg couldn't make the silence file (exit {})", status.code().unwrap_or(-1)));
    }
    std::fs::rename(&temp, &path).map_err(|e| e.to_string())?;
    Ok(path)
}
